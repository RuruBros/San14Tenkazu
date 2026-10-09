# The AI's behaviour trees, read-only. The game reads them once, when it starts, from 0000/bt_exce.bin and keeps them on
# the heap; every AI force runs the same four trees. What the output means is written up in notes/ai.md.
#   python ai.py file                     the four trees in the game's file: node counts, classes used, log strings
#   python ai.py classes                  node classes: hash -> name and where each keeps its parameters (exe image)
#   python ai.py preds                    condition predicates: id -> function and how often the trees ask each
#   python ai.py tree <t> [node] [depth]  tree t (0 force, 1 corps, 2 troops, 3 unit) as text, from a node (root by default)
#   python ai.py path <t> <node>          what decides whether a node runs: its ancestors, conditions spelled out
#   python ai.py find <class|number|var>  nodes of a class, nodes holding a number, or nodes using a variable
#   python ai.py live                     the trees in the running game, compared node by node with the file
#   python ai.py watch [seconds]          every unit's order, target kind and target whenever they change (running game)
#   python ai.py varnames                 recover variable names from their hashes into ai_vars.txt
#   python ai.py tunables [t]             every number a designer could tune: predicate number args, numeric leaf params
import json, os, re, struct, sys, time
from collections import Counter, defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, ".cache")
VARS = os.path.join(HERE, "ai_vars.txt")
PREDS = os.path.join(HERE, "ai_preds.tsv")
LEAVES = os.path.join(HERE, "ai_leaves.tsv")
INFO = os.path.join(HERE, "ai_info.tsv")
STEAM_DIR = r"C:\Program Files (x86)\Steam\steamapps\common\Romance_of_the_Three_Kingdoms_14"
FILE = os.path.join("0000", "bt_exce.bin")   # the only one the CE exe names (0xC0543); bt.bin / bt_ex.bin are older editions

AI_MANAGER = 0x1B9A4A0    # CAIManager*: +8 CJuicyBehaviorTreeManager, +0x10 CForceAI, +0x18 CDistrictAI, +0x20 CTroopsAI, +0x28 CArmyAI
TREES = ["force", "corps", "troops", "unit"]   # manager +0x10.. in file order: CForceAI, CDistrictAI, CTroopsAI, CArmyAI
LEAF_FACTORY = 0x1AE10    # CJuicyBehaviorTreeManager vtable slot 4: switch on the class hash, then vtable +0x88 loads the params
PREDICATE_TABLE, PREDICATE_COUNT = 0xC38A0, 0xA7   # jump table of the predicate dispatcher 0xC2610 (ids 1..167)

# ktgl's own node classes: the cases of the KTBT reader 0xB28120 (hash -> constructor); names come from vtable slot 12
COMPOSITE_CTORS = {0xF3836309: 0xB27170, 0x416D418C: 0xB2BA70, 0x0908082C: 0xB2B7F0, 0x2252B2CB: 0xB2EAF0,
                   0x313FB307: 0xB2C570, 0x61BD23A4: 0xB2C7C0, 0x7240A566: 0xB2E7F0, 0xD3178794: 0xB2D5C0,
                   0x9FB4C384: 0xB2CAF0, 0xB0445F52: 0xB300E0, 0xBD1BADA3: 0xB2E260, 0xFBA44708: 0xB2E5A0,
                   0x9A4DB939: 0xB2E640}
ROOT, SEQUENTIAL, CONDITION, STATIC_LINK = 0xF3836309, 0x416D418C, 0x2252B2CB, 0xBD1BADA3
TERM_KINDS = {0: "AND", 1: "OR"}             # condition terms: kind 0/1 open a group of the next n terms, kind 2 is a predicate
CONDITION_MODES = {0: "if", 1: "while", 2: "until"}                  # +0x2A: 0xB2F990, 0xB2FC80, 0xB2FB10
SEQUENTIAL_RULES = {0: "Selector", 1: "Sequence", 2: "All"}          # +0x2C: stop at a success / at a failure / never
SEQUENTIAL_ORDERS = {0: "", 1: "by priority", 2: "shuffled"}         # +0x28: 0xB2C360 sorts, 0xB2C460 shuffles

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")


def u32(b, o): return struct.unpack_from("<I", b, o)[0]
def i32(b, o): return struct.unpack_from("<i", b, o)[0]


def name_hash(text):
    # The game's hash of a variable or class name (0xB263C0): sum of each signed byte times 31^(position+1).
    r, p = 0, 1
    for ch in text.encode():
        p = p * 31 & 0xFFFFFFFF
        r = r + (ch - 256 if ch > 127 else ch) * p & 0xFFFFFFFF
    return r


# --- the file

class Node:
    __slots__ = ("tree", "i", "flags", "cls", "extra", "parent", "nchild", "params", "text", "array", "children")

    def __init__(self, tree, i, flags, cls, extra, parent, nchild, params, text, array):
        self.tree, self.i, self.flags, self.cls, self.extra = tree, i, flags, cls, extra
        self.parent, self.nchild, self.params, self.text, self.array = parent, nchild, params, text, array
        self.children = []

    @property
    def leaf(self): return bool(self.flags & 1)


def game_dir():
    try:
        import ctypes
        from game import Game, k32
        g = Game()
        buf = ctypes.create_unicode_buffer(1024); n = ctypes.c_ulong(1024)
        if k32.QueryFullProcessImageNameW(ctypes.c_void_p(g.h), 0, buf, ctypes.byref(n)):
            return os.path.dirname(buf.value)
    except SystemExit:
        pass
    return STEAM_DIR


def read_file(path=None):
    d = open(path or os.path.join(game_dir(), FILE), "rb").read()
    if d[:4] != b"JBTP": raise SystemExit("bt 파일 형식이 아닙니다")
    trees = []
    for c in range(u32(d, 8)):
        off, size, h = struct.unpack_from("<III", d, 0x10 + c * 12)
        if d[off:off + 8] != b"TB1G0000": raise SystemExit(f"chunk {c}: TB1G 아님")
        p, end = off + u32(d, off + 12), off + size        # TB1G header, then blocks {magic8, size4, 0}
        while p < end:
            magic, bsize = d[p:p + 8], u32(d, p + 8)
            if magic == b"ITBG0000":
                trees.append(parse_ktbt(d, p + 0x10, len(trees)))
            if bsize == 0: break
            p += bsize
    return trees


def parse_ktbt(d, q, t):
    if d[q:q + 4] != b"KTBT": raise SystemExit("KTBT 아님")
    n = u32(d, q + 16); q += 20
    nodes = []
    for k in range(n):
        flags, cls = u32(d, q), u32(d, q + 4); q += 8
        extra = None
        if flags & 4: extra = u32(d, q); q += 4
        parent, nchild, cnt = struct.unpack_from("<iii", d, q); q += 12
        params = list(struct.unpack_from(f"<{cnt}i", d, q)); q += 4 * cnt
        text = array = None
        if flags & 2:
            ln = u32(d, q); text = d[q + 4:q + 4 + ln].decode("cp932", "replace"); q += 4 + ln
        if flags & 8:
            an = u32(d, q); array = list(struct.unpack_from(f"<{an}i", d, q + 4)); q += 4 + 4 * an
        nodes.append(Node(t, k, flags, cls, extra, parent, nchild, params, text, array))
    for x in nodes:
        if x.parent >= 0: nodes[x.parent].children.append(x)
    return nodes


def terms(params):
    # A condition's params: [flag 0x28, flag 0x29, mode 0x2A, n, then n terms of (kind, not, fn, group, argc, args...)]
    out, q = [], 4
    for _ in range(params[3]):
        kind, neg, fn, group, argc = params[q:q + 5]
        out.append((kind, neg, fn, group, params[q + 5:q + 5 + argc])); q += 5 + argc
    return out


# --- names: classes from the exe image, variables from ai_vars.txt, predicates from ai_preds.tsv

def _rip(i):
    return i.address + i.size + int(i.op_str.split("rip ")[1].split("]")[0].replace(" ", ""), 0)


def _name_slot(im, v, s):
    f = im.u64(v + s * 8) - im.base
    if not 0 < f < len(im.img): return None
    ins = list(im.md.disasm(im.img[f:f + 16], f))[:2]
    if len(ins) == 2 and ins[0].mnemonic == "lea" and "rip" in ins[0].op_str and ins[1].mnemonic == "ret":
        t = _rip(ins[0]); return im.img[t:t + 100].split(b"\0")[0].decode("ascii", "replace").strip()
    return None


def _vtable(im, ctor):
    # the vtable the constructor stores last into [this] (a base constructor stores its own first)
    f = im.func(ctor); last = None; regs = {}
    for i in im.insns(*f):
        m = re.match(r"(\w+), \[rip", i.op_str)
        if i.mnemonic == "lea" and m: regs[m.group(1)] = _rip(i); continue
        m = re.match(r"qword ptr \[(\w+)\], (\w+)$", i.op_str)
        if i.mnemonic == "mov" and m and m.group(2) in regs: last = regs[m.group(2)]
        if i.mnemonic == "call": regs = {}
    return last


def _loader_offsets(im, f):
    # params[k] -> node offset, read from a loader made only of "mov eax,[rdx+4k]; mov [rcx+X],eax"
    out, held = {}, {}
    for i in im.md.disasm(im.img[f:f + 0x200], f):
        if i.mnemonic == "ret": return out
        m = re.match(r"(\w+), dword ptr \[rdx(?: \+ (0x[0-9a-f]+|\d+))?\]$", i.op_str)
        if i.mnemonic == "mov" and m: held[m.group(1)] = int(m.group(2) or "0", 0) // 4; continue
        m = re.match(r"dword ptr \[rcx \+ (0x[0-9a-f]+)\], (\w+)$", i.op_str)
        if i.mnemonic == "mov" and m and m.group(2) in held: out[held[m.group(2)]] = int(m.group(1), 16); continue
        return None
    return None


def classes():
    # hash -> {name, kind, vt, exec, offsets}; cached per image dump
    from image import Image, IMAGE
    cache = os.path.join(CACHE, "ai_classes.json")
    if os.path.exists(cache) and os.path.getmtime(cache) > os.path.getmtime(IMAGE):
        out = {int(k, 16): v for k, v in json.load(open(cache)).items()}
        for v in out.values():
            if v["offsets"]: v["offsets"] = {int(k): o for k, o in v["offsets"].items()}
        return _custom_loaders(out)
    im = Image()
    b, e = im.func(LEAF_FACTORY)
    ins = list(im.insns(b, e)); idx = {i.address: k for k, i in enumerate(ins)}
    cases = {}
    for k, i in enumerate(ins):
        if i.mnemonic == "cmp" and i.op_str.startswith("edx, 0x"):
            h = int(i.op_str.split(", ")[1], 16); n = ins[k + 1]
            if n.mnemonic == "ja" and ins[k + 2].mnemonic == "je": n = ins[k + 2]
            if n.mnemonic == "je": cases[h] = int(n.op_str, 16)
            elif n.mnemonic == "jne": cases[h] = ins[k + 2].address
    out = {}
    for h, t in cases.items():
        ctor = next(int(i.op_str, 16) for i in ins[idx[t]:idx[t] + 8] if i.mnemonic == "call" and i.op_str != "0xb270e0")
        v = _vtable(im, ctor)
        leafbase = _name_slot(im, v, 12) == "CBehaviorTreeLeafNode"
        name = _name_slot(im, v, 13) if leafbase else _name_slot(im, v, 12) or "CLoopListElementNode"
        load = im.u64(v + 17 * 8) - im.base if leafbase else None
        out[h] = {"name": name, "kind": "leaf", "vt": v, "ctor": ctor, "exec": im.u64(v + 16 * 8) - im.base,
                  "load": load, "offsets": _loader_offsets(im, load) if load else None}
    for h, ctor in COMPOSITE_CTORS.items():
        v = _vtable(im, ctor)
        out[h] = {"name": _name_slot(im, v, 12), "kind": "composite", "vt": v, "ctor": ctor,
                  "exec": im.u64(v + 16 * 8) - im.base, "load": None, "offsets": None}
    os.makedirs(CACHE, exist_ok=True)
    json.dump({f"{k:08x}": v for k, v in out.items()}, open(cache, "w"), indent=0)
    return _custom_loaders(out)


def _custom_loaders(classes):
    # Leaf factory 0x1C3F0 calls this composite's loader directly, not via the leaf's vtable slot 17.
    # Native loader and +0x48 AgentType cache verified by ai_node_state.py; +0x44 duplicates param 1.
    loop = classes.get(0x78F9C619)
    if loop and loop["name"] == "CLoopListElementNode" and loop["vt"] == 0x134A3F8:
        loop["load"] = 0x5CAD0
        loop["offsets"] = {0: 0x28, 1: 0x2C, 2: 0x30, 3: 0x40}
    return classes


def short(name):
    for p in ("CBehaviorTree",):
        if name.startswith(p): name = name[len(p):]
    if name.startswith("C") and name[1:2].isupper(): name = name[1:]
    return name[:-4] if name.endswith("Node") else name


def var_names():
    out = {}
    if os.path.exists(VARS):
        for line in open(VARS, encoding="utf-8"):
            p = line.split()
            if len(p) >= 2 and not line.startswith("#"): out[int(p[0], 16)] = p[1]
    return out


def pred_names():
    # ai_preds.tsv: id, name, args (v int variable, o object variable, n number, ...), meaning
    out = {}
    if os.path.exists(PREDS):
        for line in open(PREDS, encoding="utf-8"):
            if line.startswith("#") or not line.strip(): continue
            p = line.rstrip("\n").split("\t")
            out[int(p[0])] = (p[1], p[2] if len(p) > 2 else "")
    return out


def leaf_roles():
    # ai_leaves.tsv: class, param index or "node", role (in-int, in-obj, out-int, out-obj, number, number:tunable, enum:..),
    # meaning, Korean label, confidence -> {class: {index or "node": (role, meaning, label)}}
    out = defaultdict(dict)
    if os.path.exists(LEAVES):
        for line in open(LEAVES, encoding="utf-8"):
            if line.startswith("#") or not line.strip(): continue
            p = line.rstrip("\n").split("\t") + ["", "", "", ""]
            key = p[1] if p[1] == "node" else int(p[1])
            out[p[0].strip()][key] = (p[2], p[3], p[4])
    return out


def info_kinds():
    # ai_info.tsv: class, kind (or "params"), output type, meaning, Korean label, uses, confidence -> {class: {kind: label}}
    out = defaultdict(dict)
    if os.path.exists(INFO):
        for line in open(INFO, encoding="utf-8"):
            if line.startswith("#") or not line.strip(): continue
            p = line.rstrip("\n").split("\t") + ["", "", "", ""]
            if p[1].isdigit(): out[p[0].strip()][int(p[1])] = p[4]
    return out


def kind_param(name):
    # the info nodes without a source object (CGetMyself*Info, CVariableGetGameData) take the kind first
    return 0 if name.startswith("CGetMyself") or name.startswith("CVariableGetGameData") else 1


class Names:
    def __init__(self):
        self.cls = classes(); self.var = var_names(); self.pred = pred_names(); self.roles = leaf_roles()
        self.info = info_kinds()

    def node(self, x):
        c = self.cls.get(x.cls)
        return short(c["name"]) if c else f"?{x.cls:08x}"

    def value(self, v, role=""):
        # role from the tables: "v"/"o"/"l" or in-*/out-*/inout-* = a variable, "n"/number/enum = a literal;
        # without one, a value this large is taken for a variable's hash
        n = self.var.get(v & 0xFFFFFFFF)
        var = role[:1] in ("v", "o", "l") or role.startswith(("in", "out")) if role else None
        if var is None: var = n is not None or abs(v) >= 0x100000
        if var and v: return "$" + n if n else f"${v & 0xFFFFFFFF:08x}"
        return f"{v:,}"

    def term(self, t):
        kind, neg, fn, group, args = t
        name, sig = self.pred.get(fn, (f"P{fn}", ""))
        sig = sig.split()
        s = f"{name}({', '.join(self.value(a, sig[i] if i < len(sig) else '') for i, a in enumerate(args))})"
        return ("not " if neg else "") + s

    def condition(self, params):
        # spelled out as the evaluator 0xB2F0D0 walks it: a group term takes the next `group` terms
        ts = terms(params)
        out, k = [], 0
        while k < len(ts):
            kind, neg, fn, group, args = ts[k]
            if kind in TERM_KINDS:
                inner = [self.term(t) for t in ts[k + 1:k + 1 + group]]
                out.append("(" + f" {TERM_KINDS[kind]} ".join(inner) + ")"); k += 1 + group
            else:
                out.append(self.term(ts[k])); k += 1
        # top level: +0x29 set = every item must hold, clear = one is enough; +0x28 set turns the answer over
        text = (" AND " if params[1] else " OR ").join(out)
        return f"not ({text})" if params[0] else text

    def line(self, x):
        name = self.node(x)
        if x.cls == CONDITION:
            return f"#{x.i} {CONDITION_MODES.get(x.params[2], x.params[2])} {self.condition(x.params)}"
        if x.cls == SEQUENTIAL:
            order = SEQUENTIAL_ORDERS.get(x.params[0], x.params[0])
            return f"#{x.i} {SEQUENTIAL_RULES.get(x.params[1], x.params[1])}{' ' + order if order else ''}{'' if x.params[2] else ' (a child a tick)'}"
        if x.cls == STATIC_LINK:
            return f"#{x.i} {name} {self.value(x.params[0])}"
        extra = f" {x.text!r}" if x.text else ""
        full = self.cls.get(x.cls, {}).get("name", "")
        roles = self.roles.get(full, {})
        shown = [self.value(p, roles.get(i, ("",))[0]) for i, p in enumerate(x.params)]
        kinds = self.info.get(full)
        k = kind_param(full)
        if kinds and k < len(x.params) and x.params[k] in kinds:
            shown[k] = f"{x.params[k]}<{kinds[x.params[k]]}>"
        return f"#{x.i} {name}({', '.join(shown)}){extra}"


# --- commands

def cmd_file():
    names = Names()
    for t, nodes in enumerate(read_file()):
        cnt = Counter(names.node(x) for x in nodes)
        print(f"tree {t} {TREES[t]}: {len(nodes)} nodes, {len(cnt)} classes")
        print("   " + ", ".join(f"{k} {v}" for k, v in cnt.most_common()))
        for x in nodes:
            if x.text: print(f"   #{x.i} {names.node(x)}: {x.text}")


def cmd_classes():
    cls = classes()
    for h, c in sorted(cls.items(), key=lambda kv: (kv[1]["kind"], kv[1]["name"])):
        offs = c["offsets"]
        where = ("params at " + ",".join(f"{o:#x}" for _, o in sorted(offs.items()))) if offs else ("no params" if offs == {} else "custom loader")
        print(f"{h:08x} {c['kind']:9} {c['name']:48} exec {c['exec']:#x} {where}")


def predicate_functions():
    from image import Image
    im = Image()
    out = {}
    for k in range(PREDICATE_COUNT):
        case = im.i32(PREDICATE_TABLE + 4 * k)
        call = next((int(i.op_str, 16) for i in im.md.disasm(im.img[case:case + 40], case) if i.mnemonic == "call"), None)
        out[k + 1] = call
    return out


def cmd_preds():
    uses = Counter()
    for nodes in read_file():
        for x in nodes:
            if x.cls == CONDITION:
                for kind, neg, fn, group, args in terms(x.params):
                    if kind == 2: uses[fn] += 1
    names = pred_names()
    for k, f in predicate_functions().items():
        n = names.get(k, ("", ""))
        print(f"{k:3} {f or 0:#8x} {uses[k]:5}  {n[0]} {n[1]}")


def cmd_tree(t, start=0, depth=99):
    names = Names()
    nodes = read_file()[t]
    def show(x, d):
        print("  " * d + names.line(x))
        if d < depth:
            for y in x.children: show(y, d + 1)
    show(nodes[start], 0)


def cmd_path(t, i):
    names = Names()
    nodes = read_file()[t]
    chain = []
    x = nodes[i]
    while x.parent >= 0:
        chain.append(x); x = nodes[x.parent]
    for d, y in enumerate(reversed(chain)):
        if y.cls in (CONDITION, STATIC_LINK) or y is chain[0]: print("  " * d + names.line(y))


def cmd_find(what):
    names = Names()
    rev = {v: k for k, v in names.var.items()}
    num = int(what, 0) if re.fullmatch(r"-?(0x)?[0-9a-fA-F]+", what) and (what.lstrip("-").isdigit() or what.startswith("0x")) else None
    var = rev.get(what.lstrip("$"))
    for t, nodes in enumerate(read_file()):
        for x in nodes:
            hit = names.node(x) == short(what) or names.node(x) == what
            vals = x.params
            if num is not None and num in vals: hit = True
            if var is not None and any(v & 0xFFFFFFFF == var for v in vals): hit = True
            if hit: print(f"{t} {names.line(x)}")


def block_of(nodes, x, names):
    # the nearest named block (StaticLink) above x, labelled by the first command-like leaf inside it
    y = x
    while y.parent >= 0 and y.cls != STATIC_LINK: y = nodes[y.parent]
    def first_action(z):
        if z.leaf and re.match(r"(Command|Departure|Execute|.*SetPurpose|ArmySet|.*OfficerSet|SceneryInvest|Trade)", names.node(z)):
            return names.node(z)
        for c in z.children:
            a = first_action(c)
            if a: return a
        return None
    return y, first_action(y) or ""


def tunables(names, trees):
    # every literal a designer could tune: number args of predicates (signature "n") and leaf params whose role is a number
    for t, nodes in enumerate(trees):
        for x in nodes:
            if x.cls == CONDITION:
                for k, term in enumerate(terms(x.params)):
                    kind, neg, fn, group, args = term
                    if kind != 2: continue
                    sig = names.pred.get(fn, ("", ""))[1].split()
                    for a, v in enumerate(args):
                        if a < len(sig) and sig[a] == "n":
                            yield t, x, ("term", k, a), v, names.term(term)
            elif x.leaf:
                roles = names.roles.get(names.cls.get(x.cls, {}).get("name", ""), {})
                for a, v in enumerate(x.params):
                    role = roles.get(a, ("",))[0]
                    if role.startswith("number"):
                        yield t, x, ("param", a), v, f"{names.node(x)} p{a} {roles[a][2]}"


def cmd_tunables(tree=None):
    names = Names()
    trees = read_file()
    n = 0
    for t, x, where, v, text in tunables(names, trees):
        if tree is not None and t != tree: continue
        block, action = block_of(trees[t], x, names)
        print(f"{t} #{x.i:<5} block #{block.i:<5} {action:34} {where[0]} {where[1:]} = {v:,}   {text}")
        n += 1
    print(n, "values")


def live_trees(g):
    ai = g.q(g.exe + AI_MANAGER)
    mgr = g.q(ai + 8)
    out = []
    for k in range(4):
        root = g.q(mgr + 0x10 + 8 * k)
        n = g.q(root + 0x38)
        out.append((root, list(struct.unpack(f"<{n}Q", g.read(g.q(root + 0x40), 8 * n)))))
    return out


def live_params(g, x, p, cls):
    # what the node in memory holds for x.params, in the file's order (None where it keeps them elsewhere)
    if x.cls == SEQUENTIAL:
        return list(struct.unpack_from("<iii", g.read(p + 0x28, 12)))
    if x.cls == STATIC_LINK:
        return [i32(g.read(p + 0x28, 4), 0)]
    if x.cls == CONDITION:
        b = g.read(p + 0x28, 0x10)
        head = [b[0], b[1], b[2], b[3]]
        tp = struct.unpack_from("<Q", b, 8)[0]
        out = head
        for k in range(b[3]):
            t = g.read(tp + 0x18 * k, 0x18)
            kind, neg, group, argc = t[8], t[9], t[10], t[11]
            fn = i32(t, 0xC)
            args = list(struct.unpack(f"<{argc}i", g.read(struct.unpack_from("<Q", t, 0x10)[0], 4 * argc))) if argc else []
            out += [kind, neg, fn, group, argc] + args
        return out
    offs = (cls.get(x.cls) or {}).get("offsets")
    if offs is None: return None
    b = g.read(p, 0x100)
    return [i32(b, offs[k]) if k in offs else None for k in range(len(x.params))]   # None: a param the loader skips


def cmd_live():
    from game import Game
    g = Game()
    cls = classes()
    names = Names()
    for t, (nodes, (root, ptrs)) in enumerate(zip(read_file(), live_trees(g))):
        same = differ = custom = 0
        for x, p in zip(nodes, ptrs):
            if u32(g.read(p + 8, 4), 0) != x.cls: print(f"tree {t} #{x.i}: class differs"); differ += 1; continue
            got = live_params(g, x, p, cls)
            if got is None: custom += 1; continue
            if len(got) == len(x.params) and all(v is None or v == f for v, f in zip(got, x.params)): same += 1
            else:
                differ += 1
                print(f"tree {t} {names.line(x)}\n      memory {got}")
        print(f"tree {t} {TREES[t]}: root {root:#x}, {len(ptrs)} nodes ({len(nodes)} in the file): "
              f"{same} as in the file, {differ} different, {custom} not compared")


def cmd_watch(seconds=None):
    from game import Game
    import units
    g = Game()
    seen = {}
    end = time.time() + float(seconds) if seconds else None
    last_date = None
    while end is None or time.time() < end:
        y, m, d, h = units.clock(g)
        if (y, m, d) != last_date:
            last_date = (y, m, d)
        now = {}
        for slot, p, b in units.units(g):
            now[slot] = (b[0x37], b[0x38], struct.unpack_from("<H", b, 0x3A)[0], struct.unpack_from("<H", b, 0x152)[0],
                         struct.unpack_from("<H", b, 0x12)[0], b[0x10])
        for slot, v in now.items():
            if seen.get(slot) != v:
                old = seen.get(slot)
                person = g.q(g.base - 0x290 + 0x158 + v[4] * 8) if v[4] <= 6000 else None   # officers by ID (bases.py)
                commander = (g.utf16(person + 0x12, 7) + g.utf16(person + 0x20, 7)) if person else f"#{v[4]}"
                was = f" (was {old[0]}/{old[1]}/{old[2]})" if old else " (new)"
                print(f"{y}-{m:02}-{d:02} {h:02}h unit {slot:3} {commander:8} order {v[0]:2} target {v[1]:2}/{v[2]:5} from {v[3]:5}{was}")
        for slot in set(seen) - set(now):
            print(f"{y}-{m:02}-{d:02} {h:02}h unit {slot:3} gone")
        seen = now
        sys.stdout.flush()
        time.sleep(0.2)


# The sortie groups (troops tree objects): raw + 0x7F8D8 holds a pointer to each 0x40-byte record; +0x14 purpose (u16),
# +0x16 target, +0x2C leader officer. Slots past the table's end hold no leader or a purpose over 23.
SORTIE_TABLE = 0x7F8D8
ATTACK_PURPOSES = {2, 8, 9, 10, 22}


def sorties(g, count=600):
    out = {}
    for i in range(count):
        p = g.q(g.base - 0x290 + SORTIE_TABLE + 8 * i)
        if not p: break
        r = g.read(p, 0x40)
        if r is None: break
        purpose, target, leader = (struct.unpack_from("<H", r, o)[0] for o in (0x14, 0x16, 0x2C))
        if 0 < leader <= 6000 and purpose <= 23: out[i] = (purpose, target, leader)
    return out


def cmd_sorties(after=None, limit=None):
    # Each sortie group's purpose, a line when it changes; with `after`, it stops that many seconds after the game date
    # has passed the next ten-day turn (or after `limit` seconds). This is how a changed threshold was seen to work.
    from game import Game
    import units
    g = Game()
    turn = lambda d: 0 if d < 11 else 1 if d < 21 else 2
    def summary(t):
        c = Counter(p for p, _, _ in t.values())
        return f"attack={sum(n for k, n in c.items() if k in ATTACK_PURPOSES)} return(0/17/18)={c[0] + c[17] + c[18]} all={len(t)}"
    y0, m0, d0, _ = units.clock(g)
    seen, start, passed = {}, time.time(), None
    while not limit or time.time() - start < float(limit):
        now = sorties(g)
        y, m, d, h = units.clock(g)
        when = f"{y}-{m:02}-{d:02} {h:02}h"
        if not seen: print(f"{when} start {summary(now)}")
        for k, v in now.items():
            if k in seen and seen[k][0] != v[0]: print(f"{when} sortie {k}: purpose {seen[k][0]} -> {v[0]} (leader {v[2]})")
        for k in set(seen) - set(now): print(f"{when} sortie {k}: gone (was {seen[k][0]})")
        seen = now
        if passed is None and ((y, m) != (y0, m0) or turn(d) != turn(d0)):
            passed = time.time()
            print(f"== turn passed {y}-{m:02}-{d:02} {summary(now)}")
        sys.stdout.flush()
        if after and passed is not None and time.time() - passed > float(after): break
        time.sleep(0.5)
    print(f"end {summary(seen)}")


# Words tried in pairs besides the class names': personality and office words found the officer traits ($PersonalityWarlike…)
TRAIT_WORDS = """Personality Trick Value Duty Ambition WarLike Warlike War Like Hate Calm Brave Courage Reckless Kind Mercy Cruel
Gentle Fair Honest Faith Loyal Loyalty Imperial Court Emperor Justice Daring Fear Proud Pride Humble Selfish Generous Jealous
Desire Greed Patience Hasty Steady Conservative Reform Tradition Family Blood Fame Glory Honor Reputation Wealth Economy
Commerce Agriculture Develop Construction Build Building Facility Fortify Defend Defense Protect Guard Expansion Expand Invade
Offense Attack Aggressive Raid Capture Employ Hire Talent Human Personnel Military Domestic Diplomacy Strategic Strategy Intrigue
Scheme Peace Unify Conquest Love Friendship Trust Compatibility Affinity Relation Rival Revenge Obey Rebel Independence Betray
Wisdom Knowledge Charisma Charm Popularity People Public Chief Ruler Lord Person Rank Grade Post Office Credit Merit Level Exp
Point Points Ratio Limit Cap Command Lead Leadership Troop Troops Army Battle Dislike Favorite Friend Close Foe Promotion Promote
Reward Prize Policy Book Life Age Old Young Risk Danger Safe Self Current Plus Minus Ability Main Sub Top Highest High Low Gold
Horse Morale Endurance Durability Population Order Season Month Day Date Year Turn Time Term Period Elapsed Passed Random Seed
Difficulty Prisoner Captive Free Hidden Spy Envoy Strategist Advisor Civil Officer General Commander Governor Viceroy Prefect"""


def cmd_varnames():
    # Every value in the trees that looks like a hash, tried against the exe's identifier-like strings, then against
    # pairs and triples of words from the names found and from the class names (CamelCase, as the found ones are).
    from image import IMAGE
    targets = Counter()
    for nodes in read_file():
        for x in nodes:
            for v in x.params:
                if abs(v) >= 0x10000: targets[v & 0xFFFFFFFF] += 1
    img = open(IMAGE, "rb").read()
    found = {}
    P = [pow(31, k, 1 << 32) for k in range(200)]
    def join(ha, la, hb, lb):   # name_hash(a + b) from the parts: b's bytes sit la places further on
        return (ha + hb * P[la]) & 0xFFFFFFFF, la + lb
    def test(s, how):
        v = name_hash(s)
        if v in targets and v not in found: found[v] = (s, how)
    # in a fixed order, all-capital names first: short strings collide ("SUM" and "StL" hash alike)
    for s in sorted(set(m.decode() for m in re.findall(rb"[A-Za-z_][A-Za-z0-9_]{2,63}", img)), key=lambda s: (not s.isupper(), s)):
        test(s, "exe")
    base = {"My", "Myself", "Each", "Loop", "Own", "Enemy", "Ally", "Target", "Temp", "Max", "Min", "List", "Index", "ID",
            "Total", "Sum", "Need", "Check", "Base", "Belong", "Best", "Next", "Count", "Num", "Rate", "Flag", "Power",
            "Soldier", "Candidate", "Value", "Is", "Can", "Has", "Gold", "Money", "Food", "Front", "Near", "Distance"}
    words = set(base) | set(TRAIT_WORDS.split())
    for m in set(re.findall(rb"\.\?A[VUW]([A-Za-z0-9_@?$]{3,200})@@", img)):
        words |= set(w.capitalize() for w in re.findall(r"[A-Z][a-z]+|[A-Z]+(?![a-z])", m.decode()))
    # mangled fragments ("Ebepbllm") are not words: no English word here runs five consonants together
    words = sorted(w for w in words if 2 <= len(w) <= 14 and not re.search(r"[bcdfghjklmnpqrstvwxz]{5}", w.lower()))
    pre = {w: (name_hash(w), len(w)) for w in words}
    for w in words:
        for s in (w, w.upper(), w.lower()): test(s, "word")
    for a in words:
        for b in words:
            v, n = join(*pre[a], *pre[b])
            if v in targets and v not in found: found[v] = (a + b, "pair")
            test(a.upper() + "_" + b.upper(), "pair")
    # word triples from the names found so far, until a round finds nothing new
    while True:
        vocab = set(base)
        for s, how in found.values():
            vocab |= set(re.findall(r"[A-Z][a-z]*|[a-z]+", s))
        vocab = sorted(vocab)
        pre = {w: (name_hash(w), len(w)) for w in vocab}
        before = len(found)
        for a in vocab:
            for b in vocab:
                hab, lab = join(*pre[a], *pre[b])
                for suf in ("1", "2", "3", "s"): test(a + b + suf, "pair")
                if hab in targets and hab not in found: found[hab] = (a + b, "pair")
                for c in vocab:
                    v, _ = join(hab, lab, *pre[c])
                    if v in targets and v not in found: found[v] = (a + b + c, "triple")
        if len(found) == before: break
    with open(VARS, "w", encoding="utf-8") as f:
        f.write("# hash name source uses: variable names recovered by `python ai.py varnames` (exe = a string in the exe;\n"
                "# pair/triple = word combinations, a few may be chance collisions of the 32-bit hash)\n")
        for v, (s, how) in sorted(found.items(), key=lambda kv: -targets[kv[0]]):
            f.write(f"{v:08x} {s} {how} {targets[v]}\n")
    print(f"{len(found)} of {len(targets)} hashes named -> {VARS}")


CSHARP = os.path.join(HERE, "..", "Core", "Ai", "AiCatalog.Data.cs")


def role_letter(role):
    # ai_leaves.tsv roles as one letter a param: v variable, n number, t number a designer tunes, e enum, - unused, ? unknown
    if role.startswith(("in", "out")): return "v"
    if role == "number:tunable": return "t"
    if role.startswith("number"): return "n"
    if role.startswith("enum"): return "e"
    if role == "unused": return "-"
    return "?"


def cmd_csharp():
    # The tables the editor's core needs, as C#: Core/Ai/AiCatalog.Data.cs (AiCatalogTests checks it against these files).
    cls = classes()
    roles = leaf_roles()
    info = sorted(info_kinds())
    preds = {}
    for line in open(PREDS, encoding="utf-8"):
        if line.startswith("#") or not line.strip(): continue
        p = line.rstrip("\n").rstrip("\r").split("\t")
        preds[int(p[0])] = (p[1], p[2] if len(p) > 2 else "")
    out = ["// Made by `python ai.py csharp` (source/Reverse) from the exe image (the classes) and source/Reverse/ai_preds.tsv,",
           "// ai_leaves.tsv, ai_info.tsv and ai_vars.txt. Not edited by hand: change those and make it again; AiCatalogTests",
           "// checks the two agree.",
           "namespace San14Tenkazu;", "", "internal static partial class AiCatalog", "{",
           "    // hash, the game's class name, leaf, where the params live (null: a composite, or a leaf with a loader of its own),",
           "    // what each param is (v variable, n number, t number a designer tunes, e enum, - unused, ? not read)",
           "    private static readonly AiClass[] ClassData =", "    ["]
    for h, c in sorted(cls.items(), key=lambda kv: kv[1]["name"]):
        offs = c["offsets"]
        o = "null" if offs is None else "[" + ", ".join(f"0x{offs[k]:X}" for k in range(len(offs))) + "]" if all(k in offs for k in range(len(offs))) else "null"
        r = roles.get(c["name"], {})
        n = max([k for k in r if k != "node"], default=-1) + 1
        letters = "".join(role_letter(r[k][0]) if k in r else "?" for k in range(n))
        if c["name"] in info: letters = "ev" if kind_param(c["name"]) == 0 else "vev"   # (source,) kind, output
        out.append(f'        new(0x{h:08X}, "{c["name"]}", {"true" if c["kind"] == "leaf" else "false"}, {o}, "{letters}"),')
    out += ["    ];", "", "    // predicate id, name, what each argument is (v int variable, o object variable, l list, n number, e enum)",
            "    private static readonly (int Id, string Name, string Signature)[] PredicateData =", "    ["]
    for k in sorted(preds):
        out.append(f'        ({k}, "{preds[k][0]}", "{preds[k][1]}"),')
    out += ["    ];", "", "    // the info nodes, which read one fact of an object chosen by a kind number",
            "    private static readonly string[] InfoClassData =", "    ["]
    out += [f'        "{n}",' for n in info]
    out += ["    ];", "", "    // variable names recovered from their hashes (ai.py varnames)",
            "    private static readonly (uint Hash, string Name)[] VariableData =", "    ["]
    for v, n in sorted(var_names().items()):
        out.append(f'        (0x{v:08X}, "{n}"),')
    out += ["    ];", "}", ""]
    os.makedirs(os.path.dirname(CSHARP), exist_ok=True)
    open(CSHARP, "wb").write("\r\n".join(out).encode("utf-8"))
    print(f"{len(cls)} classes, {len(preds)} predicates, {len(info)} info classes, {len(var_names())} variables -> {CSHARP}")


def cmd_kotxt():
    # The tables' Korean labels as sections of the editor's word set (Core/Strings/ko.txt), printed to paste in once:
    # [AiNodes] class = what it does, [AiParams] class.k = what param k is, [AiInfo] class.kind = the fact, [AiPredicates]
    # id = what it judges. From then on the labels are kept in ko.txt (the editor's words), these tables stay the research.
    def clean(text):
        return text.strip().replace("\\", "\\\\")
    out = ["# AI 창: 노드 클래스가 하는 일 (게임의 클래스 이름 = 문구)", "[AiNodes]"]
    params = ["# AI 창: 잎 노드 파라미터 (클래스.번호 = 문구)", "[AiParams]"]
    for name, rows in sorted(leaf_roles().items()):
        if "node" in rows and rows["node"][2].strip(): out.append(f"{name} = {clean(rows['node'][2])}")
        for k in sorted(r for r in rows if r != "node"):
            if rows[k][2].strip(): params.append(f"{name}.{k} = {clean(rows[k][2])}")
    info = ["# AI 창: 정보 노드의 종류 (클래스.종류 = 문구)", "[AiInfo]"]
    for line in open(INFO, encoding="utf-8"):
        if line.startswith("#") or not line.strip(): continue
        p = line.rstrip("\r\n").split("\t")
        if p[1].isdigit() and p[4].strip(): info.append(f"{p[0].strip()}.{p[1]} = {clean(p[4])}")
        if p[1] == "params" and p[4].strip(): out.append(f"{p[0].strip()} = {clean(p[4].split(':')[0])}")
    preds = ["# AI 창: 조건 판정 (번호 = 문구)", "[AiPredicates]"]
    for line in open(PREDS, encoding="utf-8"):
        if line.startswith("#") or not line.strip(): continue
        p = line.rstrip("\r\n").split("\t")
        if len(p) > 4 and p[4].strip() and p[4].strip() != "?": preds.append(f"{p[0]} = {clean(p[4])}")
    out = out[:2] + sorted(out[2:])
    preds = preds[:2] + sorted(preds[2:], key=lambda line: int(line.split(" = ")[0]))
    print("\n".join(out + [""] + params + [""] + info + [""] + preds))


if __name__ == "__main__":
    a = sys.argv[1:] or ["file"]
    cmd = a[0]
    if cmd == "file": cmd_file()
    elif cmd == "classes": cmd_classes()
    elif cmd == "preds": cmd_preds()
    elif cmd == "tree": cmd_tree(int(a[1]), int(a[2]) if len(a) > 2 else 0, int(a[3]) if len(a) > 3 else 99)
    elif cmd == "path": cmd_path(int(a[1]), int(a[2]))
    elif cmd == "find": cmd_find(a[1])
    elif cmd == "live": cmd_live()
    elif cmd == "watch": cmd_watch(a[1] if len(a) > 1 else None)
    elif cmd == "sorties": cmd_sorties(a[1] if len(a) > 1 else None, a[2] if len(a) > 2 else None)
    elif cmd == "varnames": cmd_varnames()
    elif cmd == "tunables": cmd_tunables(int(a[1]) if len(a) > 1 else None)
    elif cmd == "csharp": cmd_csharp()
    elif cmd == "kotxt": cmd_kotxt()
    else: raise SystemExit(__doc__ if __doc__ else "명령을 모릅니다")

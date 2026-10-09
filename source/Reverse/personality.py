# 개성 effects as the game applies them, read from its code (the dumped image) and, for records and names, from the
# running game (read-only). What the output means is written up in notes/personality.md.
#   python personality.py records     every 개성: conditions, duration, range and its two effects (game running)
#   python personality.py handlers    the condition handlers 0x27DF10 picks by condition number
#   python personality.py kinds       every effect kind and the code that reads it, by the helper it goes through
#   python personality.py ids         the code that asks for one 개성 by its number (0x232D00) instead of its kinds
import struct, sys
from collections import defaultdict

from capstone import x86

from image import Image

sys.stdout.reconfigure(encoding="utf-8")

TABLE = 0x7DA88            # game.base + TABLE + i*8; the code reads it as raw + 0x7DD18
CODE_TABLE = 0x7DD18
COUNT, SIZE = 0x164, 0xE8

# The functions every effect kind is read through (rcx = unit, officer or 개성; edx = the kind).
HELPERS = {
    0x26A2C0: "unit-sums",        # a unit's effects by kind: its commander's 개성 that are lit or dur -1, and the received
    0x2C89D0: "unit-received",    # one of the 개성 the unit received from others (+0x64) has the kind
    0x2C37F0: "officer-lit",      # one of the officer's 개성 has it and is lit or dur -1 (his own unit only)
    0x2C5460: "officer-has",      # one of the officer's 개성 has it (no condition)
    0x2AB2E0: "officer-find",     # the officer's 개성 that has it (callers may then test its dur)
    0x2B2DA0: "officer-sum",      # the officer's total of it (no condition)
    0x26A750: "officer-sums",     # the officer's effects by kind (no condition; r9d 1 = dur -1 only)
    0x22C630: "value",            # the value one 개성 has for it
}
SUMS = (0x26A2C0, 0x26A750)

# What each condition handler checks (read from the handlers; numbers the handler itself compares).
CONDITIONS = {
    1: "자기 부대가 전투 부대(+0x10 == 1)", 2: "인접한 아군 부대", 3: "인접한 적 부대", 4: "자기 부대 병참 단절(+0x26 bit 2 없음)",
    5: "자기 부대 상태이상(+0x23..+0x25 중 하나)", 6: "자기 부대 상태이상 +0x23", 7: "자기 부대 상태이상 +0x24",
    8: "자기 부대 상태이상 +0x25", 9: "범위 안의 아군 부대(자기 제외)", 10: "범위 안의 적 부대", 11: "자기 부대(항상)",
    12: "자기 부대 진형이 병기", 13: "자기 부대가 +0x10 == 2", 14: "자기 부대 지형 분류 4(대하)", 15: "지형 분류 3(숲·밀림)",
    16: "지형 분류 2(산)", 17: "지형 분류 3 또는 5", 18: "지형 분류 6", 19: "지형 분류 4(대하)가 아님",
    20: "자기 부대 병사 ≤ +0xA8", 21: "범위 안의 아군 부대(남, 보유자 +0xA9 == 1)", 22: "범위 안의 적 부대(남, 보유자 +0xA9 == 1)",
    23: "인접한 아군 부대(남, 보유자 +0xA9 == 1)", 24: "인접한 적 부대(남, 보유자 +0xA9 == 1)", 25: "자기 부대 퇴각 중(+0x37 0xB·0xC)",
    26: "자기 부대 옆에 적대 부대 2 이상", 28: "자기 부대가 마을 위", 31: "범위 3 안에 아군 부대 3 이상", 32: "범위 3 안에 적 부대 3 이상",
    34: "자기 부대가 자기 세력 지역", 36: "자기 부대(항상)", 37: "자기 부대가 전투 부대이고 대하·병기 진형이 아님",
    38: "범위 안의 적대 이민족 부대", 41: "자기 부대 퇴각 중(전투 부대)",
}
EVENTS = {30: "공격받았을 때 (0x178E30, 기간형만)", 39: "범위 3 안의 아군 부대 괴멸 시 (0x258EE0, 두 수치를 사기로)",
          40: "범위 3 안의 적 부대 격파 시 (0x258EE0, 두 수치를 사기로)"}

R = {}
for names in (("rax", "eax", "ax", "al"), ("rbx", "ebx", "bx", "bl"), ("rcx", "ecx", "cx", "cl"), ("rdx", "edx", "dx", "dl"),
              ("rsi", "esi", "si", "sil"), ("rdi", "edi", "di", "dil"), ("rbp", "ebp", "bp", "bpl")) + \
        tuple((f"r{n}", f"r{n}d", f"r{n}w", f"r{n}b") for n in range(8, 16)):
    for n in names: R[n] = names[0]
VOLATILE = ("rax", "rcx", "rdx", "r8", "r9", "r10", "r11")


def game():
    from game import Game
    return Game()


def records(g):
    out = []
    for i in range(COUNT):
        p = g.table(TABLE, i)
        if not p: continue
        b = g.read(p, SIZE)
        u8 = lambda o: b[o]
        u16 = lambda o: struct.unpack_from("<H", b, o)[0]
        i16 = lambda o: struct.unpack_from("<h", b, o)[0]
        i32 = lambda o: struct.unpack_from("<i", b, o)[0]
        out.append(dict(index=i, name=b[0x10:0x1C].decode("utf-16le", "ignore").split("\0")[0],
                        description=b[0x3C:0xA0].decode("utf-16le", "ignore").split("\0")[0],
                        officer=u16(0xA2), cond1=u8(0xA4), value1=i32(0xA8), cond2=u8(0xAC), value2=i32(0xB0),
                        dur=i16(0xB4), kind1=u8(0xB6), effect1=i16(0xB8), kind2=u8(0xBA), effect2=i16(0xBC),
                        be=u8(0xBE), bf=u8(0xBF)))
    return out


def names_by_kind():
    try:
        rs = records(game())
    except SystemExit:
        return {}, {}
    by_kind = defaultdict(list)
    for r in rs:
        for k in (r["kind1"], r["kind2"]):
            if k: by_kind[k].append(r["name"])
    return by_kind, {r["index"]: r["name"] for r in rs}


# --- what the registers may hold at a call: constants along the code before it, read from the start of its function
# with forward jumps merged where they land. Enough for "mov edx, 0x2F", "xor r9d, r9d; lea edx, [r9 + 0x14]",
# "test eax, eax; jne ...; lea edx, [rax + 0x42]" and two branches that give 0x16 and 0x18.
def code_start(im, site):
    # the .pdata entry holding site, joined with the entries that end right where it begins (a function's chained
    # parts follow its body with no padding)
    b, e = im.func(site)
    for _ in range(4):
        before = im.func(b - 1)
        if not before or before[1] != b: break
        b = before[0]
    return b if site - b < 0x6000 else site - 0x200


def merge(states):
    regs = set.intersection(*(set(s) for s in states)) if states else set()
    return {r: set().union(*(s[r] for s in states)) for r in regs}


def known_before(im, site):
    start = code_start(im, site)
    state, pending, zero = {}, defaultdict(list), None
    for i in im.md.disasm(im.img[start:site], start):
        if i.address in pending:
            state = merge(pending.pop(i.address) + ([state] if state is not None else []))
        elif state is None:
            state = {}                       # reached only by a jump from after it: nothing known
        m, ops = i.mnemonic, i.op_str.split(", ")
        if m.startswith("j"):
            target = int(ops[0], 0) if ops[0].startswith("0x") else None
            if target is not None and i.address < target <= site:
                taken = {r: set(v) for r, v in state.items()}
                if zero and m in ("je", "jz"): taken[zero] = {0}
                pending[target].append(taken)
            if m == "jmp": state = None
            elif zero and m in ("jne", "jnz"): state[zero] = {0}   # falling through "test r, r; jne" leaves r = 0
            zero = None
            continue
        zero = None
        if m in ("ret", "int3"):
            state = None
            continue
        if m == "call":
            for r in VOLATILE: state.pop(r, None)
            continue
        if m in ("cdq", "cqo"): state.pop("rdx", None); continue
        if m in ("cdqe", "cwde"): state.pop("rax", None); continue
        if m in ("div", "idiv", "mul") or (m == "imul" and len(ops) == 1):
            state.pop("rax", None); state.pop("rdx", None); continue
        if "rep" in m or m.startswith(("stos", "movs")):
            for r in ("rcx", "rdi", "rsi"): state.pop(r, None)
            continue
        if m == "test" and len(ops) == 2 and ops[0] == ops[1] and ops[0] in R:
            zero = R[ops[0]]
            continue
        if m in ("cmp", "test", "push", "bt") or not ops or ops[0] not in R: continue
        d = R[ops[0]]
        if m == "xor" and len(ops) == 2 and ops[0] == ops[1]:
            state[d] = {0}
        elif m == "mov" and len(ops) == 2:
            try: state[d] = {int(ops[1], 0)}
            except ValueError:
                if ops[1] in R and R[ops[1]] in state: state[d] = set(state[R[ops[1]]])
                else: state.pop(d, None)
        elif m == "lea" and ops[1].startswith("[") and "*" not in ops[1] and "rip" not in ops[1] and "sp" not in ops[1]:
            sums = {0}
            for p in ops[1].strip("[]").replace(" ", "").replace("-", "+-").split("+"):
                if p in R:
                    sums = {a + v for a in sums for v in state[R[p]]} if sums is not None and R[p] in state else None
                elif sums is not None:
                    sums = {a + int(p, 0) for a in sums}
            if sums is None: state.pop(d, None)
            else: state[d] = sums
        else:
            state.pop(d, None)
    if site in pending:
        state = merge(pending.pop(site) + ([state] if state is not None else []))
    return {r: sorted(v) for r, v in (state or {}).items()}


def buffer_slots(im, site):
    # the kinds a caller of a sums function reads from its buffer (r8 = lea [rsp/rbp + X]): reads at X + 4*kind
    b, e = im.func(site)
    before = list(im.md.disasm(im.img[max(b, site - 0x40):site], max(b, site - 0x40)))
    base = None
    for i in before:
        if i.mnemonic == "lea" and i.op_str.startswith("r8, [r"):
            inner = i.op_str[5:-1].replace(" ", "").replace("-", "+-")
            reg, _, off = inner.partition("+")
            if reg in ("rsp", "rbp"): base = (reg, int(off, 0) if off else 0)
    if base is None: return None
    reg_id = x86.X86_REG_RSP if base[0] == "rsp" else x86.X86_REG_RBP
    slots = set()
    for i in im.flow(b):
        if i.address <= site or i.mnemonic == "lea" or i.id == 0: continue
        for op in i.operands:
            if op.type == x86.X86_OP_MEM and op.mem.base == reg_id and op.mem.index == 0 and base[1] <= op.mem.disp < base[1] + 0x1FC:
                slots.add((op.mem.disp - base[1]) // 4)
    return sorted(slots)


def table_functions(im):
    # the functions that read the 개성 table, or an officer's 개성 through the slot getters (0x2305D0, 0x230570 lit
    # only, 0x230670 the list of all nine)
    fs = {f for t in (0x2305D0, 0x230570, 0x230670) for s, f in im.callers(t)}
    for b, e, _ in im.funcs:
        if CODE_TABLE.to_bytes(4, "little") in im.img[b:e]:
            fs.add(b)
    return fs


def kinds(im):
    im.md.detail = True
    uses = defaultdict(list)                 # kind -> [(how, site)]
    unresolved = []
    for site, target, f in im.xref()[1]:
        if target not in HELPERS or im.img[site] != 0xE8: continue
        k = known_before(im, site)
        kinds_here, how = k.get("rdx"), HELPERS[target]
        if target in SUMS:
            mode = k.get("r9")
            if target == 0x26A750 and mode == [1]: how += "(dur -1)"
            elif target == 0x26A2C0 and mode != [0]: how += f"(r9={'?' if mode is None else mode})"
        if target == 0x2AB2E0:
            after = list(im.md.disasm(im.img[site:site + 0x60], site))[:14]
            if any("0xb4]" in i.op_str and i.mnemonic == "cmp" for i in after): how += "(dur -1)"
        if kinds_here is None:
            unresolved.append((site, how))
            continue
        for kind in kinds_here:
            if kind == 0 and target in SUMS:
                slots = buffer_slots(im, site)
                if slots is None: unresolved.append((site, how + " buffer"))
                for s in slots or []: uses[s].append((how + "[slot]", site))
            else:
                uses[kind].append((how, site))
    # kinds compared directly with +0xB6/+0xBA in the functions that read the 개성 table
    for b in table_functions(im):
        e = im.func(b)[1]
        loaded = None
        for i in im.md.disasm(im.img[b:e], b):
            if i.id == 0: continue           # data inside the code (skipdata)
            o = i.operands
            if i.mnemonic == "cmp" and len(o) == 2 and o[1].type == x86.X86_OP_IMM:
                if o[0].type == x86.X86_OP_MEM and o[0].mem.disp in (0xB6, 0xBA) and o[0].size == 1:
                    uses[o[1].imm & 0xFF].append(("compare", i.address))
                elif o[0].type == x86.X86_OP_REG and loaded == o[0].reg:
                    uses[o[1].imm & 0xFF].append(("compare", i.address))
            loaded = None
            if i.mnemonic == "movzx" and len(o) == 2 and o[1].type == x86.X86_OP_MEM and o[1].mem.disp in (0xB6, 0xBA):
                loaded = o[0].reg
    return uses, unresolved


def handlers(im):
    # 0x27DF10 fills a frame array with handler pointers ("lea rax, [rip + h]; mov [rsp/rbp + off], rax"), index =
    # condition number, then calls array[cond] if the condition is below 0x65 and the entry is not null.
    flow = im.flow(0x27DF10)
    rbp_from_rsp = None
    lea_rbp = sub = None
    for i in flow:
        if i.mnemonic == "lea" and i.op_str.startswith("rbp, [rsp - "): lea_rbp = int(i.op_str.split("- ")[1][:-1], 0)
        if i.mnemonic == "sub" and i.op_str.startswith("rsp, ") and sub is None: sub = int(i.op_str[5:], 0)
    rbp_from_rsp = sub - lea_rbp
    stores, regs, started = {}, {}, False
    for i in flow:
        if i.mnemonic == "call" and i.op_str == "0x22c3b0": started = True; continue
        if not started: continue
        if i.mnemonic == "call": break
        ops = i.op_str.split(", ")
        if i.mnemonic == "lea" and "rip" in ops[1]:
            regs[ops[0]] = i.address + i.size + int(ops[1].split("rip + ")[1][:-1], 0)
        elif i.mnemonic == "xor" and ops[0] == ops[1]:
            regs[ops[0].replace("e", "r", 1) if ops[0].startswith("e") else ops[0]] = 0
        elif i.mnemonic == "mov" and ops[0].startswith("qword ptr [r") and ops[1] in regs:
            inner = ops[0][len("qword ptr ["):-1].replace(" ", "")
            reg, off = inner[:3], inner[3:]
            off = int(off, 0) if off else 0
            stores[off + (rbp_from_rsp if reg == "rbp" else 0)] = regs[ops[1]]
    base = min(stores)
    table = {(off - base) // 8: h for off, h in stores.items()}
    out = []
    for cond in range(max(table) + 1):
        h = table.get(cond, 0)
        checks = None
        if h:
            for i in im.md.disasm(im.img[h:h + 0x40], h):
                if i.mnemonic == "cmp" and i.op_str.startswith("ecx, "): checks = int(i.op_str[5:], 0); break
        out.append((cond, h, checks))
    return out


def ids(im):
    out = []
    for site, f in im.callers(0x232D00):
        for i in known_before(im, site).get("rdx") or [None]:
            out.append((i, site, im.func(site)[0]))
    return out


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "records":
        for r in records(game()):
            if not (r["kind1"] or r["kind2"] or r["cond1"]): continue
            print(f"{r['index']:3} {r['name']:6} cond1={r['cond1']:<3} v1={r['value1']:<5} cond2={r['cond2']:<3} dur={r['dur']:<3} "
                  f"kind1={r['kind1']:<3} {r['effect1']:<5} kind2={r['kind2']:<3} {r['effect2']:<5} officer={r['officer']} "
                  f"be={r['be']:#04x} bf={r['bf']:#04x} | {r['description']}")
    elif cmd == "handlers":
        im = Image()
        for cond, h, checks in handlers(im):
            meaning = CONDITIONS.get(cond) or EVENTS.get(cond, "")
            state = f"{h:#x}" if h else "없음"
            mismatch = "" if not h or checks == cond else f"  (처리 함수가 비교하는 수 {checks})"
            print(f"{cond:3}  {state:9} {meaning}{mismatch}")
    elif cmd == "kinds":
        im = Image()
        uses, unresolved = kinds(im)
        by_kind, _ = names_by_kind()
        for k in sorted(set(uses) | set(by_kind)):
            how = defaultdict(list)
            for h, site in uses.get(k, []): how[h].append(f"{site:#x}")
            text = "; ".join(f"{h} {' '.join(s[:4])}{' …' if len(s) > 4 else ''}" for h, s in sorted(how.items())) or "-"
            print(f"{k:3}  {text}  | {' '.join(by_kind.get(k, [])[:6])}")
        print(f"# kind not resolved at {len(unresolved)} calls (loops, tables): " + " ".join(f"{s:#x}" for s, h in unresolved))
    elif cmd == "ids":
        im = Image()
        _, names = names_by_kind()
        by = defaultdict(list)
        for i, site, f in ids(im): by[i].append(f"{site:#x}@{f:#x}")
        for i in sorted(by, key=lambda v: (v is None, v or 0)):
            print(f"{'?' if i is None else i:>4} {names.get(i, '') if i is not None else '':6} {' '.join(by[i])}")
    else:
        print(__doc__ if __doc__ else open(__file__, encoding="utf-8").read().split("import")[0])

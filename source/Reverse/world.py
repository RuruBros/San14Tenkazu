# A snapshot of the game objects the formulas read, in the form the Core's World loads (Formulas/World.cs):
# every table as the game's pointer list at raw + offset (raw = the game's own world pointer, exe + 0x277DF18),
# each record's bytes, plus the exe-static settings object. The code's own offsets name the tables, so a formula
# in C# reads "[raw + 0xDF30 + i*8]" as w.Table(0xDF30)[i], exactly as the game does.
import gzip, json, struct

# raw offset of the pointer list -> (record size, count); counts as this build holds them (2026-10-03).
TABLES = {
    0x73A50: ("officer", 0x1F0, 1851),
    0xDF30: ("force", 0x268, 52),
    0xE0D0: ("corps", 0x30, 52),
    0x77B18: ("item", 0x80, 202),
    0x78490: ("rank", 0xB8, 13),
    0x784F8: ("office", 0x58, 256),
    0x85F20: ("doctrine", 0x50, 80),
    0x7E838: ("unit", 0x200, 501),           # CArmyUnitData
    0xE270: ("tile", 0x20, 48400),           # CHexData, 220 x 220
    0x73860: ("terrain", 0x48, 31),          # CTerrainData
    0x77428: ("formation", 0xC8, 21),        # CFormationData
    0x78D50: ("policy", 0xA8, 121),          # CPolicyData
    0x80880: ("policyfx", 0xB0, 101),        # CPolicyEffectData (+0x98..+0xA0: per-level values, encoded)
    0x85DF0: ("credit", 0x18, 7),            # CCreditEffectData
    0x7DD18: ("personality", 0xE8, 356),     # CPersonalityData
    0x6CAF0: ("county", 0x98, 501),          # CAreaData
    0xDD38: ("city", 0x168, 52),             # CCityData
    0xDED8: ("gate", 0xC0, 11),              # CGateData
    0x6DA98: ("building", 0x20, 3001),       # CObjectData
    0xDC80: ("state", 0x38, 23),             # CStateData (주): +0x30 its 지역 이점
    0x85990: ("advantage", 0xB8, 18),        # CAdvantageData (지역 이점): requirement and two effects, in +0xA0 vector
    0x774D0: ("tactic", 0x90, 201),          # CTacticsData (전법): +0x40 its kind
    0x79118: ("tacticref", 0x58, 31),       # 0x22A320 scans +0x52 when +0x53 marks a valid reference
    0x73958: ("objectkind", 0x68, 31),       # the building kinds (a CObjectData's +0x12): +0x2C its class
    0x78CF8: ("clan", 0x78, 7),              # the officer clans (+0x14C), 0x22BE10 reads the ruler's
}
# Tables hung off an exe global instead of the world: key -> (name, rva of the pointer, offset of the pointer list
# past it, record size, count). The key (rva + offset) names the table in the snapshot and in Tables (C#).
EXE_TABLES = {
    0x277DF60: ("level", 0x277DF00, 0x60, 0x28, 101),   # ability levels 0..100: +0x10 the exp each ability needs
}


def table_ptr(o, g, key, i):
    # the i-th record of a table, by the raw offset of its pointer list or an EXE_TABLES key
    if key in EXE_TABLES:
        _, rva, off, _, _ = EXE_TABLES[key]
        return o.q(o.q(g.exe + rva) + off + i * 8)
    return o.q(g.base - 0x290 + key + i * 8)
BYID_RAW, BYID_COUNT = 0x158, 6001          # officers by ID (pointers into the officer array, or null)
WORLDSTATE_RAW, WORLDSTATE_SIZE = 0x871E8, 0x1800   # CWorldData (date, player force, mode)
# exe globals the formulas read: name -> (rva, how): "set" is whether the qword is nonzero, "int" the dword itself
EXE_VALUES = {"mgr_2143268": (0x2143268, "set"), "link_floor": (0x27D2694, "int"), "wounded_force1": (0x27D26A4, "int"),
              "capture_extra": (0x27D260C, "int"), "county_loss": (0x1D18984, "int"),   # county_loss: a misread, unused (kept until the next snapshot)
              "bombard_fx_pos": (0x27D26A8, "int"), "bombard_fx_neg": (0x27D26AC, "int"),
              "recruit_preset2": (0x27D2624, "int"), "capacity_extra": (0x27D2690, "int"),
              "tactic_link_p0_1": (0x27D2660, "int"), "tactic_link_p0_2": (0x27D2664, "int"),
              "tactic_link_p1_1": (0x27D2668, "int"), "tactic_link_p1_2": (0x27D266C, "int"),
              "tactic_link_p2_1": (0x27D2670, "int"), "tactic_link_p2_2": (0x27D2674, "int"),
              "tactic_link_ai_1": (0x27D267C, "int"), "tactic_link_ai_2": (0x27D2680, "int"),
              "challenge_active": (0x27D29A0, "ptrint0"), "challenge_kind": (0x27D29A0, "ptrint4"),
              "challenge_flags": (0x27D2A10, "ptrbyte9"), "challenge_rules": (0x27D2A10, "ptrbyte8")}
SETTINGS_RVA, SETTINGS_SIZE = 0x1A46CA0, 0x1000   # the options object 0x3E9C90 returns


def exe_value_address(o, exe, rva, how):
    if how.startswith('ptr'):
        pointer = o.q(exe + rva)
        if not pointer: return 0
        prefix = 'ptrbyte' if how.startswith('ptrbyte') else 'ptrint'
        return pointer + int(how[len(prefix):])
    return exe + rva


def vector(m, rec_addr, at):
    # a std::vector of dwords inside a record: begin at +at, end at +at+8
    b, e = struct.unpack("<QQ", m.read(rec_addr + at, 16))
    if not b or e < b or e - b > 0x100: return ""
    return m.read(b, e - b).hex()


def office_vector(m, rec_addr):
    # CPostData +0x40/+0x48: begin/end of a vector of dwords (its ability bonuses, encoded)
    b, e = struct.unpack("<QQ", m.read(rec_addr + 0x40, 16))
    if not b or e < b or e - b > 0x100: return ""
    return m.read(b, e - b).hex()


# Linked lists the game keeps beside its tables, by the raw offset of their head. The head holds an index into a
# pool of nodes (exe + pool rva: the node pointers; + 0x30: their count); a node's first `fields` qwords point at
# records, and qword `next` is the next node. Members are saved as [[table, index] or null per field].
POOL_RVA = 0x27D10F0
LISTS = {
    0x60: {"pool": POOL_RVA, "next": 1, "fields": 1},     # active units, force troop totals and local surrender pressure
    0xC0: {"pool": POOL_RVA, "next": 1, "fields": 1},     # active forces, the third-force gate for diplomacy kind 7
    0x110: {"pool": POOL_RVA, "next": 1, "fields": 1},    # the items that have an owner (CPersonItemIterator)
    0x80: {"pool": POOL_RVA, "next": 1, "fields": 1},     # the bases (cities, gates), World_GetBase 0x32C130
    0x70: {"pool": POOL_RVA, "next": 1, "fields": 1},     # units pending final removal (0x31ECC0 -> 0x28E290)
    0x140: {"pool": 0x2144580, "next": 6, "fields": 3},   # destruction requests: target unit/base, attacker unit (0x31EAB0)
    0x90: {"pool": POOL_RVA, "next": 1, "fields": 1},     # the cities (0x2AAAA0 counts a force's in a 주 here)
    0x100: {"pool": POOL_RVA, "next": 1, "fields": 1},    # the officers CFootholdPersonIterator walks (0x234810)
    0xF0: {"pool": POOL_RVA, "next": 1, "fields": 1},     # buildings that take no siege damage (0x233590); empty so far
}


def ref_of(o, raw, p):
    # [table, index] of a record pointer, null for 0
    if not p: return None
    for off, (name, size, count) in TABLES.items():
        first = o.q(raw + off)
        if first <= p < first + size * count and (p - first) % size == 0:
            return [f"{off:#x}", (p - first) // size]
    raise SystemExit(f"pointer {p:#x} is in no snapshot table")


def list_items(o, raw, exe, head_off, spec):
    head = o.q(raw + head_off)
    if not head: return []
    pool = o.q(exe + spec["pool"])
    count = struct.unpack("<I", o.read(exe + spec["pool"] + 0x30, 4))[0]
    idx = struct.unpack("<I", o.read(head, 4))[0]
    if idx >= count: return []
    node = o.q(pool + idx * 8)
    out, seen = [], set()
    while node and node not in seen:
        seen.add(node)
        vals = struct.unpack(f"<{spec['next'] + 1}Q", o.read(node, 8 * (spec["next"] + 1)))
        out.append([ref_of(o, raw, v) for v in vals[:spec["fields"]]])
        node = vals[spec["next"]]
    return out


# The node pools the game's lists come from (manager objects in .data): their blocks and arrays are copied at the
# snapshot too, or the oracle would copy them later, after the running game moved its own lists on.
POOLS = (0x27D10E0, 0x2144570)


def prefetch_pools(o, g):
    for mgr in POOLS:
        q = struct.unpack("<18Q", o.read(g.exe + mgr, 0x90))
        count = max(q[7], q[8], 0x40)
        for k in (1, 2, 3, 4, 5, 10, 11, 12, 13):
            if 0x10000 < q[k] < 0x7FF000000000:
                o.prefetch(q[k], count * (16 if k == 1 else 8))


def snapshot(o, tables=TABLES):
    # Read through the oracle: the pages it copies now are the ones its calls see later, so the C# World and the
    # game's code read the same bytes even while the game runs on.
    g = o.g
    raw = g.base - 0x290
    prefetch_pools(o, g)
    out = {"exe_settings_rva": SETTINGS_RVA, "settings": o.read(g.exe + SETTINGS_RVA, SETTINGS_SIZE).hex(), "tables": {},
           "lists": {f"{h:#x}": {"items": list_items(o, raw, g.exe, h, spec)} for h, spec in LISTS.items()}}
    exe_tables = {k: (name, size, count) for k, (name, rva, off, size, count) in EXE_TABLES.items()}
    from oracle import OracleError
    for off, (name, size, count) in list(tables.items()) + list(exe_tables.items()):
        recs, extra, missing_vectors = [], {}, []
        try:
            a0 = table_ptr(o, g, off, 0)
        except OracleError:
            # a frozen game whose dump predates this table: the modules that need it must wait for a live run
            if g.pid is not None: raise
            print(f"  (frozen game lacks the {name} table; skipped)")
            continue
        for i in range(count):
            p = table_ptr(o, g, off, i)
            if p != a0 + i * size:
                raise SystemExit(f"{name}: entry {i} is not contiguous")
            recs.append(o.read(p, size).hex())
            if name == "office":
                v = office_vector(o, p)
                if v: extra[str(i)] = {"vec40": v}
            if name == "policyfx":
                v = vector(o, p, 0x98)
                if v: extra[str(i)] = {"vec98": v}
            if name == "personality":
                v = vector(o, p, 0xC8)
                if v: extra[str(i)] = {"vecC8": v}
            if name == "formation":
                v = vector(o, p, 0xA8)
                if v: extra[str(i)] = {"vecA8": v}
            if name == "advantage":
                v = vector(o, p, 0xA0)
                if v: extra[str(i)] = {"vecA0": v}
            if name == "tactic":
                try:
                    v = vector(o, p, 0x70)
                    if v: extra[str(i)] = {"vec70": v}
                except OracleError:
                    if g.pid is not None: raise
                    extra[str(i)] = {"vec70_missing": True}
                    missing_vectors.append(i)
        if missing_vectors:
            print(f"  (frozen game lacks {len(missing_vectors)} {name} vectors; marked missing)")
        out["tables"][f"{off:#x}"] = {"name": name, "size": size, "address": f"{a0:#x}", "records": recs, "extra": extra}
    first = o.q(raw + 0x73A50)
    byid = []
    for i in range(BYID_COUNT):
        p = o.q(raw + BYID_RAW + i * 8)
        byid.append(-1 if not p else (p - first) // 0x1F0)
    out["byid"] = byid
    ws = o.q(raw + WORLDSTATE_RAW)
    out["worldstate"] = o.read(ws, WORLDSTATE_SIZE).hex()
    out["flags"] = {}
    for k, (rva, how) in EXE_VALUES.items():
        try:
            address = exe_value_address(o, g.exe, rva, how)
            out["flags"][k] = int(o.q(address) != 0) if how == "set" else (
                o.read(address, 1)[0] if how.startswith('ptrbyte') else struct.unpack('<i', o.read(address, 4))[0]) if address else 0
        except OracleError:
            if g.pid is not None: raise
            print(f"  (frozen game lacks the {k} global; skipped)")
    out["cmdactions"] = cmd_actions(o)
    return out


# The commands under way (CCmdAction objects: missions officers are away on), on the list at [raw + 0x871E0] + 0x18;
# each names its officers (IDs on a list at +0x20, pool exe + 0x1B5C9A0) and its kind (vtable slot +0x98, read by
# running it). 0x232240 holds an officer away when his command's kind is 0x10.
CMD_POOL_RVA = 0x1B5C9A0


def cmd_actions(o):
    from image import Image
    g = o.g
    raw = g.base - 0x290
    classes = {g.exe + vt: name for vt, name in Image().rtti_vtables().items()}

    def heap(p):
        return 0x10000 < p < 0x7FF000000000
    mgr = o.q(raw + 0x871E0)
    out = []
    if not mgr: return out
    head = o.q(mgr + 0x18)
    if not head: return out
    pool = o.q(g.exe + POOL_RVA)
    count = struct.unpack("<I", o.read(g.exe + POOL_RVA + 0x30, 4))[0]
    idx = struct.unpack("<I", o.read(head, 4))[0]
    if idx >= count: return out
    node = o.q(pool + idx * 8)
    pool2 = o.q(g.exe + CMD_POOL_RVA)
    count2 = struct.unpack("<I", o.read(g.exe + CMD_POOL_RVA + 0x30, 4))[0]
    seen = set()
    while node and node not in seen:
        seen.add(node)
        obj = o.q(node)
        vt = o.q(obj) if heap(obj) else 0
        if not classes.get(vt, "").startswith(".?AVCCmdAction"):
            node = o.q(node + 8)
            continue
        # the kind getter is `mov eax, imm32; ret`, or `mov eax, [rcx + disp]; ret` (CCmdActionSpyInspect: +0x70);
        # anything else is recorded as -1
        kind = -1
        fn = o.q(vt + 0x98)
        if fn and g.exe <= fn < g.exe + g.exe_size:
            code = o.read(fn, 7)
            if code[0] == 0xB8 and code[5] == 0xC3: kind = struct.unpack("<i", code[1:5])[0]
            elif code[0] == 0x8B and code[1] == 0x41 and code[3] == 0xC3: kind = struct.unpack("<i", o.read(obj + code[2], 4))[0]
            elif code[0] == 0x8B and code[1] == 0x81 and code[6] == 0xC3: kind = struct.unpack("<i", o.read(obj + struct.unpack("<i", code[2:6])[0], 4))[0]
        ids = []
        sub = o.q(obj + 0x20)
        if heap(sub):
            k2 = struct.unpack("<I", o.read(sub, 4))[0]
            n2 = o.q(pool2 + k2 * 8) if k2 < count2 else 0
            seen2 = set()
            while n2 and n2 not in seen2:
                seen2.add(n2)
                ids.append(struct.unpack("<I", o.read(n2, 4))[0])
                n2 = o.q(n2 + 8)
        out.append({"kind": kind, "officers": ids})
        node = o.q(node + 8)
    return out


def save(obj, path):
    with gzip.open(path, "wt", encoding="utf-8") as f:
        json.dump(obj, f, separators=(",", ":"))

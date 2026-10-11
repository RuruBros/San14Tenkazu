"""Replay explicit new-force slots on the single authenticated frozen input.

No live access or fabricated force/corps records. Only slots already empty in
the input are used. The recorded copied renderer/history settings are retained;
enabled renderer/history and saving in the live game are separate validation.
"""
import hashlib
import json
import struct
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE / "vectors"), str(HERE / ".cache")]
import game
import context


def forbidden(*args, **kwargs):
    raise RuntimeError("Live game access forbidden")


game.Game = context.Game = forbidden
from context import Context
from engine_audit_state import EngineAuditState
from force_fall_audit_v1 import enrich
from unicorn import UC_HOOK_CODE, UC_PROT_ALL
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RSP

sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)
folder = HERE / ".cache/force_creation_renderer_absent_guarded_input_20261006-070138"
path = folder / "input.pkl"
digest = hashlib.sha256(path.read_bytes()).hexdigest()
if digest != "8cd848271fec8e4d2d2e13bb1c2bd6f9d7cc5abac995bdcc59dafedaeaa267ee":
    raise RuntimeError("Frozen input differs")
report = json.loads((folder / "report.json").read_text())
if not report["complete"]:
    raise RuntimeError("Frozen input guard incomplete")
ctx = Context(str(path))
o = ctx.o
o.insn_limit = 100_000_000
if "--export" in sys.argv:
    reads = []
    def read(address, length):
        value = o.read(address, length)
        reads.append(dict(address=address, hex=value.hex()))
        return value
    def records(table, size, count):
        return [dict(index=i, address=ctx.addr(table, i), hex=read(ctx.addr(table, i), size).hex()) for i in range(count)]
    raw = ctx.raw
    state_address = o.q(raw + 0x871E8)
    cities = records(0xDD38, 0x168, 47)
    officers = records(0x73A50, 0x1F0, len(ctx.rec[0x73A50]))
    forces = records(0xDF30, 0x120, 52)
    corps = records(0xE0D0, 0x30, 52)
    units = records(0x7E838, 0x200, 501)
    for address, length in ((ctx.g.exe + 0x277DF18, 8), (raw + 0x871E8, 8),
                            (state_address + 0x24, 0x18), (raw + 0x158, 6001 * 8),
                            (raw + 0xDD38, 52 * 8), (raw + 0xDF30, 52 * 8), (raw + 0xE0D0, 52 * 8)):
        read(address, length)
    for rva in (0x2D7C80, 0x33CED0, 0x232400, 0x233110):
        read(ctx.g.exe + rva, 0x80)
    export = HERE / ".cache/city-independence-slots-input.json"
    export.write_text(json.dumps(dict(input_sha256=digest, exe=ctx.g.exe, raw=raw, state=state_address,
        cities=cities, officers=officers, forces=forces, corps=corps, units=units, reads=reads)), encoding="utf-8")
    print("Exported immutable preflight input", export)
    sys.exit(0)
audit = EngineAuditState(ctx)
audit.tables[0x79E90] = (ctx.addr(0x79E90, 0), 0x140, 2001)
audit.tables[0x85A90] = (ctx.addr(0x85A90, 0), 0x78, 101)
audit.wrappers.append(("registered_counties", ctx.raw + 0xB0))
enrich(audit, ctx)
baseline = audit.read()
host = report["force_creation_cases"][0]["fixture"]["host_pokes"]
old_host = [(int(p, 16), o.read(int(p, 16), len(bytes.fromhex(b)))) for p, b in host]
slots = [i for i in range(1, 46) if ctx.u16(0xDF30, i, 0x10) == 0]
person, city = ctx.addr(0x73A50, 997), ctx.addr(0xDD38, 28)
event = o.alloc(0x80)
o.poke(event, struct.pack("<Q", event + 0x20))
o.poke(event + 0x2C, struct.pack("<ii", 997, 28))
entries = []


def observe(mu, address, size, user):
    entries.append(dict(rva=hex(address - ctx.g.exe),
                        args=[hex(mu.reg_read(r)) for r in (UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9)],
                        stack=[hex(v) for v in struct.unpack("<Q", o.read(mu.reg_read(UC_X86_REG_RSP) + 0x28, 8))]))


hooks = [o.mu.hook_add(UC_HOOK_CODE, observe, begin=ctx.g.exe + r, end=ctx.g.exe + r)
         for r in (0x39FD00, 0x2D7C80, 0x2A9520, 0x2D76A0, 0x33CED0)]
output = HERE / ".cache/city-independence-slots.json"
product = None
if "--product" in sys.argv:
    product = json.loads(Path(sys.argv[sys.argv.index("--product") + 1]).read_text())
    if product["input_sha256"] != digest or sorted(r["slot"] for r in product["rows"]) != slots:
        raise RuntimeError("Product preflight input or slots differ")
    output = HERE / ".cache/city-independence-slots-product-results.json"
cases = []
guard_cases = []


def state():
    person_bytes = o.read(person, 0xE9)
    city_bytes = o.read(city, 0x52)
    corps = person_bytes[0xE0]
    corps_bytes = o.read(ctx.addr(0xE0D0, corps), 0x18)
    force = corps_bytes[0x10]
    force_bytes = o.read(ctx.addr(0xDF30, force), 0x120)
    owners = []
    for i in range(1, 47):
        c = ctx.addr(0xDD38, i)
        corps_id = struct.unpack("<H", o.read(c + 0x30, 2))[0]
        owners.append(o.read(ctx.addr(0xE0D0, min(corps_id, 51)) + 0x10, 1)[0])
    serving = []
    for i, b in enumerate(ctx.rec[0x73A50]):
        current = o.read(ctx.addr(0x73A50, i), 0xE9)
        if current[0xE0] == corps and current[0xE6] in (1, 2, 3, 4):
            serving.append(struct.unpack_from("<H", current, 0x10)[0])
    return dict(force=force, ruler=struct.unpack_from("<H", force_bytes, 0x10)[0],
                status=person_bytes[0xE6], corps=corps, corps_number=corps_bytes[0x11],
                corps_leader=struct.unpack_from("<H", corps_bytes, 0x12)[0],
                home=struct.unpack_from("<H", person_bytes, 0xE2)[0],
                place=struct.unpack_from("<H", person_bytes, 0xE4)[0],
                city_corps=struct.unpack_from("<H", city_bytes, 0x30)[0],
                governor=struct.unpack_from("<H", city_bytes, 0x2E)[0],
                city_owners=owners, serving=serving)


def save():
    output.write_text(json.dumps(dict(input_sha256=digest, live_game_access=False,
        complete=len(cases) == len(slots) and (product is None or len(guard_cases) == 4), empty_slots=slots, copied_host_settings=host,
        enabled_renderer_history_verified=False, product_bodies=product is not None,
        cases=cases, guard_cases=guard_cases), indent=2), encoding="utf-8")


try:
    for p, b in host:
        o.poke(int(p, 16), bytes.fromhex(b))
    original = state()
    captured = []
    def after_event():
        captured.append((state(), audit.read()))
        return []
    if o.call(0x39FD00, 0, event, read_after=after_event) != 1:
        raise RuntimeError("Reference event failed")
    reference_state, reference_audit = captured[0]
    print("Reference automatic event", reference_state, flush=True)
    for slot in slots:
        entries.clear()
        captured.clear()
        def after_sequence():
            captured.append((state(), audit.read()))
            return []
        # One ABI adapter calls the two real native functions. It does not patch
        # native code or manufacture records; Oracle rolls back the whole call.
        body = bytearray.fromhex("534883EC30")
        def imm(op, value):
            body.extend(bytes.fromhex(op) + struct.pack("<Q", value))
        imm("48B8", slot)
        body.extend(bytes.fromhex("4889442420"))
        for op, value in (("48B9", person), ("48BA", city), ("49B8", 1), ("49B9", 0), ("48B8", ctx.g.exe + 0x2D7C80)):
            imm(op, value)
        body.extend(bytes.fromhex("FFD0"))
        for op, value in (("48B9", 0), ("48BA", 0), ("49B8", 0), ("49B9", 0), ("48B8", ctx.g.exe + 0x33CED0)):
            imm(op, value)
        body.extend(bytes.fromhex("FFD0B801000000EB0233C04883C4305BC3"))
        if product:
            body = bytes.fromhex(next(r["body"] for r in product["rows"] if r["slot"] == slot))
        wrapper = o.alloc(len(body))
        o.poke(wrapper, bytes(body))
        if o.call(wrapper - ctx.g.exe, read_after=after_sequence) != 1:
            raise RuntimeError("Native creation sequence failed")
        created, final = captured[0]
        if created["force"] != slot:
            raise RuntimeError("Explicit slot was not used")
        if not (created["ruler"] == created["governor"] == created["corps_leader"] == 997
                and created["status"] == 1 and created["home"] == created["place"] == 28
                and created["corps"] == created["city_corps"] and created["corps_number"] == 1
                and created["serving"] == reference_state["serving"]
                and all(original["city_owners"][i] == v for i, v in enumerate(created["city_owners"]) if i != 27)):
            raise RuntimeError("Created ruler/corps/city/membership differs")
        if slot == reference_state["force"] and final != reference_audit:
            raise RuntimeError("Direct creation and refresh differ from the original event")
        cases.append(dict(slot=slot, function_complete=True, after=created,
                          changed_records=len(audit.delta(baseline, final)["records"]), entries=list(entries),
                          matches_reference_event=slot == reference_state["force"]))
        save()
        print("Slot", slot, "passed; officers", len(created["serving"]), flush=True)
        if audit.read() != baseline:
            raise RuntimeError("Case did not restore the audited frozen state")
    if product:
        row = next(r for r in product["rows"] if r["slot"] == 37)
        wrapper = o.alloc(0x2000)
        o.poke(wrapper, bytes.fromhex(row["body"]))
        for name, address, replacement in (
            ("slot-became-occupied", row["forceAddress"] + 0x10, struct.pack("<H", 1)),
            ("governor-left-city", person + 0xE4, struct.pack("<H", 29)),
            ("empty-corps-became-occupied", ctx.addr(0xE0D0, 2) + 0x11, bytes([1])),
        ):
            old = o.read(address, len(replacement))
            try:
                o.poke(address, replacement)
                expected = audit.read()
                entries.clear()
                if o.call(wrapper - ctx.g.exe) != 0 or entries or audit.read() != expected:
                    raise RuntimeError("Frame guard did not reject without native calls/writes: " + name)
                guard_cases.append(dict(name=name, passed=True, native_calls=0, audited_state_unchanged=True))
                print("Guard", name, "passed", flush=True)
            finally:
                o.poke(address, old)
        code, data = 0x6000000000, 0x6000003000
        if ctx.g.region(code)[2] != 0x10000 or any(p in o.pages for p in range(code, code + 0x4000, 0x1000)):
            raise RuntimeError("Frame replay scratch range is not free")
        o.mu.mem_map(code, 0x4000, UC_PROT_ALL)
        o.pages.update(range(code, code + 0x4000, 0x1000))
        o.own.update(range(code, code + 0x4000, 0x1000))
        o.poke(code, bytes.fromhex(row["frame"]))
        o.poke(code + 0x400, bytes.fromhex(row["body"]))
        from oracle import SENTINEL
        o.poke(data, bytes(8) + struct.pack("<Q", SENTINEL))
        entries.clear()
        captured.clear()
        o.call(code - ctx.g.exe, read_after=after_sequence)
        if (o.read(data, 1) != bytes([2]) or struct.unpack("<i", o.read(data + 4, 4))[0] != 1
                or captured[0][0]["force"] != 37 or sum(e["rva"] == "0x2d7c80" for e in entries) != 1):
            raise RuntimeError("Exact frame stub did not create the explicit force once")
        entries.clear()
        o.call(code - ctx.g.exe)
        if entries or audit.read() != baseline:
            raise RuntimeError("Second frame entry repeated native creation or changed state")
        guard_cases.append(dict(name="exact-frame-stub-first-and-second-entry", passed=True, creation_calls=1))
        print("Exact frame stub first/second entries passed", flush=True)
        save()
    print("Passed", len(cases), "originally empty ordinary slots; evidence", output, flush=True)
finally:
    for p, b in reversed(old_host):
        o.poke(p, b)
    for hook in hooks:
        o.mu.hook_del(hook)
    o.unstub()
    if audit.read() != baseline or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
        raise RuntimeError("Frozen input or audit state changed")

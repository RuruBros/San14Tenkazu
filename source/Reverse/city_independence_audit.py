"""Execute the scenario new-force event on one authenticated frozen input.

No live process access, gameplay/presentation/allocator stubs or merged pages.
The copied renderer-absent and history-suppressed host settings are explicit;
this evidence does not validate the live game's enabled renderer/history.
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
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RSP

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
audit = EngineAuditState(ctx)
enrich(audit, ctx)
baseline = audit.read()
host = report["force_creation_cases"][0]["fixture"]["host_pokes"]
old_host = [(int(p, 16), o.read(int(p, 16), len(bytes.fromhex(b)))) for p, b in host]
event = o.alloc(0x80)
o.poke(event, struct.pack("<Q", event + 0x20))
o.poke(event + 0x2C, struct.pack("<ii", 997, 28))
entries = []


def observe(mu, address, size, user):
    entries.append(dict(rva=hex(address - ctx.g.exe),
                        args=[hex(mu.reg_read(r)) for r in (UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9)],
                        stack=[hex(v) for v in struct.unpack("<Q", o.read(mu.reg_read(UC_X86_REG_RSP) + 0x28, 8))]))


hooks = [o.mu.hook_add(UC_HOOK_CODE, observe, begin=ctx.g.exe + r, end=ctx.g.exe + r)
         for r in (0x3BF120, 0x39FD00, 0x2D7C80, 0x2A9520, 0x33CED0)]
try:
    for p, b in host:
        o.poke(int(p, 16), bytes.fromhex(b))
    results = []
    def after():
        def record(table, index, size):
            return o.read(ctx.addr(table, index), size)
        person = record(0x73A50, 997, 0xE9)
        city = record(0xDD38, 28, 0x50)
        corps = record(0xE0D0, person[0xE0], 0x18)
        force = record(0xDF30, corps[0x10], 0x120)
        results.append(dict(person=997, status=person[0xE6], corps=person[0xE0],
                            home=struct.unpack_from("<H", person, 0xE2)[0],
                            place=struct.unpack_from("<H", person, 0xE4)[0],
                            city_corps=struct.unpack_from("<H", city, 0x30)[0],
                            governor=struct.unpack_from("<H", city, 0x2E)[0],
                            force=corps[0x10], ruler=struct.unpack_from("<H", force, 0x10)[0],
                            changed_records=len(audit.delta(baseline, audit.read())["records"])))
        return []
    # 0x3BF120's verified edit-mode guards delegate to this exact scenario body.
    # The frozen input lacks its static event-instance page; do not merge another
    # scene's page or manufacture that instance to claim full dispatcher coverage.
    result = o.call(0x39FD00, 0, event, read_after=after)
    if result != 1 or len(results) != 1:
        raise RuntimeError("Edit-mode event did not finish")
    state = results[0]
    if not (state["status"] == 1 and state["force"] == 44 and state["ruler"] == 997
            and state["governor"] == 997 and state["home"] == state["place"] == 28
            and state["city_corps"] == state["corps"]):
        raise RuntimeError("New ruler/city ties differ")
    expected = ["0x39fd00", "0x2d7c80", "0x2a9520", "0x33ced0"]
    if [e["rva"] for e in entries] != expected:
        raise RuntimeError("Unexpected native event call order")
    creation = entries[1]
    if creation["args"][2:] != ["0x1", "0x0"] or creation["stack"] != ["0x0"]:
        raise RuntimeError("Edit-mode creation arguments differ")
    output = HERE / ".cache/city-independence-event.json"
    output.write_text(json.dumps(dict(input_sha256=digest, complete=True, live_game_access=False,
                                     copied_host_settings=host, entries=entries, result=result, after=state,
                                     edit_guard_dispatcher_verified=False,
                                     enabled_renderer_history_verified=False), indent=2), encoding="utf-8")
    print(json.dumps(dict(complete=True, after=state, evidence=str(output))), flush=True)
finally:
    for p, b in reversed(old_host):
        o.poke(p, b)
    for hook in hooks:
        o.mu.hook_del(hook)
    o.unstub()
    if audit.read() != baseline or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
        raise RuntimeError("Frozen test input was not restored")

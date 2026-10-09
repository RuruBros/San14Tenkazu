# Player selection removal: execute the real leaf and record host lookup/refresh calls separately.
import struct
from context import *
from engine_audit_state import EngineAuditState
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX

COVER = [0x29C870, 0x32E640]


def generate(ctx, include_ui=False):
    o = ctx.o
    audit = EngineAuditState(ctx)
    snapshot = audit.snapshot
    person = next(i for i, b in enumerate(ctx.rec[OFFICERS]) if i and ctx.u16(OFFICERS, i, 0x10))
    identifier = ctx.u16(OFFICERS, person, 0x10)
    other = next(ctx.u16(OFFICERS, i, 0x10) for i in range(1, len(ctx.rec[OFFICERS]))
                 if ctx.u16(OFFICERS, i, 0x10) not in [0, identifier])
    slots = [identifier, other, identifier, 6001, 0]
    common = [['worldstate', 0, 0x165D, '05'], ['worldstate', 0, 0x165E, struct.pack('<5H', *slots).hex()],
              ['worldstate', 0, 0x1668, struct.pack('<5i', -1, 20, 31, 42, 53).hex()]]
    fixtures = [dict(name='invalid_officer', person=0, pokes=common),
                dict(name='empty_selection', person=person, pokes=common+[['worldstate', 0, 0x165D, '00']]),
                dict(name='missing_selection', person=person, pokes=common+[['worldstate', 0, 0x165E,
                    struct.pack('<5H', other, other, 6001, 0, other).hex()]]),
                dict(name='two_matching_slots', person=person, pokes=common)]
    active, hooks = [None], []
    def event(kind):
        def trace(mu, address, size, data):
            if active[0] is not None:
                active[0].append(dict(kind=kind, receiver=hex(mu.reg_read(UC_X86_REG_RCX)),
                                     selection=o.read(audit.ws+0x165D, 31).hex()))
        return trace
    for rva, kind in [(0x110E0, 'host_lookup'), (0x897530, 'refresh')]:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, event(kind), begin=ctx.g.exe+rva, end=ctx.g.exe+rva))
    # The UI repaint sorts a temporary list and writes graphical panel objects; those physical objects
    # are outside the formula core. The real TLS registry and panel-availability queries still execute.
    # include_ui keeps the complete diagnostic path, including any native UI allocation failure.
    if not include_ui: o.stub(0x897530)
    original = audit.read()
    cases = []
    try:
        for f in fixtures:
            def run():
                before, after, events = audit.read(), [], []
                active[0] = events
                try:
                    o.call(0x29C870, ctx.addr(OFFICERS, f['person']), read_after=lambda: after.append(audit.read()) or [])
                    return dict(fixture=f, after=audit.delta(before, after[0]), events=events)
                finally:
                    active[0] = None
            c = ctx.run(f['pokes'], run)
            if c is None: raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
            cases.append(c)
            print(f'  selection {f["name"]}: {len(c["events"])} host events', flush=True)
    finally:
        o.unstub()
        for hook in hooks: o.mu.hook_del(hook)
    if audit.read() != original: raise RuntimeError('Selection fixtures did not restore input')
    ctx.save('officer_selection_world.json.gz', snapshot)
    ctx.save('officer_selection.json.gz', dict(world='officer_selection_world.json.gz', cases=cases,
        gameplay_stubs=[], presentation=[] if include_ui else ['0x897530'], native_entry='0x29c870',
        boundary='Physical selection-panel repaint; actual host registry lookup and availability branches execute.'))
    return f'officer selection: {len(cases)} bodies'

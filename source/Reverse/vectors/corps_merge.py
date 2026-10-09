# Execute the complete corps merger/dissolution body on copied frozen memory.
# Command cancellation and player presentation are explicit unsupported branches in the C# port.
import collections
import struct
import time
from context import *
from engine_audit_state import EngineAuditState
from engagement_tactic_sequence import PRESENTATION
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8

COVER = [0x293050, 0x240CB0, 0x240C90, 0x32AEC0, 0x226A00, 0xA20E0, 0x2EDF20,
         0x32AFE0, 0x226C10, 0x2827C0, 0x26CC10, 0x334780, 0x2311B0]


def generate(ctx, names=None):
    o = ctx.o
    o.insn_limit = 300_000_000
    audit = EngineAuditState(ctx)
    snapshot = audit.snapshot
    refs = {ctx.addr(t, i): [hex(t), i] for t, (_, _, count) in audit.tables.items() for i in range(count)}
    for t in snapshot['tables']:
        snapshot['tables'][t]['address'] = hex(ctx.addr(int(t, 16), 0))
    def list_items(head):
        return [[refs[int(p, 16)]] for p in audit.members(o.q(ctx.raw + head))]
    for head in [0xA0, 0xD0, 0x120]:
        snapshot['lists'][hex(head)] = {'items': list_items(head)}
    def corps_cache():
        manager = o.q(ctx.g.exe + 0x1B9A4A0)
        header = o.q(manager + 0x90)
        todo, seen, pairs = [o.q(header + 8)], set(), {}
        while todo:
            n = todo.pop()
            if o.read(n + 0x19, 1)[0]: continue
            if n in seen: raise RuntimeError('Corps cache cycle')
            seen.add(n)
            pairs[hex(o.q(n + 0x20))] = hex(o.q(n + 0x28))
            todo.extend([o.q(n), o.q(n + 0x10)])
        if len(pairs) != o.q(manager + 0x98): raise RuntimeError('Corps cache count differs')
        return pairs
    def state():
        out = audit.read()
        out['lists'].update({hex(h): list_items(h) for h in [0xA0, 0xD0, 0x120]})
        out['corps_cache'] = corps_cache()
        return out
    initial = state()
    snapshot['corps_cache'] = initial['corps_cache']
    snapshot['nested'] = initial['nested']
    pool_enabled = o.q(ctx.g.exe + 0x27D10E8) != 0
    fixtures = []
    common = [['worldstate', 0, 0x3A, '00']]
    def fixture(name, source=3, destination=9, mode=0, pokes=None):
        fixtures.append(dict(name=name, source=[hex(CORPS), source],
            destination=[hex(CORPS), destination] if destination is not None else None,
            mode=mode, pokes=common + (pokes or [])))
    def poke(t, i, at, value, fmt='B'):
        return [hex(t), i, at, struct.pack('<' + fmt, value).hex()]
    fixture('merge_cross_force')
    fixture('merge_same_force', pokes=[poke(CORPS, 9, 0x10, 9)])
    fixture('mode_one', mode=1)
    fixture('same_corps', destination=3)
    fixture('dissolve_null', destination=None)
    fixture('dissolve_zero', destination=0)
    fixture('invalid_destination', pokes=[poke(CORPS, 9, 0x11, 0)])
    fixture('empty_source', source=4)
    fixture('source_zero', source=0)
    for status in [1, 2, 3, 6]:
        fixture(f'leader_status_{status}_mode_one', mode=1, pokes=[poke(OFFICERS, 266, 0xE6, status)])
    fixture('officer_statuses', pokes=[poke(OFFICERS, 185, 0xE6, 6), poke(OFFICERS, 191, 0xE6, 5),
        poke(OFFICERS, 261, 0xE6, 7)])
    fixture('force_change_counters', pokes=[poke(OFFICERS, 261, 0x1C6, 0x563412, 'I')])
    fixture('same_force_counters', pokes=[poke(CORPS, 9, 0x10, 9), poke(OFFICERS, 261, 0x1C6, 0x563412, 'I')])
    fixture('envoy_invalid_host', pokes=[poke(FORCES, 9, 0x196, 261, 'H'), poke(FORCES, 9, 0x19C, 0)])
    for rank in [1, 4, 6, 0]:
        fixture(f'budget_rank_{rank}', pokes=[poke(FORCES, 29, 0x47, rank),
            poke(CORPS, 3, 0x24, 180000, 'i'), poke(CORPS, 9, 0x24, 100000, 'i')])
    fixture('budget_overflow', pokes=[poke(CORPS, 3, 0x24, 2147483647, 'i'), poke(CORPS, 9, 0x24, 1, 'i')])
    fixture('budget_negative_source', pokes=[poke(CORPS, 3, 0x24, -1, 'i')])
    fixture('reference_orders', pokes=[poke(CORPS, 9, 0x18, 3), poke(CORPS, 9, 0x19, 8),
        poke(CORPS, 9, 0x1C, 0x80000000, 'I'), poke(CORPS, 9, 0x20, 0x1234, 'H'), poke(CORPS, 9, 0x22, 7)])
    fixture('caution_and_troop', pokes=[poke(0x80BA8, 1, 0x14, 3, 'i'),
        poke(0x7F8D8, 1, 0x14, 1, 'H'), poke(0x7F8D8, 1, 0x16, 1, 'H'),
        poke(0x7F8D8, 2, 0x14, 2, 'H'), poke(0x7F8D8, 2, 0x16, 1, 'H')])
    fixture('invalid_caution', pokes=[poke(0x80BA8, 1, 0x14, 3, 'i'), poke(0x80BA8, 1, 0x18, 0, 'H')])
    fixture('additional_city_dissolution', destination=None, pokes=[poke(0xDD38, 2, 0x30, 3)])
    # Native 0x22A7B0 resolves gate 1 to city 6 in this input. City 2 has no associated gate.
    gate_city = (o.call(0x22A7B0, ctx.addr(0xDED8, 1)) - ctx.addr(0xDD38, 0)) // snapshot['tables']['0xdd38']['size']
    fixture('gate_parent_dissolution', destination=None, pokes=[poke(0xDD38, gate_city, 0x30, 3)])
    fixture('negative_gate_capacity', destination=None, pokes=[poke(0xDD38, gate_city, 0x30, 3),
        ['exe', 'capacity_extra', 0, struct.pack('<i', -100000).hex()]])
    building = ctx.u16(0xDED8, 1, 0x4E)
    tile = ctx.u16(0x6DA98, building, 0x10)
    county = ctx.u16(TILES, tile, 0x12)
    center = ctx.u16(0x6CAF0, county, 0x3A)
    fixture('tribe_gate_morale', destination=None, pokes=[poke(0xDD38, gate_city, 0x30, 3), poke(TILES, center, 0x14, 46)])
    fixture('negative_capacity_dissolution', destination=None, pokes=[
        ['exe', 'capacity_extra', 0, struct.pack('<i', -100000).hex()], poke(0xDD38, 1, 0xB8, 1)])
    if names is not None:
        fixtures = [f for f in fixtures if f['name'] in names]
        if {f['name'] for f in fixtures} != set(names): raise ValueError('Unknown corps fixture')
    active, hooks = [None], []
    def entry(rva):
        def trace(mu, address, size, data):
            if active[0] is None: return
            values = [mu.reg_read(r) for r in [UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8]]
            active[0].append(dict(rva=hex(rva), args=[refs.get(v, hex(v)) for v in values]))
        return trace
    for rva in COVER[1:]:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, entry(rva), begin=ctx.g.exe+rva, end=ctx.g.exe+rva))
    presentation = [*PRESENTATION, 0x1B4990]
    for rva in presentation: o.stub(rva)
    cases, stats = [], collections.Counter()
    ptr = lambda r: ctx.addr(int(r[0], 16), r[1]) if r is not None else 0
    try:
        for f in fixtures:
            started = time.time()
            def run():
                before, after, events = state(), [], []
                active[0] = events
                def inspect(): after.append(state()); return []
                try:
                    result = o.call(0x293050, ptr(f['source']), ptr(f['destination']), f['mode'], 0, read_after=inspect)
                    return result, before, after[0], events
                finally: active[0] = None
            observed = ctx.run(f['pokes'], run)
            if observed is None: raise RuntimeError(f'Corps merger failed: {f["name"]}: {ctx.failures[-1]}')
            result, before, after, events = observed
            if any(e['rva'] == '0x334780' for e in events):
                raise RuntimeError('Fixture reached unsupported officer command cancellation')
            changes = audit.delta(before, after)
            cases.append(dict(fixture=f, result=result, after=changes, events=events))
            stats['cases'] += 1
            for e in events: stats[e['rva']] += 1
            print(f'  corps {f["name"]}: {len(changes["records"])} records, sections {list(changes)} ({time.time()-started:.2f}s)', flush=True)
    finally:
        o.unstub()
        for h in hooks: o.mu.hook_del(h)
    ctx.save('corps_merge_world.json.gz', snapshot)
    ctx.save('corps_merge.json.gz', dict(world='corps_merge_world.json.gz', cases=cases, stats=dict(stats),
        pool_enabled=pool_enabled, presentation=[hex(r) for r in presentation], gameplay_stubs=[],
        frozen_input=ctx.frozen, scope='Complete 293050 body without active officer commands or player presentation; merge and invalid-destination dissolution'))
    return f'corps lifecycle: {dict(stats)}'

# Execute ordinary officer capture in surviving forces/corps, without gameplay stubs.
# Use an explicit named output and frozen dump; this module never publishes the shared world snapshot.
import collections
import struct
import time
from context import *
from engine_audit_state import EngineAuditState
from engagement_tactic_sequence import PRESENTATION
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_RSP, UC_X86_REG_RAX

COVER = [0x2E0CF0, 0x29BFF0, 0x2E7E90, 0x2E8E70, 0x2827C0, 0x26CC10, 0x277F60, 0x2E7D00,
         0x27B9F0, 0x27A5F0, 0x2FED20, 0x241E20]


def generate(ctx, names=None):
    o = ctx.o
    o.insn_limit = 300_000_000
    audit = EngineAuditState(ctx)
    snapshot = audit.snapshot
    for head in [0xA0, 0xB0]:
        snapshot['lists'][hex(head)] = {'items': world.list_items(o, ctx.raw, ctx.g.exe, head,
            {'pool': world.POOL_RVA, 'next': 1, 'fields': 1})}
    vectors = {}
    # Capture external input arrays independently of results. Unavailable arrays in an older frozen dump are not
    # invented; a fixture touching one fails below. The affected officers' entire county arrays are compared.
    for i, data in enumerate(ctx.rec[OFFICERS]):
        begin, end = struct.unpack_from('<QQ', data, 0x1D0)
        if not begin or end < begin or end - begin > 501 * 4: continue
        if ctx.frozen and ctx.g.read(begin, end - begin) is None: continue
        value = o.read(begin, end - begin)
        vectors[i] = (begin, len(value))
        snapshot['tables'][hex(OFFICERS)]['extra'].setdefault(str(i), {})['vec1D0'] = value.hex()
    ref = lambda t, i: [hex(t), i]
    ptr = lambda r: ctx.addr(int(r[0], 16), r[1]) if r else 0
    refs = {ctx.addr(t, i): ref(t, i) for t, (_, _, count) in audit.tables.items() for i in range(count)}
    attacker = next(i for i in range(1, 501) if ctx.rec[UNITS][i][0x10]
                    and ctx.u16(UNITS, i, 0x12) and ctx.officer_force(ctx.officer_by_id(ctx.u16(UNITS, i, 0x12))) != 9)
    captor = ctx.officer_by_id(ctx.u16(UNITS, attacker, 0x12))
    force = ctx.officer_force(captor)
    fixtures = []
    common = [['worldstate', 0, 0x3A, '00']]
    def fixture(name, person=261, unit=None, captor_force=None, officer=None, pokes=None):
        fixtures.append(dict(name=name, person=ref(OFFICERS, person), unit=unit,
            force=captor_force, officer=officer, pokes=common + (pokes or [])))
    source = ref(UNITS, attacker)
    # The officers below were actually captured by the certified city-capture/overflow run.
    for person in [185, 191, 261, 473, 493, 710, 812, 813, 925]:
        fixture(f'ordinary_{person}', person, source)
    fixture('invalid_officer_zero', 0, source)
    fixture('no_captor')
    fixture('captor_force', captor_force=ref(FORCES, force))
    fixture('captor_officer', officer=ref(OFFICERS, captor))
    fixture('invalid_unit_then_force', unit=ref(UNITS, 0), captor_force=ref(FORCES, force))
    fixture('commander_at_base', unit=source,
        pokes=[[hex(OFFICERS), captor, 0xE4, struct.pack('<H', ctx.u16(OFFICERS, captor, 0xE2)).hex()]])
    for status in [2, 3]:
        corps = ctx.rec[OFFICERS][261][0xE0]
        fixture(f'corps_leader_status_{status}', unit=source,
            pokes=[[hex(OFFICERS), 261, 0xE6, bytes([status]).hex()],
                   [hex(CORPS), corps, 0x11, '02'], [hex(CORPS), corps, 0x12, struct.pack('<H', 261).hex()]])
    for field in range(5):
        original_force = ctx.officer_force(261)
        fixture(f'post_field_{field}', unit=source,
            pokes=[[hex(FORCES), original_force, 0x54 + field * 30, struct.pack('<H', 261).hex()],
                   [hex(FORCES), original_force, 0x62 + field * 30, '0000']])
    fixture('advisor', unit=source, pokes=[[hex(FORCES), ctx.officer_force(261), 0x52, '0501']])
    fixture('envoy', unit=source, pokes=[[hex(FORCES), ctx.officer_force(261), 0x196, '0501'],
                                     [hex(FORCES), ctx.officer_force(261), 0x19C, bytes([force]).hex()]])
    fixture('envoy_invalid_host', unit=source, pokes=[[hex(FORCES), ctx.officer_force(261), 0x196, '0501'],
                                                  [hex(FORCES), ctx.officer_force(261), 0x19C, '00']])
    fixture('city_assignments', unit=source, pokes=[['0xdd38', 1, 0xCA, '05010501']])
    fixture('county_count_zero', unit=source, pokes=[[hex(OFFICERS), 261, 0x1E8, '00000000']])
    if names is not None:
        fixtures = [f for f in fixtures if f['name'] in names]
        if {f['name'] for f in fixtures} != set(names): raise ValueError('Unknown officer capture fixture name')
    active, hooks = [None], []
    def entry(rva):
        def trace(mu, address, size, data):
            if active[0] is None: return
            args = [mu.reg_read(r) for r in [UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8]]
            event = dict(rva=hex(rva), args=[refs.get(x, hex(x)) for x in args])
            active[0].append(event)
            if rva == 0x2827C0:
                rsp = mu.reg_read(UC_X86_REG_RSP)
                retaddr = o.q(rsp)
                def returned(mu, address, size, data):
                    if mu.reg_read(UC_X86_REG_RSP) != rsp + 8: return
                    event['selected'] = refs.get(mu.reg_read(UC_X86_REG_RAX))
                    mu.hook_del(holder[0])
                holder = [o.mu.hook_add(UC_HOOK_CODE, returned, begin=retaddr, end=retaddr)]
                hooks.append(holder[0])
        return trace
    for rva in COVER[1:]:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, entry(rva), begin=ctx.g.exe+rva, end=ctx.g.exe+rva))
    presentation = [*PRESENTATION, 0x1B4990]
    for rva in presentation: o.stub(rva)
    cases, stats = [], collections.Counter()
    def state():
        out = audit.read()
        out['officer_counties'] = {str(i): o.read(a, n).hex() for i, (a, n) in vectors.items()}
        return out
    try:
        for f in fixtures:
            began = time.time()
            i = f['person'][1]
            if struct.unpack_from('<i', ctx.rec[OFFICERS][i], 0x1E8)[0] and i not in vectors:
                raise RuntimeError(f'Frozen input lacks the officer county array: {i}')
            def run():
                before = state()
                events, after = [], []
                active[0] = events
                def inspect():
                    after.append(state())
                    return []
                try:
                    result = o.call(0x2E0CF0, ptr(f['person']), ptr(f['unit']), 0, 0,
                        stack_args=[ptr(f['force']), ptr(f['officer'])], read_after=inspect)
                    return result, before, after[0], events
                finally: active[0] = None
            observed = ctx.run(f['pokes'], run)
            if observed is None: raise RuntimeError(f'Officer capture failed: {f["name"]}: {ctx.failures[-1]}')
            result, before, after, events = observed
            changes = audit.delta(before, after)
            # Retain only changed external arrays; the original values are independently present in the world.
            if 'officer_counties' in changes:
                changes['officer_counties'] = {i: value for i, value in after['officer_counties'].items()
                                             if value != before['officer_counties'][i]}
            cases.append(dict(fixture=f, result=result, after=changes, events=events))
            stats['cases'] += 1
            for e in events: stats[e['rva']] += 1
            print(f'  officer capture {f["name"]}: result {result}, {len(changes["records"])} changed records ({time.time()-began:.2f}s)', flush=True)
    finally:
        o.unstub()
        # Entry hooks persist for this module only; the return hooks already deleted themselves.
        for hook in hooks[:len(COVER)-1]: o.mu.hook_del(hook)
    ctx.save('officer_capture_world.json.gz', snapshot)
    evidence = getattr(ctx.g, 'immutable_code_evidence', None)
    if evidence: evidence = {k: v for k, v in evidence.items() if k != 'added_text_rvas'} | {'added_text_pages': len(evidence['added_text_rvas'])}
    ctx.save('officer_capture.json.gz', dict(world='officer_capture_world.json.gz', cases=cases, stats=dict(stats),
        presentation=[hex(r) for r in presentation], gameplay_stubs=[],
        immutable_code_evidence=evidence,
        scope='Ordinary officers, notification/dialog zero, non-player original force, surviving force/corps; full native body and recorded gameplay state'))
    return f'officer capture bodies: {dict(stats)}'

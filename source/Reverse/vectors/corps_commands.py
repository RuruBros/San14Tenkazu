# Complete corps merger/dissolution including real active-command cancellation on the frozen copy.
import copy
import gzip
import json
import struct
import time
from context import *
from engine_audit_state import EngineAuditState
from command_storage import read_store
from command_cancel import SIZES
from engagement_tactic_sequence import PRESENTATION
from unit_finalization import capture_native_pools
from destruction_overflow import require_unchanged_snapshot
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9

COVER = [0x293050, 0x334780, 0x1D7DA0, 0x1F6720, 0x1F22C0]


def generate(ctx, fixture_factory=None, entry=0x293050, stem='corps_commands', capture_free_lists=False, extra_tables=()):
    o = ctx.o
    officer_entry = entry in [0x2E0CF0, 0x264DD0, 0x33BC00, 0x2E75C0, 0x2EA6D0, 0x258EE0, 0x295AA0, 0x2BDD50]
    unit_host_entry = entry == 0x258EE0
    free_manager = entry == 0x33BC00
    o.insn_limit = 500_000_000
    pools = [(0x1B5C990,24,8)]
    if entry in [0x2E75C0,0x2EA6D0,0x258EE0,0x295AA0]: pools.append((0x2143270,40,24))
    capture_native_pools(ctx,pools)
    audit = EngineAuditState(ctx)
    for table, size, count in extra_tables:
        first = ctx.addr(table, 0)
        if any(ctx.addr(table, i) != first + i * size for i in range(count)):
            raise RuntimeError(f'Extra audit table {table:#x} is not contiguous')
        data = o.read(first, size * count)
        audit.tables[table] = (first, size, count)
        audit.snapshot['tables'][hex(table)] = dict(size=size, address=hex(first), extra={},
            records=[data[i*size:(i+1)*size].hex() for i in range(count)])
    if entry in [0x264DD0, 0x33BC00] or capture_free_lists:
        audit.wrappers.extend((f'force_deferred:{i}', ctx.addr(FORCES, i)+0x260) for i in range(52))
        audit.wrappers.append(('free_officers', o.q(ctx.g.exe+0x277E028)+0x18))
    snapshot = copy.deepcopy(audit.snapshot)
    if unit_host_entry: snapshot['personality_refresh_available'] = bool(o.q(ctx.g.exe+0x1B91DC0))
    if free_manager: snapshot['nested'] = {name: audit.members(o.q(address)) for name,address in audit.wrappers}
    if free_manager: snapshot['free_manager'] = o.read(o.q(ctx.g.exe+0x277E028), 0x140).hex()
    guard = copy.deepcopy(snapshot)
    initial_rng = o.read(ctx.g.exe + 0x1A47B90, 4)
    if entry == 0x2BDD50: snapshot['initial_rng'] = initial_rng.hex()
    store = read_store(ctx, audit)
    commands = []
    for address, info in zip(store['active'], snapshot['cmdactions']):
        pointer = int(address, 16); vt = o.q(pointer)
        if info['kind'] not in SIZES: raise RuntimeError('An unknown command class requires analysis first')
        commands.append(dict(info, address=address, data=o.read(pointer, SIZES[info['kind']]).hex(),
            handlers={hex(at): hex(o.q(vt+at)-ctx.g.exe) for at in [0x18,0x38,0x68,0x90,0xA8,0xB0,0xC8,0xD8,0xF0,0x110,0x118]}))
    snapshot.update(commands=commands, command_storage=store)
    refs = {ctx.addr(t, i): [hex(t), i] for t, (_, _, count) in audit.tables.items() for i in range(count)}
    list_heads = [0xA0, 0xD0, 0x120] + ([0xB0] if officer_entry else [])
    for head in list_heads:
        snapshot['lists'][hex(head)] = {'items': [[refs[int(p, 16)]] for p in audit.members(o.q(ctx.raw + head))]}
    county_vectors = {}
    if officer_entry:
        for i, data in enumerate(ctx.rec[OFFICERS]):
            begin, end = struct.unpack_from('<QQ', data, 0x1D0)
            if not begin or end < begin or end - begin > 501 * 4: continue
            if ctx.frozen and ctx.g.read(begin, end - begin) is None: continue
            value = o.read(begin, end - begin) if end != begin else b''
            county_vectors[str(i)] = (begin, len(value))
            snapshot['tables'][hex(OFFICERS)]['extra'].setdefault(str(i), {})['vec1D0'] = value.hex()
    active = {pid: c for c in reversed(commands) if struct.unpack_from('<i', bytes.fromhex(c['data']), 0x58)[0] != -1
              for pid in c['officers']}
    candidates = []
    for index in range(1, 46):
        corps = ctx.rec[CORPS][index]
        if not corps[0x10] or not corps[0x11] or not struct.unpack_from('<H', corps, 0x12)[0]: continue
        running = {active[ctx.u16(OFFICERS, i, 0x10)]['address'] for i, person in enumerate(ctx.rec[OFFICERS])
                   if person[0xE0] == index and person[0xE6] in [1,2,3,4] and ctx.u16(OFFICERS,i,0x10) in active}
        destination = next((i for i in range(1,46) if i != index and ctx.rec[CORPS][i][0x10] == corps[0x10]
                            and ctx.rec[CORPS][i][0x11] and ctx.u16(CORPS,i,0x12)), None)
        if running and destination is not None: candidates.append((len(running), index, destination, running))
    if not candidates: raise RuntimeError('This input has no active corps officer commands')
    _, source, destination, running = min(candidates)
    fixtures = [dict(name=name, source=source, destination=target, mode=0,
                     pokes=[['worldstate', 0, 0x3A, '00']])
                for name, target in [('merge_active_command', destination), ('dissolve_active_command', 0)]]
    if fixture_factory is not None:
        fixtures = fixture_factory(ctx, snapshot, source, destination)
        if any(f.get('inherits') for f in fixtures):snapshot['initial_rng']=initial_rng.hex()
        if entry == 0x295AA0 and any(f['destroy_own_unit'] for f in fixtures):
            unit_host_entry=True
            snapshot['personality_refresh_available']=bool(o.q(ctx.g.exe+0x1B91DC0))
        corps_indices = {f['source'] for f in fixtures if f['source'] is not None}
        officers = {ctx.u16(OFFICERS, i, 0x10) for i,p in enumerate(ctx.rec[OFFICERS]) if p[0xE0] in corps_indices}
        running |= {c['address'] for c in commands if officers.intersection(c['officers'])}
    for command in commands:
        if command['address'] not in running: continue
        pointer = int(command['address'], 16)
        if command['handlers']['0xd8'] != '0x22b1c0': continue
        wrapper = o.q(pointer+0x38); slot = struct.unpack('<I',o.read(wrapper,4))[0]
        node = o.q(o.q(ctx.g.exe+0x2143270+0x10)+slot*8); targets=[]
        while node:
            a,kind,kindb,b = struct.unpack('<QiiQ',o.read(node,24))
            if a not in refs or b not in refs: raise RuntimeError('The active destination requires a new object resolver')
            targets.append(dict(primary=refs[a],primary_kind=kind,secondary=refs[b],secondary_kind=kindb)); node=o.q(node+24)
        command['destinations']=targets
    print(f'  selected corps {source} -> {destination}: {len(running)} active command(s)', flush=True)
    def cache():
        manager = o.q(ctx.g.exe + 0x1B9A4A0)
        header = o.q(manager + 0x90)
        todo, seen, pairs = [o.q(header + 8)], set(), {}
        while todo:
            node = todo.pop()
            if o.read(node + 0x19, 1)[0]: continue
            if node in seen: raise RuntimeError('Corps cache cycle')
            seen.add(node); pairs[hex(o.q(node+0x20))] = hex(o.q(node+0x28))
            todo.extend([o.q(node), o.q(node+0x10)])
        if len(pairs) != o.q(manager+0x98): raise RuntimeError('Corps cache count differs')
        return pairs
    def state():
        result = audit.read()
        result['lists'].update({hex(h): [[refs[int(p, 16)]] for p in audit.members(o.q(ctx.raw+h))]
                                for h in list_heads})
        result['corps_cache'] = cache()
        if officer_entry:
            result['officer_counties'] = {i: o.read(a, n).hex() if n else '' for i, (a, n) in county_vectors.items()}
        if free_manager: result['free_manager'] = o.read(o.q(ctx.g.exe+0x277E028), 0x140)
        return result
    def descriptors(pointer):
        wrapper = o.q(pointer + 0x38)
        if not wrapper: return []
        index = struct.unpack('<I', o.read(wrapper, 4))[0]
        node = o.q(o.q(ctx.g.exe + 0x2143270 + 0x10) + index * 8)
        result, seen = [], set()
        while node:
            if node in seen: raise RuntimeError('Destination descriptor cycle')
            seen.add(node); result.append(o.read(node, 24).hex()); node = o.q(node + 24)
        return result
    original, initial_store = state(), read_store(ctx, audit)
    snapshot.update(corps_cache=original['corps_cache'], nested=original['nested'])
    frame, hooks = [None], []
    def constructor(mu, address, size, user):
        if frame[0] is not None:
            p = mu.reg_read(UC_X86_REG_RCX)
            frame[0]['allocations'].append(dict(address=hex(p), data=o.read(p, 0x80).hex()))
    def cancellation(mu, address, size, user):
        if frame[0] is not None: frame[0]['cancelled'].append(hex(mu.reg_read(UC_X86_REG_RCX)))
    def merger(mu, address, size, user):
        if frame[0] is not None:
            frame[0]['mergers'].append([refs.get(mu.reg_read(r),hex(mu.reg_read(r))) for r in [UC_X86_REG_RCX,UC_X86_REG_RDX]])
    def force_removal(mu, address, size, user):
        if frame[0] is not None: frame[0]['force_removals'].append(hex(mu.reg_read(UC_X86_REG_RCX)))
    def reevaluation(mu, address, size, user):
        if frame[0] is not None:
            frame[0]['reevaluations'].append([refs[mu.reg_read(UC_X86_REG_RCX)], mu.reg_read(UC_X86_REG_R8)])
    for rva, fn in [(0x1D7DA0, constructor), (0x334780, cancellation)]:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, fn, begin=ctx.g.exe+rva, end=ctx.g.exe+rva))
    for rva,fn in [(0x293050,merger),(0x293790,force_removal)]:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE,fn,begin=ctx.g.exe+rva,end=ctx.g.exe+rva))
    if officer_entry:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE, reevaluation, begin=ctx.g.exe+0x2E8E70, end=ctx.g.exe+0x2E8E70))
    if entry == 0x2BDD50:
        sort_slot=[None]
        def sorting(mu,address,size,user):sort_slot[0]=mu.reg_read(UC_X86_REG_RDX)&0xFFFFFFFF
        def sorted_candidates(mu,address,size,user):
            if frame[0] is not None:
                node=o.q(audit.heads+sort_slot[0]*8); values=[]
                while node:
                    values.append(refs[o.q(node)]);node=o.q(node+8)
                frame[0]['inheritance_candidates']=values
        for rva,fn in [(0x24C590,sorting),(0x2BDFCE,sorted_candidates)]:
            hooks.append(o.mu.hook_add(UC_HOOK_CODE,fn,begin=ctx.g.exe+rva,end=ctx.g.exe+rva))
    def refresh_unit():
        if frame[0] is not None: frame[0]['unit_refreshes'].append(refs[o.mu.reg_read(UC_X86_REG_RDX)])
        return 0
    def visual():
        if frame[0] is not None:
            point=o.mu.reg_read(UC_X86_REG_R8)
            owner=next(i for i,b in enumerate(ctx.rec[UNITS]) if struct.unpack_from('<Q',b,0x148)[0]+0x10==point)
            frame[0]['morale_visuals'].append([owner,o.mu.reg_read(UC_X86_REG_RDX)])
        return 0
    def difficulty_log():
        if frame[0] is not None:
            signed=lambda value: struct.unpack('<i',struct.pack('<I',value&0xFFFFFFFF))[0]
            frame[0]['difficulty_logs'].append([signed(o.mu.reg_read(UC_X86_REG_RCX)),
                o.mu.reg_read(UC_X86_REG_RDX)-ctx.g.exe,
                signed(o.mu.reg_read(UC_X86_REG_R8)),signed(o.mu.reg_read(UC_X86_REG_R9))])
        return 0
    presentation=[*PRESENTATION,0x1B4990]
    if unit_host_entry:
        # Verified graphics host getter, effect creation and debug formatting do not change gameplay records.
        # The getter's native return is always this exact singleton address; effect/log requests are compared.
        o.stub(0xEEC0,ctx.g.exe+0x1B5CB40)
        o.stub(0x37D6A0,fn=visual)
        o.stub(0x366AB0,fn=difficulty_log)
        presentation.extend([0xEEC0,0x37D6A0,0x366AB0])
    for rva in [*PRESENTATION,0x1B4990]:
        o.stub(rva,fn=refresh_unit if unit_host_entry and rva==0x1A3C50 else None)
    cases = []
    try:
        for fixture in fixtures:
            started = time.time()
            if officer_entry:
                i = fixture['person'][1]
                if struct.unpack_from('<i', ctx.rec[OFFICERS][i], 0x1E8)[0] and str(i) not in county_vectors:
                    raise RuntimeError(f'The frozen input lacks the captured officer county array: {i}')
            def run():
                before, after, active = state(), [], dict(allocations=[], cancelled=[], mergers=[], force_removals=[])
                inheritance_before = []
                if entry == 0x2BDD50:active['inheritance_candidates']=[]
                if officer_entry: active['reevaluations'] = []
                if unit_host_entry: active.update(unit_refreshes=[],morale_visuals=[],difficulty_logs=[])
                frame[0] = active
                def inspect():
                    result = dict(after=audit.delta(before, state()), store=read_store(ctx, audit))
                    result['created'] = []
                    for allocation in active['allocations']:
                        pointer = int(allocation['address'], 16)
                        result['created'].append(dict(address=hex(pointer), data=o.read(pointer, 0x80).hex(),
                            descriptors=descriptors(pointer)))
                    after.append(result)
                    return []
                try:
                    if entry == 0x2700A0:
                        ptr=lambda r:ctx.addr(int(r[0],16),r[1]) if r else 0
                        result=o.call(entry,ptr(fixture['a']),ptr(fixture['b']),fixture['delta']&0xFFFFFFFF,
                            fixture['notify'],stack_args=[fixture['suppress']],read_after=inspect)
                    elif entry == 0x2BDD50:
                        from death_inheritance import native_driver
                        driver, command, input_undo = native_driver(ctx, fixture, commands)
                        def at_body(mu, address, size, user): inheritance_before.append(state())
                        body_hook=o.mu.hook_add(UC_HOOK_CODE,at_body,begin=ctx.g.exe+entry,end=ctx.g.exe+entry)
                        def inherited():
                            nonlocal before
                            if len(inheritance_before)!=1:raise RuntimeError('Missing inheritance input checkpoint')
                            before=inheritance_before[0]
                            return inspect()
                        try:
                            o.call(driver-ctx.g.exe,read_after=inherited)
                        finally:
                            o.mu.hook_del(body_hook)
                            for address,value in reversed(input_undo):o.poke(address,value)
                        result=0
                    elif entry == 0x258EE0:
                        ptr = lambda r: ctx.addr(int(r[0],16),r[1]) if r else 0
                        o.call(entry,ptr(fixture['unit']),ptr(fixture.get('attacker')),ptr(fixture.get('attacker_base')),
                               fixture['mode'],stack_args=[fixture['reason']],read_after=inspect)
                        result = 0  # The native destruction body is void; compare its state and host requests.
                    elif entry == 0x295AA0:
                        # Win64 args established at 0x259723..0x25974B; no presentation-suppression shortcut.
                        if fixture.get('pending_units'):
                            from officer_death_extended import pending_driver
                            driver=pending_driver(ctx,fixture)
                            def at_death(mu,address,size,user):inheritance_before.append(state())
                            body_hook=o.mu.hook_add(UC_HOOK_CODE,at_death,begin=ctx.g.exe+entry,end=ctx.g.exe+entry)
                            def after_death():
                                nonlocal before
                                if len(inheritance_before)!=1:raise RuntimeError('Missing death input checkpoint')
                                before=inheritance_before[0]
                                return inspect()
                            try:result=o.call(driver-ctx.g.exe,read_after=after_death)
                            finally:o.mu.hook_del(body_hook)
                        else:
                            target=fixture.get('attacker')
                            result=o.call(entry,ctx.addr(OFFICERS,fixture['person'][1]),ctx.addr(int(target[0],16),target[1]) if target else 0,1,fixture['destroy_own_unit'],
                                stack_args=[1,0,1,1,0],read_after=inspect)
                    elif entry == 0x2E0CF0:
                        ptr = lambda r: ctx.addr(int(r[0], 16), r[1]) if r else 0
                        result = o.call(entry, ptr(fixture['person']), ptr(fixture['unit']), 0, 0,
                                        stack_args=[ptr(fixture['force']), ptr(fixture['officer'])], read_after=inspect)
                    elif entry == 0x264DD0:
                        ptr = lambda r: ctx.addr(int(r[0], 16), r[1]) if r else 0
                        result = o.call(entry, ptr(fixture['person']), ptr(fixture.get('release_officer')),
                                        ptr(fixture.get('relocation')), read_after=inspect)
                    elif entry in [0x2E75C0,0x2EA6D0]:
                        result = o.call(entry,ctx.addr(OFFICERS,fixture['person'][1]),0,0,0,read_after=inspect)
                    elif free_manager:
                        manager = o.q(ctx.g.exe+0x277E028)
                        code = b'\x48\x83\xec\x28'  # Native Win64 shadow space and stack alignment.
                        code += b'\x48\xb9'+struct.pack('<Q',manager)+b'\xc7\x41\x08'+struct.pack('<I',fixture['count']&0xFFFFFFFF)
                        for ref in fixture['queue']:
                            code += b'\x48\xb9'+struct.pack('<Q',manager)+b'\x48\xba'+struct.pack('<Q',ctx.addr(int(ref[0],16),ref[1]))
                            code += b'\x48\xb8'+struct.pack('<Q',ctx.g.exe+0x32B5A0)+b'\xff\xd0'
                        code += b'\x48\xb9'+struct.pack('<Q',manager)+b'\x48\xb8'+struct.pack('<Q',ctx.g.exe+entry)+b'\xff\xd0'
                        code += b'\x48\x83\xc4\x28\xc3'
                        harness = o.alloc(len(code)); o.poke(harness,code)
                        result = o.call(harness-ctx.g.exe,read_after=inspect)
                    else:
                        pointer = ctx.addr(CORPS,fixture['source']) if fixture['source'] is not None else 0
                        second = ctx.addr(CORPS,fixture['destination']) if entry == 0x293050 else 0
                        result = o.call(entry,pointer,second,fixture['mode'],0,read_after=inspect)
                    return dict(fixture=fixture, result=result, **active, **after[0])
                finally: frame[0] = None
            result = ctx.run(fixture['pokes'], run)
            if result is None: raise RuntimeError(f'{fixture["name"]}: {ctx.failures[-1]}')
            if result['force_removals']: raise RuntimeError('A surviving-force fixture reached the separate force-removal body')
            cases.append(result)
            print(f'  corps commands {fixture["name"]}: {len(result["cancelled"])} cancellations, {len(result["allocations"])} allocations, '
                  f'{len(result["after"]["records"])} changed records ({time.time()-started:.2f}s)', flush=True)
    finally:
        o.unstub()
        for hook in hooks: o.mu.hook_del(hook)
    if state() != original or read_store(ctx, audit) != initial_store:
        raise RuntimeError('Corps command fixtures did not restore their frozen input')
    if not ctx.frozen: require_unchanged_snapshot(guard, initial_rng, extra_tables)
    ctx.save(stem+'_world.json.gz', snapshot)
    evidence = getattr(ctx.g, 'immutable_code_evidence', None)
    if evidence:
        evidence = {k: v for k, v in evidence.items() if k != 'added_text_rvas'} | {'added_text_pages': len(evidence['added_text_rvas'])}
    ctx.save(stem+'.json.gz', dict(world=stem+'_world.json.gz', cases=cases, gameplay_stubs=[],native_entry=hex(entry),
        immutable_code_evidence=evidence,
        presentation=[hex(r) for r in presentation], physical_fields=[[0, 8], [0x18, 16], [0x30, 16]]))
    return f'corps commands: {len(cases)} complete native bodies'

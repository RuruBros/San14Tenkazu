# Native ruler mutation, loyalty and force teardown dependencies on a single frozen snapshot.
import copy
import gzip
import hashlib
import json
import struct
from pathlib import Path
from context import FORCES, OFFICERS, CORPS, world
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from unit_group_cache import cache_tree
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX

LOYALTY = 0x85DB8
COVER = [0x28DB70, 0x2688C0, 0x229730, 0x58440, 0x277C40, 0xA21C0]


def publish_player_prefix(folder, output):
    # Reuse the observed boundary payload; never promote the unfinished function to a complete call.
    folder, output = Path(folder), Path(output)
    report = json.loads((folder/'report.json').read_text(encoding='utf-8'))
    trace = json.loads((folder/'mutation_root_trace.json').read_text(encoding='utf-8'))
    digest = hashlib.sha256((folder/'input.pkl').read_bytes()).hexdigest()
    if digest != report['input_sha256'] or digest != trace['input_sha256']:
        raise RuntimeError('Player prefix input hash differs')
    if report['complete'] or trace['calls'][-1][1] != '0x450d30':
        raise RuntimeError('Expected unfinished player presentation boundary not reached')
    observed = json.loads(gzip.decompress((folder/'before_player_corps_refresh.json.gz').read_bytes()))
    case = dict(fixture=dict(name='player_actual_same_corps_prefix', kind='change', dying=observed['dying'],
        successor=observed['successor'], pokes=[]), after=observed['delta'], officers=observed['officers'],
        refresh_corps=trace['calls'][-1][3])
    case['after'].setdefault('nested', {})[report['output_wrapper']] = observed['officers']
    snapshot = json.loads(gzip.decompress((folder/'player_ruler_change_world.json.gz').read_bytes()))
    output.mkdir(exist_ok=True)
    world.save(snapshot, str(output/'player_ruler_prefix_world.json.gz'))
    world.save(dict(world='player_ruler_prefix_world.json.gz', case=case, entry='0x28db70', endpoint='0x450d30',
        function_complete=False, gameplay_stubs=False, presentation_stubs=False, output_wrapper=report['output_wrapper'],
        input_sha256=digest, scope='Observed prefix before the first instruction of player corps presentation. '
        'The remaining UI/strategy/refresh suffix did not complete.'), str(output/'player_ruler_prefix.json.gz'))


def generate(ctx, retained=(), omit=()):
    if not ctx.frozen: raise RuntimeError('Ruler mutation requires a coherent frozen input')
    o = ctx.o
    capture_native_pools(ctx)
    audit = EngineAuditState(ctx)
    first, size, count = ctx.addr(LOYALTY, 0), 0x38, 7
    if any(ctx.addr(LOYALTY, i) != first+i*size for i in range(count)):
        raise RuntimeError('Loyalty table is not contiguous')
    records = [o.read(first+i*size, size).hex() for i in range(count)]
    audit.tables[LOYALTY] = (first, size, count)
    audit.snapshot['tables'][hex(LOYALTY)] = dict(size=size, address=hex(first), extra={}, records=records)
    ctx.rec[LOYALTY] = [bytes.fromhex(b) for b in records]
    flag = 'loyalty_join_limit'
    if flag in world.EXE_VALUES: raise RuntimeError('Temporary loyalty flag already registered')
    world.EXE_VALUES[flag] = (0x27D2638, 'int')
    audit.snapshot['flags'][flag] = int.from_bytes(o.read(ctx.g.exe+0x27D2638, 4), 'little', signed=True)
    if int.from_bytes(o.read(ctx.g.exe+0x1A49CAC, 4), 'little', signed=True) != 2:
        raise RuntimeError('Loyalty tenure divisor differs from the port')
    audit.wrappers.extend((f'force:{i}:{at:x}', ctx.addr(FORCES, i)+at) for i in range(52) for at in (0x250, 0x260))
    manager = o.q(ctx.g.exe+0x1B9A4A0)
    refs = {ctx.addr(t, i): [hex(t), i] for t, (_, _, count) in audit.tables.items() for i in range(count)}
    def list_items(head): return [[refs[int(p, 16)]] for p in audit.members(o.q(ctx.raw+head))]
    def caches():
        return {name: {hex(k): hex(v) for _, k, v in cache_tree(o.read, o.q, manager, at)[0]}
                for name, at in (('corps_cache', 0x90), ('force_cache', 0x80))}
    def state():
        s = audit.read(); s.update(caches())
        s['lists'].update({hex(head): list_items(head) for head in (0xA0, 0xD0, 0x120)})
        return s
    original = state()
    snapshot = copy.deepcopy(audit.snapshot)
    snapshot.update(nested=original['nested'], **caches())
    for key in snapshot['tables']: snapshot['tables'][key]['address'] = hex(ctx.addr(int(key, 16), 0))
    for head in (0xA0, 0xD0, 0x120):
        snapshot['lists'][hex(head)] = {'items': list_items(head)}
    wrapper = o.q(ctx.addr(FORCES, 3)+0x260)
    output = o.alloc(16); o.poke(output, struct.pack('<QQ', ctx.g.exe+0x13436B0, wrapper))
    fixtures = []
    def poke(t, i, at, value, fmt='B'): return [hex(t), i, at, struct.pack('<'+fmt, value).hex()]
    def add(name, kind, pokes=(), **kwargs): fixtures.append(dict(name=name, kind=kind, pokes=list(pokes), **kwargs))
    # The completed original call is reused; its fixture must remain byte-for-byte identical.
    fixtures.append(dict(name='original_same_corps', dying=551, successor=176, pokes=[], kind='change'))
    for successor in (247, 289, 292, 330, 390, 538, 543, 567, 572, 703):
        add(f'same_corps_{successor}', 'change', dying=551, successor=successor)
    for name, extra in (
        ('advisor', [poke(FORCES, 3, 0x52, 176, 'H')]),
        ('forced_loyalty', [poke(OFFICERS, 176, 0x15E, ctx.u16(OFFICERS, 176, 0x15E)|0x2000, 'H')]),
        ('tenure', [poke(OFFICERS, 247, 0x17C, 40, 'h'), poke(OFFICERS, 247, 0x16C, 0, 'h')]),
        ('bounds_upper', [poke(LOYALTY, 0, 0x2C, 300, 'h'), poke(LOYALTY, 1, 0x2C, -100, 'h'),
                          poke(LOYALTY, 2, 0x2C, 200, 'h'), poke(LOYALTY, 6, 0x2C, 20, 'h'),
                          ['settings', 0, 0xE58, '00000000'], ['settings', 0, 0x4C4, '00000000']]),
        ('bounds_inverted', [poke(LOYALTY, 0, 0x2C, -20, 'h'), poke(LOYALTY, 1, 0x2C, 30, 'h'),
                             poke(LOYALTY, 2, 0x2C, 40, 'h'), poke(LOYALTY, 6, 0x2C, 10, 'h'),
                             ['settings', 0, 0xE58, '00000000'], ['settings', 0, 0x4C4, '00000000']]),
        ('second_lower', [['settings', 0, 0xE58, '00000000'], ['settings', 0, 0x4C4, '01000000']]),
        ('player_same_corps', [['worldstate', 0, 0x3A, '03']]),
        ('inactive_successor', [poke(OFFICERS, 176, 0xE6, 0)]),
        ('inactive_dying', [poke(OFFICERS, 551, 0xE6, 0)]),
    ): add(name, 'change', extra, dying=551, successor=176)
    # Move the successor to an existing NPC corps; the native merger remains fully active.
    other_corps = next(i for i, b in enumerate(ctx.rec[CORPS]) if i and i != 1 and b[0x10] not in (0, 45) and b[0x11])
    add('cross_corps', 'change', [poke(OFFICERS,176,0xE0,other_corps)], dying=551, successor=176)
    add('null_successor', 'change', dying=551, successor=None)
    add('null_dying', 'change', dying=None, successor=176)
    person, ruler, force = 176, 551, 3
    basic = [poke(OFFICERS, person, 0xE6, 4), poke(OFFICERS, person, 0xBC, 0, 'H'),
             poke(OFFICERS, person, 0xBA, 0, 'H'), [hex(OFFICERS), person, 0xC0, bytes(32).hex()],
             poke(OFFICERS, person, 0x15E, 0, 'H'), poke(FORCES, force, 0x10, ruler, 'H')]
    for status in (0, 1, 2, 4, 5, 6, 9, 255):
        add(f'loyalty_status_{status}', 'loyalty', basic+[poke(OFFICERS,person,0xE6,status)], person=person, force=force, tenure=True)
    for name, extra in (
        ('sworn', [poke(OFFICERS,person,0xBC,123,'H'),poke(OFFICERS,ruler,0xBC,123,'H')]),
        ('spouse', [poke(OFFICERS,person,0xBA,ruler,'H')]),
        ('like', [poke(OFFICERS,person,0xCE,ruler,'H')]),
        ('marked', [poke(OFFICERS,person,0x15E,0x4000,'H')]),
        ('dislike', [poke(OFFICERS,person,0xDE,ruler,'H')]),
        ('same_zero_clan', [poke(OFFICERS,person,0xB2,0,'H'),poke(OFFICERS,ruler,0xB2,0,'H')]),
        ('different_clan', [poke(OFFICERS,person,0xB2,1,'H'),poke(OFFICERS,ruler,0xB2,2,'H')]),
        ('same_nonzero_house', [poke(OFFICERS,person,0x14C,1),poke(OFFICERS,ruler,0x14C,1)]),
        ('join_equal', [poke(OFFICERS,person,0x17A,100,'h'),poke(OFFICERS,ruler,0x17A,100,'h')]),
        ('join_above', [poke(OFFICERS,person,0x17A,101,'h'),poke(OFFICERS,ruler,0x17A,100,'h')]),
        ('join_limit', [poke(OFFICERS,person,0x17A,105,'h'),poke(OFFICERS,ruler,0x17A,100,'h'),['exe',flag,0,'05000000']]),
    ): add('loyalty_'+name,'loyalty',basic+extra,person=person,force=force,tenure=True)
    for distance in (0,49,50,51,75,100,149,150,151,255):
        add(f'loyalty_distance_{distance}','loyalty',basic+[poke(OFFICERS,person,0xBE,distance),poke(OFFICERS,ruler,0xBE,0)],person=person,force=force,tenure=False)
    for amount in (-32768,-3,-2,-1,0,1,2,3,100,32767):
        for enabled in (False,True):
            add(f'loyalty_tenure_{amount}_{enabled}','loyalty',basic+[poke(OFFICERS,person,0x17C,amount,'h'),poke(OFFICERS,person,0x16C,-32768,'h')],person=person,force=force,tenure=enabled)
    for destination in (0,3,45,51,52,255,256,0xffffffff):
        for allow in (False,True):
            add(f'slot_{destination}_{allow}','slot',force=3,destination=destination,allow_empty=allow)
    for slot, oid in ((0,0),(0,551),(1,176),(2,6000),(2,6001),(2,65535),(3,551),(0xffffffff,551)):
        add(f'officer_slot_{slot}_{oid}','diplomatic_officer',[poke(FORCES,3,0x196+min(slot,2)*2,oid,'H')],force=3,slot=slot)
    slots = [poke(FORCES,3,0x196+i*2,551 if i==0 else 176,'H') for i in range(3)]+[[hex(FORCES),3,0x19C,'2d2d2d']]
    for name, extra, a, b in (
        ('first_duplicate', [],3,45),('first_invalid', [poke(FORCES,3,0x196,0,'H')],3,45),
        ('inactive_officer',[poke(OFFICERS,551,0xE6,0)],3,45),
        ('second_slot',[poke(FORCES,3,0x19C,9)],3,45),('third_slot',[[hex(FORCES),3,0x19C,'0908']],3,45),
        ('none',[[hex(FORCES),3,0x19C,'090807']],3,45),('same',[],3,3),('zero_force',[],0,45),
        ('zero_destination',[],3,0),('null_force',[],None,45),('null_destination',[],3,None),
        ('quiet_player',[['worldstate',0,0x3A,'03']],3,45),
    ): add('clear_'+name,'clear',slots+extra,force=a,destination=b)
    cache_keys = [int(k,16) for k in original['force_cache']]
    for n,key in enumerate(cache_keys+[0,ctx.addr(FORCES,0),2**64-1]): add(f'force_cache_{n}','force_cache',key=hex(key))
    fixtures = [f for f in fixtures if f['name'] not in omit]
    prior = {c['fixture']['name']:c for c in retained}
    cases, hooks, events = [], [], []
    def host(mu,address,size,data):
        rva=address-ctx.g.exe
        arg=mu.reg_read(UC_X86_REG_RCX)
        events.append([hex(rva),hex(arg)])
    try:
        for rva in (0x450D30,0x88D380,0x1B4E60): hooks.append(o.mu.hook_add(UC_HOOK_CODE,host,begin=ctx.g.exe+rva,end=ctx.g.exe+rva))
        for f in fixtures:
            if f['name'] in prior:
                saved=prior[f['name']]
                if saved['fixture']!=f: raise RuntimeError('Retained ruler fixture differs')
                # The original probe predates host tracing and the force-cache audit. Never invent those fields.
                cases.append(saved); continue
            events.clear()
            def run():
                before, after = state(), []
                def inspect(): after.append(state()); return []
                ptr=lambda t,key: ctx.addr(t,f[key]) if f[key] is not None else 0
                kind=f['kind']
                if kind=='change': result=o.call(0x28DB70,ptr(OFFICERS,'dying'),ptr(OFFICERS,'successor'),output,0,read_after=inspect)
                elif kind=='loyalty': result=o.call32(0x2688C0,ptr(OFFICERS,'person'),ptr(FORCES,'force'),int(f['tenure']),read_after=inspect)
                elif kind=='slot': result=o.call32(0x229730,ptr(FORCES,'force'),f['destination'],int(f['allow_empty']),read_after=inspect)
                elif kind=='diplomatic_officer': result=o.call(0x58440,ptr(FORCES,'force'),f['slot'],read_after=inspect)
                elif kind=='clear': result=o.call(0x277C40,ptr(FORCES,'force'),ptr(FORCES,'destination'),0,0,read_after=inspect)
                elif kind=='force_cache': result=o.call(0xA21C0,manager,int(f['key'],16),read_after=inspect)
                else: raise RuntimeError('Unknown fixture')
                delta=audit.delta(before,after[0])
                if kind!='change' and any(k not in ('records','force_cache') for k in delta): raise RuntimeError('Unexpected engine mutation')
                if kind in ('loyalty','slot','diplomatic_officer') and delta!={'records':[]}: raise RuntimeError('Read helper mutated state')
                if kind=='clear' and any(int(r[0],16)!=FORCES for r in delta['records']): raise RuntimeError('Appointment changed a different table')
                if kind=='force_cache' and delta['records']: raise RuntimeError('Cache erasure changed records')
                return dict(fixture=f,result=result if kind not in ('change','force_cache') else None,
                    after=delta,officers=after[0]['nested']['force:3:260'] if kind=='change' else [],host_events=list(events))
            result=ctx.run(f['pokes'],run)
            if result is None: raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
            cases.append(result);ctx.save('ruler_change_progress.json.gz',dict(cases=cases))
            print('  ruler',f['name'],'records',len(result['after']['records']),'sections',list(result['after']),flush=True)
        if state()!=original: raise RuntimeError('Ruler collection did not roll back')
        ctx.save('ruler_change_world.json.gz',snapshot)
        ctx.save('ruler_change.json.gz',dict(world='ruler_change_world.json.gz',cases=cases,gameplay_stubs=False,
            presentation_stubs=False,policy_reset=False,output_wrapper='force:3:260',omitted=list(omit)))
    finally:
        for h in hooks: o.mu.hook_del(h)
        o.unstub(); world.EXE_VALUES.pop(flag)
    return f'{len(cases)} complete native ruler mutation/loyalty/diplomatic/cache cases'

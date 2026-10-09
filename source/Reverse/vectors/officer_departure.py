# Pure native departure helpers in one coherent frozen scene; no live process execution.
import copy,struct
from officer_death_player import audit_state


def requests(ctx):
    person,ruler=384,557;result=[]
    def poke(p,at,data):return ['0x73a50',p,at,bytes(data).hex()]
    def word(p,at,n):return poke(p,at,struct.pack('<H',n&65535))
    base=[poke(person,0xE6,b'\x04'),poke(person,0xE8,b'\x3b'),word(person,0x166,190),word(person,0x16C,0),word(person,0xE4,13),
        word(person,0xBA,0),word(person,0xBC,0),word(person,0xB4,0),word(person,0xB6,0),poke(person,0xC0,bytes(16)),
        word(person,0x15E,ctx.u16(0x73A50,person,0x15E)&~0x4000),word(ruler,0xB4,0),word(ruler,0xB6,0)]
    def add(name,kind,extra=()):result.append(dict(name=name,kind=kind,person=person,rva=0x2665F0 if kind=='basis' else 0x275F40,pokes=base+list(extra)))
    add('predicate_active_low_loyalty','predicate')
    for tick in range(12):
        add('predicate_seed_tick_'+str(tick),'predicate',[poke(person,0xE8,b'\x00'),word(person,0x16C,-100),['worldstate',0,0x450,struct.pack('<i',tick).hex()]])
    for status in (1,2,3,5,6,9):add('predicate_status_'+str(status),'predicate',[poke(person,0xE6,bytes([status]))])
    for loyalty in (59,60,84,85,86,255):add('predicate_loyalty_'+str(loyalty),'predicate',[poke(person,0xE8,bytes([loyalty]))])
    for year,month,loyalty in ((193,6,60),(193,7,60),(192,1,60),(195,1,59),(195,1,60),(65535,255,60)):
        add(f'predicate_join_{year}_month_{month}_loyalty_{loyalty}','predicate',[word(person,0x166,year),poke(person,0xE8,bytes([loyalty])),['worldstate',0,0x36,bytes([month]).hex()]])
    add('predicate_marked','predicate',[word(person,0x15E,ctx.u16(0x73A50,person,0x15E)|0x4000)])
    add('predicate_spouse','predicate',[word(person,0xBA,ctx.u16(0x73A50,ruler,0x10))])
    add('predicate_sworn','predicate',[word(person,0xBC,10),word(ruler,0xBC,10)])
    for reverse in (False,True):
        for same in (False,True):
            add(f'predicate_parent_{reverse}_clan_{same}','predicate',[
                word(ruler if reverse else person,0xB4,ctx.u16(0x73A50,person if reverse else ruler,0x10)),
                word(person,0xB2,111),word(ruler,0xB2,111 if same else 222)])
    for place in (0,52,61,62,562):add('predicate_place_'+str(place),'predicate',[word(person,0xE4,place)])
    for loyalty,merit,like in ((0,0,False),(50,0,False),(84,0,False),(85,0,False),(86,0,False),(255,0,False),
        (59,-100,False),(59,10,False),(59,100,False),(59,0,True)):
        add(f'basis_loyalty_{loyalty}_merit_{merit}_like_{like}','basis',[poke(person,0xE8,bytes([loyalty])),word(person,0x16C,merit)]+
            ([word(person,0xC0,ctx.u16(0x73A50,ruler,0x10))] if like else []))
    return result


def publish(ctx,retained):
    if not ctx.frozen:raise RuntimeError('Frozen departure input required')
    snapshot=copy.deepcopy(retained['snapshot']);cases=retained['cases']
    if len(snapshot['tables'])!=32:raise RuntimeError('Complete 32-table input required')
    expected=requests(ctx)
    if len(cases)!=len(expected) or any(c['fixture']!=f or not c['function_complete'] or not c['state_unchanged'] for c,f in zip(cases,expected)):
        raise RuntimeError('Complete retained helper cases differ')
    predicates=[c['result'] for c in cases if c['fixture']['kind']=='predicate']
    if set(predicates)!={0,1}:raise RuntimeError('Both seeded predicate results required')
    for name,rva in (('departure_loyalty_limit',0x1A4968C),('departure_compatibility_divisor',0x1A49698),('departure_chance_cap',0x1A496A4)):
        if snapshot['flags'][name]!=struct.unpack('<i',ctx.o.read(ctx.g.exe+rva,4))[0]:raise RuntimeError('Actual rule input differs')
    ctx.save('officer_departure_world.json.gz',snapshot)
    ctx.save('officer_departure.json.gz',dict(world='officer_departure_world.json.gz',cases=cases,
        input_sha256=retained['input_sha256'],complete=True,gameplay_stubs=False,allocator_stubs=False,presentation_stubs=False,
        scope='Whole 0x275F40 predicate and 0x2665F0 chance only, 32-table/engine state unchanged; 0x264DD0 removal and rebellion are separate.'))
    return len(cases)


def generate(ctx,snapshot,digest,retained=()):
    if not ctx.frozen:raise RuntimeError('Live native generation forbidden')
    o=ctx.o;audit=audit_state(ctx,snapshot);baseline=audit.read();cases=[]
    prior={c['fixture']['name']:c for c in retained}
    snapshot=copy.deepcopy(snapshot)
    for name,rva in (('departure_loyalty_limit',0x1A4968C),('departure_compatibility_divisor',0x1A49698),('departure_chance_cap',0x1A496A4)):
        snapshot['flags'][name]=struct.unpack('<i',o.read(ctx.g.exe+rva,4))[0]
    try:
        for f in requests(ctx):
            if f['name'] in prior:
                if prior[f['name']]['fixture']!=f:raise RuntimeError('Retained fixture differs')
                cases.append(prior[f['name']]);continue
            def execute():
                before=audit.read();output=[]
                def after():output.append(audit.delta(before,audit.read()));return []
                result=o.call(f['rva'],ctx.addr(0x73A50,f['person']),read_after=after)
                if output[0]!={'records':[]}:raise RuntimeError('Pure helper changed audited state')
                return dict(fixture=f,function_complete=True,result=result,state_unchanged=True)
            c=ctx.run(f['pokes'],execute)
            if c is None:raise RuntimeError(ctx.failures[-1])
            cases.append(c);ctx.save('officer_departure_progress.json.gz',dict(input_sha256=digest,snapshot=snapshot,cases=cases))
        if audit.read()!=baseline:raise RuntimeError('Departure did not roll back')
        ctx.save('officer_departure_progress.json.gz',dict(input_sha256=digest,snapshot=snapshot,cases=cases))
        return publish(ctx,dict(input_sha256=digest,snapshot=snapshot,cases=cases))
    finally:o.unstub()

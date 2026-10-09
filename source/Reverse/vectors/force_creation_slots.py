# Exact read-only force-slot selection, independent of force creation and its presentation.
import copy,struct
from officer_death_player import audit_state

def requests(ctx):
    person=997;result=[]
    def word(table,index,at,value):return [hex(table),index,at,struct.pack('<H',value&65535).hex()]
    def add(name,requested,pokes=()):result.append(dict(name=name,person=person,requested=requested,pokes=list(pokes)))
    for requested in (-1,0,1,8,37,38,45,46,50,51,52,255):add('original_request_'+str(requested),requested)
    occupied=[word(0xDF30,i,0x10,557) for i in range(1,52)]
    occupied+=[word(0xDF30,i,0x11E,321) for i in range(52)]+[word(0x73A50,person,0xB2,222)]
    def vacant(*indices):return [word(0xDF30,i,0x10,0) for i in indices]
    for requested in (0,3,46,51,52):
        add('occupied_request_'+str(requested),requested,occupied)
    add('fallback_highest_reserved',0,occupied+vacant(1,37,38,41,45))
    add('no_reserved_or_matching_slot',0,occupied+vacant(1,9,37,46,50,51))
    add('explicit_nonreserved_slot',3,occupied+vacant(3,45))
    add('occupied_explicit_falls_back',3,occupied+vacant(38,45))
    add('former_ruler_precedes_clan',0,occupied+vacant(9,11,45)+[word(0xDF30,9,0x11E,ctx.u16(0x73A50,person,0x10)),word(0xDF30,11,0x11E,222)])
    add('first_former_ruler_match',0,occupied+vacant(9,10,45)+[word(0xDF30,i,0x11E,ctx.u16(0x73A50,person,0x10)) for i in (9,10)])
    add('clan_precedes_reserved',0,occupied+vacant(11,45)+[word(0xDF30,11,0x11E,222)])
    add('first_clan_match',0,occupied+vacant(1,11,45)+[word(0xDF30,i,0x11E,222) for i in (1,11)])
    add('tribal_match_excluded',0,occupied+vacant(45,46,50,51)+[word(0xDF30,i,0x11E,ctx.u16(0x73A50,person,0x10)) for i in (46,50,51)])
    add('invalid_person_status_still_selects',0,occupied+vacant(45)+[['0x73a50',person,0xE6,'00']])
    return result

def publish(ctx,retained):
    if not ctx.frozen or len(retained['snapshot']['tables'])!=32:raise RuntimeError('Coherent full 32-table input required')
    cases=retained['cases'];expected=requests(ctx)
    if len(cases)!=len(expected) or any(c['fixture']!=f or not c['function_complete'] or not c['state_unchanged'] for c,f in zip(cases,expected)):
        raise RuntimeError('Complete retained slot cases differ')
    ctx.save('force_creation_slots_world.json.gz',retained['snapshot'])
    ctx.save('force_creation_slots.json.gz',dict(world='force_creation_slots_world.json.gz',input_sha256=retained['input_sha256'],cases=cases,
        complete=True,gameplay_stubs=False,allocator_stubs=False,presentation_stubs=False,
        scope='Whole read-only 0x2A9520 slot selection only; force creation 0x2D7C80 and rebellion 0x2E5D40 remain separate.'))
    return len(cases)

def generate(ctx,snapshot,digest,retained=()):
    if not ctx.frozen:raise RuntimeError('Live native generation forbidden')
    o=ctx.o;audit=audit_state(ctx,snapshot);baseline=audit.read();cases=[];prior={c['fixture']['name']:c for c in retained}
    try:
        for f in requests(ctx):
            if f['name'] in prior:
                if prior[f['name']]['fixture']!=f:raise RuntimeError('Retained fixture differs')
                cases.append(prior[f['name']]);continue
            def execute():
                before=audit.read();captured=[]
                def after():captured.append(audit.delta(before,audit.read()));return []
                returned=o.call(0x2A9520,ctx.addr(0x73A50,f['person']),f['requested'],read_after=after)
                if captured[0]!={'records':[]}:raise RuntimeError('Read-only slot selection changed audited state')
                refs={ctx.addr(0xDF30,i):i for i in range(52)}
                if returned and returned not in refs:raise RuntimeError('Returned non-slot pointer')
                return dict(fixture=f,function_complete=True,result=None if not returned else refs[returned],native_pointer=hex(returned),state_unchanged=True)
            c=ctx.run(f['pokes'],execute)
            if c is None:raise RuntimeError(ctx.failures[-1])
            cases.append(c);ctx.save('force_creation_slots_progress.json.gz',dict(input_sha256=digest,snapshot=snapshot,cases=cases))
            print(f['name'],c['result'],flush=True)
        if audit.read()!=baseline:raise RuntimeError('Slot selection did not roll back')
        ctx.save('force_creation_slots_progress.json.gz',dict(input_sha256=digest,snapshot=snapshot,cases=cases))
        return publish(ctx,dict(input_sha256=digest,snapshot=snapshot,cases=cases))
    finally:o.unstub()

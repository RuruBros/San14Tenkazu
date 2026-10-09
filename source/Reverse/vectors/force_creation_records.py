# Force initialization and resource helpers execute only in one coherent frozen Unicorn input.
import copy,random,struct
from officer_death_player import audit_state
from unit_lifecycle import GeneralListOracle
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX,UC_X86_REG_RDX

LISTS=(0x250,0x260,0x218,0x228,0x238)

def requests(ctx):
    result=[];rng=random.Random(0x226EC0)
    def add(name,kind,**fields):result.append(dict(name=name,kind=kind,**fields))
    for color in (0,1,-1):
        add('original_record_color_'+str(color),'initialize',target=44,color=color,enabled=True,pokes=[],counts=[0]*5,present=[True]*5)
    for i in range(12):
        target=(0,3,44,46,51)[i%5];data=bytearray(ctx.rec[0xDF30][target])
        data[0x10:]=rng.randbytes(len(data)-0x10)
        present=[True]*5
        if i<5:present[i]=False
        elif i==5:present=[False]*5
        for at,exists in zip(LISTS,present):
            struct.pack_into('<Q',data,at,struct.unpack_from('<Q',ctx.rec[0xDF30][target],at)[0] if exists else 0)
        add('record_padding_and_lists_'+str(i),'initialize',target=target,color=i%2,enabled=i!=6,
            pokes=[['0xdf30',target,0,data.hex()]],counts=[1,2,0,2,1],present=present)
    for rank in (0,1,2,3,4,5,6,7,8,255):
        add('resource_rank_'+str(rank),'points',person=997,pokes=[['0x73a50',997,0x1BC,bytes([rank]).hex()]])
    for name,pokes in (
        ('ruler',[['0x73a50',997,0xE6,'01']]),
        ('marked',[['0x73a50',997,0x15E,'0040']]),
        ('special_based',[['0x73a50',997,0x10,'0f10']]),
        ('special_no_home',[['0x73a50',997,0x10,'0f10'],['0x73a50',997,0xE2,'0000']]),
        ('special_tribe',[['0x73a50',997,0x10,'0f10'],['0xe0d0',3,0x10,'2e']])):
        add('points_'+name,'points',person=997,pokes=[['0x73a50',997,0x1BC,'06']]+pokes)
    for target in (0,3,44,46,51):add('resources_actual_'+str(target),'resources',target=target,pokes=[])
    members=[i for i,b in enumerate(ctx.rec[0x73A50]) if i and b[0xE0]==3 and b[0xE6] in (1,2,3,4)]
    reset=[['0x73a50',i,0x14C,'00'] for i in members]
    base=reset+[['0x73a50',997,0x1BC,'06']]
    for clan in (0,1,2,3,4,5,6,7,255):
        add('resources_clan_'+str(clan),'resources',target=3,pokes=base+[['0x73a50',997,0x14C,bytes([clan]).hex()]])
    for status in (0,1,2,3,4,5,6,7,8,255):
        add('resources_status_'+str(status),'resources',target=3,pokes=base+[['0x73a50',997,0x14C,'01'],['0x73a50',997,0xE6,bytes([status]).hex()]])
    for name,pokes in (
        ('marked',[['0x73a50',997,0x15E,'0040']]),
        ('special',[['0x73a50',997,0x10,'0f10']]),
        ('special_no_home',[['0x73a50',997,0x10,'0f10'],['0x73a50',997,0xE2,'0000']]),
        ('invalid_corps',[['0xe0d0',3,0x11,'00']]),
        ('corp_force_zero',[['0xe0d0',3,0x10,'00']]),
        ('corp_force_outside',[['0xe0d0',3,0x10,'ff']])):
        add('resources_'+name,'resources',target=3,pokes=base+[['0x73a50',997,0x14C,'01']]+pokes)
    add('resources_cap_99','resources',target=3,pokes=base+[['0x85db8',i,0x2E,'ff'] for i in range(7)]+[['0x73a50',997,0x14C,'06']])
    add('resources_ordered_two_members','resources',target=3,pokes=reset+[
        ['0x73a50',i,at,value] for i in (292,997) for at,value in ((0x14C,'03'),(0x1BC,'06'))])
    add('points_rule_zero_for_rank_255','points',person=997,pokes=[['0x73a50',997,0x1BC,'ff']]+[['0x85db8',i,0x2E,bytes([i+1]).hex()] for i in range(7)])
    return result

def publish(ctx,snapshot,digest,cases):
    fixtures=requests(ctx)
    if not ctx.frozen or len(snapshot['tables'])!=32 or len(cases)!=len(fixtures):raise RuntimeError('Complete coherent helper proof required')
    if any(c['fixture']!=f or not c['function_complete'] for c,f in zip(cases,fixtures)):raise RuntimeError('Retained fixture differs')
    ctx.save('force_creation_records_world.json.gz',snapshot)
    ctx.save('force_creation_records.json.gz',dict(world='force_creation_records_world.json.gz',input_sha256=digest,cases=cases,
        complete=True,gameplay_stubs=False,allocator_stubs=False,presentation_stubs=False,native_pool=True,
        scope='Whole 0x226EC0 initialization, 0x232070 resources and 0x22C2C0 points; creation caller and UI/GPU are separate.'))
    return len(cases)

def generate(ctx,snapshot,digest,retained=()):
    if not ctx.frozen:raise RuntimeError('Live native generation forbidden')
    o=ctx.o;audit=audit_state(ctx,snapshot);pool=ctx.g.exe+0x27D10E0
    audit.wrappers.extend((f'creation:{i}:{at:x}',ctx.addr(0xDF30,i)+at) for i in (0,3,44,46,51) for at in LISTS[2:])
    baseline=audit.read();cases=[];prior={c['fixture']['name']:c for c in retained};calls=[];expected={};coverage={}
    def observe(mu,address,size,user):
        rva=address-ctx.g.exe;coverage[rva]=coverage.get(rva,0)+1
        if rva==0x18770:
            if mu.reg_read(UC_X86_REG_RCX)!=pool:raise RuntimeError('Unexpected pool')
            index=mu.reg_read(UC_X86_REG_RDX)&0xffffffff
            if index not in expected:raise RuntimeError('Unexpected list clear')
            calls.append(expected[index])
    hooks=[o.mu.hook_add(UC_HOOK_CODE,observe,begin=ctx.g.exe+r,end=ctx.g.exe+r) for r in (0x226EC0,0x232070,0x22C2C0,0x3FD20,0x18770)]
    try:
        for f in requests(ctx):
            if f['name'] in prior:
                if prior[f['name']]['fixture']!=f:raise RuntimeError('Retained fixture changed')
                cases.append(prior[f['name']]);continue
            lists=[];old_enabled=o.read(pool+8,8);calls.clear();expected.clear()
            try:
                if f['kind']=='initialize':
                    for at,count in zip(LISTS,f['counts']):
                        wrapper=o.q(ctx.addr(0xDF30,f['target'])+at)
                        if not wrapper:raise RuntimeError('Original wrapper absent')
                        li=GeneralListOracle(ctx,wrapper,capacity=2);li.prepare([ctx.addr(0xDF30,i) for i in (3,44)][:count])
                        lists.append(li);expected[li.index]=at
                    if not f['enabled']:o.poke(pool+8,bytes(8))
                def execute():
                    before=audit.read();captured=[];total=o.q(pool+0x30);free=o.q(pool+0x20)
                    def after():
                        captured.append(audit.delta(before,audit.read()))
                        returned=[]
                        for at,li,count,present in zip(LISTS,lists,f.get('counts',[]),f.get('present',[])):
                            removed=f['enabled'] and present
                            wanted=[0,0,0] if removed else [li.nodes if count else 0,li.nodes+(count-1)*24 if count else 0,count]
                            if [o.q(s) for s in li.slots]!=wanted:raise RuntimeError('List head/tail/count differs')
                            if removed:returned=[li.nodes+i*24 for i in range(count)]+returned
                        if lists:
                            if o.q(pool+0x30)!=total-len(returned) or o.q(pool+0x20)!=(returned[0] if returned else free):raise RuntimeError('Pool accounting differs')
                            for i,node in enumerate(returned):
                                if o.q(node+8)!=(returned[i+1] if i+1<len(returned) else free):raise RuntimeError('Returned chain differs')
                        return []
                    if f['kind']=='initialize':returned=o.call(0x226EC0,ctx.addr(0xDF30,f['target']),f['color'],read_after=after)
                    elif f['kind']=='resources':returned=o.call(0x232070,ctx.addr(0xDF30,f['target']),read_after=after)
                    else:returned=o.call(0x22C2C0,ctx.addr(0x73A50,f['person']),read_after=after)&0xffffffff
                    delta=captured[0]
                    if delta.keys()-{'records','nested'} or any(int(t,16)!=0xDF30 for t,_,_ in delta['records']):raise RuntimeError('Unexpected non-force state change')
                    if f['kind']=='points' and delta!={'records':[]}:raise RuntimeError('Points changed state')
                    wanted=[at for at,present in zip(LISTS,f.get('present',[])) if present and f['enabled']] if lists else []
                    if calls!=wanted:raise RuntimeError('List clear order differs')
                    return dict(fixture=f,function_complete=True,result=returned if f['kind']=='points' else None,after=delta,clear_calls=list(calls),nested_before=before['nested'])
                c=ctx.run(f['pokes'],execute)
                if c is None:raise RuntimeError(ctx.failures[-1])
                cases.append(c);ctx.save('force_creation_records_progress.json.gz',dict(input_sha256=digest,snapshot=snapshot,cases=cases))
                print(f['name'],'complete','result',c['result'],'records',len(c['after']['records']),'clears',c['clear_calls'],flush=True)
            finally:
                o.poke(pool+8,old_enabled)
                for li in reversed(lists):li.restore()
        if audit.read()!=baseline:raise RuntimeError('Helper state did not roll back')
        ctx.save('force_creation_records_coverage.json.gz',{hex(r):n for r,n in coverage.items()})
        return publish(ctx,snapshot,digest,cases)
    finally:
        for h in hooks:o.mu.hook_del(h)
        o.unstub()

# Read-only rebellion candidate selection and comparator, with complete native calls that do not execute rebellion.
import copy,struct
from officer_death_player import audit_state
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8,UC_X86_REG_R9,UC_X86_REG_R15,UC_X86_REG_RSP

RULES=dict(rebellion_loyalty_gate=0x1A497DC,rebellion_service_divisor=0x1A497E8,rebellion_merit_gate=0x1A497F4,
    rebellion_compatibility_gate=0x1A49800,rebellion_chance_base=0x1A4980C,rebellion_dying_bonus=0x1A49818,
    rebellion_merit_divisor=0x1A49824,rebellion_chance_service_divisor=0x1A49830,rebellion_imperial_penalty=0x1A4984C)

def requests(ctx,tick):
    person,ruler,other=997,557,384;result=[]
    def word(table,index,at,n):return [hex(table),index,at,struct.pack('<H',n&65535).hex()]
    def pword(index,at,n):return word(0x73A50,index,at,n)
    def byte(index,at,n):return ['0x73a50',index,at,bytes([n]).hex()]
    def add(name,kind,pokes=(),**kwargs):result.append(dict(name=name,kind=kind,person=person,other=other,ruler=ruler,force=3,dying=ruler,pokes=list(pokes),**kwargs))
    for a,b in ((False,False),(True,False),(False,True),(True,True)):
        add(f'compare_dislikes_{a}_{b}','compare',[
            ['0x73a50',i,0xD0,bytes(16).hex()] for i in (person,other)]+[pword(person,0xD0,ruler if a else 0),pword(other,0xD0,ruler if b else 0)])
    compare=[['0x73a50',i,0xD0,bytes(16).hex()] for i in (person,other)]
    for a,b in ((0,255),(255,0),(84,85),(85,84)):
        add(f'compare_loyalty_{a}_{b}','compare',compare+[byte(person,0xE8,a),byte(other,0xE8,b)])
    for a,b in ((0,0),(0,1),(1,0),(1,2),(2,1),(254,255),(255,254)):
        add(f'compare_office_{a}_{b}','compare',compare+[byte(i,0xE8,80) for i in (person,other)]+[byte(person,0xE7,a),byte(other,0xE7,b)])
    for a,b in ((1,6000),(6000,1)):
        add(f'compare_id_{a}_{b}','compare',compare+[byte(i,0xE8,80) for i in (person,other)]+[byte(i,0xE7,0) for i in (person,other)]+[pword(person,0x10,a),pword(other,0x10,b)])
    base=[['0xdd38',9,0x30,'03'],byte(person,0xE6,3),byte(person,0xE8,89),pword(person,0x16C,0),pword(person,0x17C,-4),
        pword(person,0xBA,0),pword(person,0xBC,0),pword(person,0xB4,0),pword(person,0xB6,0),pword(person,0xB2,222),
        byte(person,0xBE,0),byte(ruler,0xBE,20),['0x73a50',person,0xC0,bytes(32).hex()],pword(person,0xD0,ruler),
        ['0xdd38',28,0x67,'00'],['worldstate',0,0x450,struct.pack('<i',tick).hex()]]
    def check(name,extra=(),dying=ruler):
        add(name,'check',base+list(extra));result[-1]['dying']=dying
    add('original_two_cities','check')
    check('eligible_zero_chance')
    for status in (1,2,4,5,6):check('status_'+str(status),[byte(person,0xE6,status)])
    check('excluded_candidate',dying=person)
    check('same_city_as_ruler',[pword(person,0xE2,13)])
    check('gate_home',[pword(person,0xE2,52)])
    check('marked_candidate_city',[['worldstate',0,0x1654,struct.pack('<H',person).hex()]])
    check('base_flag_blocks',[['0xdd38',28,0x67,'01']])
    check('spouse',[pword(person,0xBA,ruler)])
    check('sworn',[pword(person,0xBC,77),pword(ruler,0xBC,77)])
    check('parent',[pword(person,0xB4,ruler)])
    check('reverse_parent',[pword(ruler,0xB4,person)])
    check('related_without_dislike',[pword(person,0xB2,ctx.u16(0x73A50,ruler,0xB2)),['0x73a50',person,0xD0,bytes(16).hex()]])
    check('related_with_dislike',[pword(person,0xB2,ctx.u16(0x73A50,ruler,0xB2))])
    check('like_blocks',[pword(person,0xC0,ruler)])
    for distance in (0,5,6,75,255):check('compatibility_'+str(distance),[byte(person,0xBE,distance),byte(ruler,0xBE,0)])
    check('unit_place',[pword(person,0xE4,62)])
    for loyalty,service in ((89,3),(89,4),(90,0),(90,-3),(90,-4)):
        check(f'loyalty_{loyalty}_service_{service}',[byte(person,0xE8,loyalty),pword(person,0x16C,service),pword(person,0x17C,-8)])
    check('merit_gate_9',[pword(person,0x17C,9),['0x73a50',person,0xD0,bytes(16).hex()]])
    check('merit_gate_10',[pword(person,0x17C,10),['0x73a50',person,0xD0,bytes(16).hex()]])
    check('merit_gate_20',[pword(person,0x17C,20),['0x73a50',person,0xD0,bytes(16).hex()]])
    check('chance_70_false',[byte(person,0xE8,0),pword(person,0x17C,20)])
    check('no_dying_bonus',[byte(person,0xE8,80),pword(person,0x17C,20)],dying=None)
    check('invalid_dying_bonus',[byte(person,0xE8,80),pword(person,0x17C,20)],dying=0)
    imperial=ctx.officer_by_id(607)
    if imperial is None or ctx.u16(0x73A50,imperial,0x10)!=607:raise RuntimeError('Actual imperial officer lookup differs')
    for title in (6,0):
        check('imperial_ruler_title_'+str(title),[word(0xDF30,3,0x10,607),['0xdf30',3,0x48,bytes([title]).hex()],
            pword(imperial,0xE2,13),pword(imperial,0xE4,13),byte(imperial,0xE6,1),byte(imperial,0xE0,3),
            pword(imperial,0xBC,0),pword(imperial,0xB4,0),pword(imperial,0xB6,0),pword(imperial,0xB2,607),byte(imperial,0xBE,20),
            byte(person,0xE8,60),pword(person,0x17C,20)])
    check('signed_month_day_zero_chance',[['worldstate',0,0x36,'ff80']])
    check('two_candidates_sort',[byte(other,0xE6,2),byte(other,0xE8,89),pword(other,0xE2,28),pword(other,0xE4,28),pword(other,0x16C,0),pword(other,0x17C,-4),
        pword(other,0xBA,0),pword(other,0xBC,0),pword(other,0xB4,0),pword(other,0xB6,0),pword(other,0xB2,222),byte(other,0xBE,0),
        ['0x73a50',other,0xC0,bytes(32).hex()],pword(other,0xD0,ruler)])
    return result

def generate(ctx,snapshot,digest,retained=(),seeds=()):
    if not ctx.frozen:raise RuntimeError('Live native generation forbidden')
    o=ctx.o;audit=audit_state(ctx,snapshot);baseline=audit.read();snapshot=copy.deepcopy(snapshot);cases=[];seed_cases=list(seeds)
    for name,rva in RULES.items():snapshot['flags'][name]=struct.unpack('<i',o.read(ctx.g.exe+rva,4))[0]
    refs={ctx.addr(0x73A50,i):i for i in range(len(ctx.rec[0x73A50]))};scratch=o.alloc(8);o.poke(scratch,struct.pack('<Q',ctx.addr(0x73A50,557)))
    captured={};hooks=[];sorted_heads=[]
    def observe(mu,address,size,user):
        rva=address-ctx.g.exe
        if rva==0x2E5D40:raise RuntimeError('Fixture unexpectedly executes the separate rebellion body')
        if rva==0x24EEC0:
            pool=mu.reg_read(UC_X86_REG_RCX);index=mu.reg_read(UC_X86_REG_RDX)&0xffffffff
            sorted_heads.append(o.q(pool+0x10)+index*8)
        elif rva==0x3FB5F0 and o.q(mu.reg_read(UC_X86_REG_RSP))==ctx.g.exe+0x2E5A8C:
            node=o.q(sorted_heads[-1]);ordered=[];seen=set()
            while node:
                if node in seen:raise RuntimeError('Candidate list cycle')
                seen.add(node);ordered.append(refs[o.q(node)]);node=o.q(node+8)
            captured.update(candidates=ordered,draw=mu.reg_read(UC_X86_REG_RCX)&0xffffffff,chance=mu.reg_read(UC_X86_REG_RDX)&0xffffffff)
    for rva in (0x24EEC0,0x3FB5F0,0x2E5D40):hooks.append(o.mu.hook_add(UC_HOOK_CODE,observe,begin=ctx.g.exe+rva,end=ctx.g.exe+rva))
    def save():ctx.save('ruler_rebellion_progress.json.gz',dict(input_sha256=digest,snapshot=snapshot,cases=cases,seeds=seed_cases))
    try:
        tick=0
        while not seed_cases or max(c['result'] for c in seed_cases)<70:
            tick=len(seed_cases)
            if tick>=32:raise RuntimeError('No retained seed sample supports the requested no-rebellion chance boundary')
            f=dict(name='candidate_seed_'+str(tick),kind='seed',person=997,pokes=[['worldstate',0,0x450,struct.pack('<i',tick).hex()]])
            def sample():
                before=audit.read();output=[];ws=o.q(ctx.raw+0x871E8);date=o.read(ws+0x34,4);year,month,day=struct.unpack('<hbb',date)
                def after():output.append(audit.delta(before,audit.read()));return []
                draw=o.call(0x3FB600,100,1,year+month,day,stack_args=(997,0),read_after=after)
                if output[0]!={'records':[]}:raise RuntimeError('Seed sampling changed audited state')
                return dict(fixture=f,function_complete=True,state_unchanged=True,result=draw)
            c=ctx.run(f['pokes'],sample)
            if c is None:raise RuntimeError(ctx.failures[-1])
            seed_cases.append(c);save();print(f['name'],c['result'],flush=True)
        selected=max(seed_cases,key=lambda c:c['result']);tick=int(selected['fixture']['name'].split('_')[-1])
        print('Retained actual false-branch seed',tick,'draw',selected['result'],flush=True)
        prior={c['fixture']['name']:c for c in retained}
        for f in requests(ctx,tick):
            if f['name'] in prior:
                if prior[f['name']]['fixture']!=f:raise RuntimeError('Retained fixture differs')
                cases.append(prior[f['name']]);continue
            captured.clear();sorted_heads.clear()
            def execute():
                before=audit.read();output=[]
                def after():output.append(audit.delta(before,audit.read()));return []
                if f['kind']=='compare':returned=o.call(0x255DA0,scratch,ctx.addr(0x73A50,f['person']),ctx.addr(0x73A50,f['other']),read_after=after)
                else:returned=o.call(0x2E54E0,ctx.addr(0xDF30,f['force']),0 if f['dying'] is None else ctx.addr(0x73A50,f['dying']),read_after=after)
                if output[0]!={'records':[]}:raise RuntimeError('Read-only rebellion selection changed audited state: '+str(list(output[0])))
                if f['kind']=='check' and returned!=0:raise RuntimeError('No-rebellion fixture returned nonzero')
                return dict(fixture=f,function_complete=True,state_unchanged=True,result=returned,selection=copy.deepcopy(captured))
            c=ctx.run(f['pokes'],execute)
            if c is None:save();raise RuntimeError(ctx.failures[-1])
            cases.append(c);save();print(f['name'],c['result'],c['selection'],flush=True)
        if audit.read()!=baseline:raise RuntimeError('Rebellion helpers did not roll back')
        save();return publish(ctx,dict(input_sha256=digest,snapshot=snapshot,cases=cases,seeds=seed_cases))
    finally:
        for hook in hooks:o.mu.hook_del(hook)
        o.unstub()

def publish(ctx,retained):
    if not ctx.frozen or len(retained['snapshot']['tables'])!=32:raise RuntimeError('Coherent full input required')
    seeds=retained['seeds'];selected=max(seeds,key=lambda c:c['result']);tick=int(selected['fixture']['name'].split('_')[-1]);expected=requests(ctx,tick)
    if selected['result']<70 or len(retained['cases'])!=len(expected):raise RuntimeError('Missing complete selection cases')
    for case,f in zip(retained['cases'],expected):
        if case['fixture']!=f or not case['function_complete'] or not case['state_unchanged']:raise RuntimeError('Retained case differs')
    for name,rva in RULES.items():
        if retained['snapshot']['flags'][name]!=struct.unpack('<i',ctx.o.read(ctx.g.exe+rva,4))[0]:raise RuntimeError('Actual rule differs')
    ctx.save('ruler_rebellion_world.json.gz',retained['snapshot'])
    ctx.save('ruler_rebellion.json.gz',dict(world='ruler_rebellion_world.json.gz',input_sha256=retained['input_sha256'],cases=retained['cases'],seeds=seeds,
        complete=True,gameplay_stubs=False,allocator_stubs=False,presentation_stubs=False,
        scope='Whole 0x255DA0 comparator and 0x2E54E0 no-rebellion selection including real candidate order/chance/draw; actual rebellion, creation and enabled presentation remain separate.'))
    return len(retained['cases']),len(seeds)

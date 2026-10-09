# Whole ordinary player death; fixtures and all native execution stay in coherent copied memory.
import copy,struct
from context import world
from engine_audit_state import EngineAuditState
from force_events import Observer
from unit_group_cache import cache_tree
from command_storage import read_store
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8


class DeathObserver(Observer):
    def __init__(self,ctx):
        super().__init__(ctx)
        for rva in (0x308EF0,0x30DBB0,0x30DC20,0x30DD20,0x29C870,0x897530,0x451FF0,0x43FF90):
            self.hooks.append(self.o.mu.hook_add(UC_HOOK_CODE,self.death_event,begin=ctx.g.exe+rva,end=ctx.g.exe+rva))
    def death_event(self,mu,address,size,data):
        rva=address-self.ctx.g.exe;arg=mu.reg_read(UC_X86_REG_RDX);slot=mu.reg_read(UC_X86_REG_R8)&0xffffffff
        if rva==0x308EF0:self.events.append(dict(kind='message',id=arg&0xffffffff))
        elif rva==0x30DBB0:self.events.append(dict(kind='officer',id=arg&0xffffffff,slot=slot))
        elif rva==0x30DC20:self.events.append(dict(kind='text',text=self.text(arg),slot=slot))
        elif rva==0x30DD20:self.events.append(dict(kind='number',value=arg&0xffffffff,slot=slot))
        elif rva==0x29C870:
            pointer=mu.reg_read(UC_X86_REG_RCX);oid=int.from_bytes(self.o.read(pointer+0x10,2),'little')
            self.events.append(dict(kind='player_slots',person=self.ctx.officer_by_id(oid)))
        elif rva==0x897530:self.events.append(dict(kind='player_slots_refresh'))
        else:self.events.append(dict(kind='script',entry=hex(rva),value=arg&0xffffffff))


def audit_state(ctx,snapshot):
    audit=EngineAuditState(ctx)
    audit.tables={int(key,16):(ctx.addr(int(key,16),0),data['size'],len(data['records'])) for key,data in snapshot['tables'].items()}
    audit.wrappers.extend((f'force:{i}:{at:x}',ctx.addr(0xDF30,i)+at) for i in range(52) for at in (0x250,0x260))
    audit.wrappers.extend((f'registered:{h:x}',ctx.raw+h) for h in (0xA0,0xD0,0x120))
    original=audit.read
    def state():
        result=original();manager=ctx.o.q(ctx.g.exe+0x1B9A4A0)
        for name,at in (('force_cache',0x80),('corps_cache',0x90),('troop_cache',0xA0)):
            result[name]={hex(k):hex(v) for _,k,v in cache_tree(ctx.o.read,ctx.o.q,manager,at)[0]}
        refs={ctx.addr(t,i):[hex(t),i] for t,(_,_,count) in audit.tables.items() for i in range(count)}
        result['lists'].update({hex(h):[[refs[int(p,16)]] for p in result['nested'][f'registered:{h:x}']] for h in (0xA0,0xD0,0x120)})
        result.update(profile=ctx.o.read(ctx.g.exe+0x2784AB0,0x278),county_bits=ctx.o.read(ctx.g.exe+0x2141AE0,0x2000),
            manager_mark=ctx.o.read(ctx.o.q(ctx.g.exe+0x1BB3D00)+0x30B20,4),
            command_storage=read_store(ctx,audit))
        return result
    audit.read=state
    return audit


def publish(ctx,snapshot,cases,digest,stem='officer_death_player'):
    if not ctx.frozen or len(snapshot['tables'])!=32:raise RuntimeError('Complete coherent 32-table player input required')
    snapshot=copy.deepcopy(snapshot)
    for command in snapshot['commands']:
        pointer=int(command['address'],16)
        if ctx.o.read(pointer,len(bytes.fromhex(command['data']))).hex()!=command['data']:raise RuntimeError('Command input differs')
        vtable=ctx.o.q(pointer)
        command['handlers']={hex(at):hex(ctx.o.q(vtable+at)-ctx.g.exe) for at in (0x68,0x90,0xA8,0xB0,0xC8,0xD8,0xF0,0x110,0x118)}
    force=3;title=ctx.rec[0xDF30][force][0x48];address=ctx.addr(0x78168,title)
    # Actual title validity uses 0x234160; title zero falls back through 0x22ED00/0x22EF00.
    if ctx.o.q(ctx.o.q(address)+0x18)-ctx.g.exe!=0x234160:raise RuntimeError('Title validity dispatch differs')
    if address!=ctx.addr(0x78168,0):
        initial=ctx.o.read(address+0x10,2);source=address+0x10
    else:
        if force not in range(1,46):raise RuntimeError('The force-name localization path has not been captured')
        ruler=ctx.addr(0x73A50,ctx.officer_by_id(ctx.u16(0xDF30,force,0x10)))
        if ruler==ctx.addr(0x73A50,0):raise RuntimeError('The force-name ruler is invalid')
        source=ruler+0x12;initial=ctx.o.read(source,2)
        if initial==bytes(2):source=ruler+0x20;initial=ctx.o.read(source,2)
    snapshot['force_initial_input']=dict(force=force,title=title,address=hex(source),text=initial.hex())
    # Resolve the actual RIP-relative return instruction, rather than a decompiler DAT label.
    code=ctx.o.read(ctx.g.exe+0x1EF3F4,7)
    if code[:3]!=bytes.fromhex('488d05'):raise RuntimeError('Playback singleton instruction differs')
    manager=ctx.g.exe+0x1EF3FB+struct.unpack_from('<i',code,3)[0]
    snapshot['playback_input']=dict(address=hex(manager),state=int.from_bytes(ctx.o.read(manager+8,4),'little',signed=True))
    snapshot['exe_settings_rva']=world.SETTINGS_RVA;snapshot['rng']=ctx.o.read(ctx.g.exe+0x1A47B90,4).hex()
    for case in cases:
        if not case.get('function_complete') or any(e['kind']=='script' for e in case['events']):raise RuntimeError('Unfinished or office/ruler script case')
    ctx.save(stem+'_world.json.gz',snapshot)
    ctx.save(stem+'.json.gz',dict(world=stem+'_world.json.gz',cases=cases,input_sha256=digest,
        complete=True,gameplay_stubs=False,allocator_stubs=False,presentation_stubs=False,
        scope='Whole no-item ordinary player officer death with valid adviser, no active command and no related-ruler playback event; physical UI/playback heap is a host boundary.'))
    return len(cases)


def generate(ctx,snapshot,retained,digest):
    if not ctx.frozen:raise RuntimeError('Live native generation forbidden')
    o=ctx.o;audit=audit_state(ctx,snapshot);baseline=audit.read();observer=DeathObserver(ctx);cases=[]
    initial=retained[0]['fixture'];requests=[copy.deepcopy(initial)]
    for name,mode,include in (('player_ordinary_no_history',0,False),('player_ordinary_killed_history',1,True)):
        fixture=copy.deepcopy(initial);fixture.update(name=name,mode=mode,history=include);requests.append(fixture)
    fixture=copy.deepcopy(initial);fixture.update(name='player_ordinary_alias_slots')
    fixture['pokes'] += [['worldstate',0,0x165E,struct.pack('<HH',fixture['person'],fixture['person']).hex()],
        ['worldstate',0,0x1668,struct.pack('<II',7,9).hex()]]
    requests.append(fixture)
    fixture=copy.deepcopy(initial);fixture.update(name='player_ordinary_marked')
    bits=ctx.u16(0x73A50,fixture['person'],0x15E)
    fixture['pokes'] += [['0x73a50',fixture['person'],0x15E,struct.pack('<H',bits|0x4000).hex()],['0x73a50',fixture['person'],0x1C5,'01']]
    requests.append(fixture)
    prior={c['fixture']['name']:c for c in retained}
    try:
        for f in requests:
            if f['name'] in prior:
                if prior[f['name']]['fixture']!=f:raise RuntimeError('Retained fixture differs')
                cases.append(prior[f['name']]);continue
            observer.events.clear()
            def call():
                before=audit.read();captured=[]
                def after():captured.append(audit.delta(before,audit.read()));return []
                returned=o.call(0x295AA0,ctx.addr(0x73A50,f['person']),0,f['mode'],0,
                    stack_args=(0,0,0,int(f['history']),0),read_after=after)
                return dict(fixture=f,function_complete=True,result=hex(returned),after=captured[0],events=copy.deepcopy(observer.events))
            case=ctx.run(f['pokes'],call)
            if case is None:
                print(f['name'],'INCOMPLETE',ctx.failures[-1],flush=True)
                ctx.save('officer_death_player_failures.json.gz',dict(input_sha256=digest,failures=ctx.failures))
                continue
            cases.append(case);ctx.save('officer_death_player_progress.json.gz',dict(cases=cases,input_sha256=digest))
            print(f['name'],'complete',len(case['after']['records']),flush=True)
        if audit.read()!=baseline:raise RuntimeError('Player death did not roll back')
        return publish(ctx,snapshot,cases,digest)
    finally:observer.close();o.unstub()

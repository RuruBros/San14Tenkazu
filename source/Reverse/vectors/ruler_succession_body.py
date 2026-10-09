# Whole NPC succession with explicit native UI/history suppression in copied input.
# The original enabled-presentation body and rebellion paths remain separate proofs.
import copy
from context import world
from officer_death_player import audit_state,DeathObserver
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8,UC_X86_REG_R9

class SuccessionObserver(DeathObserver):
    def __init__(self,ctx):
        super().__init__(ctx);self.entries=[]
        for rva in (0x28DB70,0x2E54E0,0x275F40,0x264DD0,0x2E8E70,0x26CCC0,0x1B4E60,0x17E5E0):
            self.hooks.append(self.o.mu.hook_add(UC_HOOK_CODE,self.entry,begin=ctx.g.exe+rva,end=ctx.g.exe+rva))
    def entry(self,mu,address,size,data):
        rva=address-self.ctx.g.exe
        args=[mu.reg_read(r) for r in (UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8,UC_X86_REG_R9)]
        self.entries.append(dict(rva=hex(rva),args=[hex(a) for a in args]))
        if rva==0x1B4E60:self.events.append(dict(kind='map_refresh'))
        elif rva==0x17E5E0:self.events.append(dict(kind='path_cache_reset'))

def trace(ctx,snapshot,retained,digest):
    if not ctx.frozen or not retained['function_complete']:raise RuntimeError('Complete coherent NPC input required')
    o=ctx.o;audit=audit_state(ctx,snapshot)
    managers=[o.q(ctx.g.exe+0x1B91DA0+at) for at in range(8,0x38,8)]
    path=o.q(ctx.g.exe+0x1BB3CF8)
    if not path:raise RuntimeError('Actual path cache manager absent')
    original=audit.read
    def state():
        result=original();result['map_marks']=[None if not p else o.read(p+0xA0,4).hex() for p in managers]
        result['path_cache']=o.read(path,0x28)
        return result
    audit.read=state;baseline=state();observer=SuccessionObserver(ctx)
    f=copy.deepcopy(retained['fixture']);at=int(f['host_pokes'][0][0],16);control=o.read(at,4)
    refs={ctx.addr(0x85F20,i):i for i in range(80)}
    snapshot=copy.deepcopy(snapshot)
    snapshot['doctrine_byid']=[refs[o.q(ctx.raw+0xBCE0+i*8)] for i in range(80)]
    snapshot['flags']['loyalty_join_limit']=int.from_bytes(o.read(ctx.g.exe+0x27D2638,4),'little',signed=True)
    snapshot['desertion_loyalty_limit']=int.from_bytes(o.read(ctx.g.exe+0x1A4968C,4),'little',signed=True)
    snapshot['map_managers']=[None if not p else dict(address=hex(p),data=o.read(p,0xA4).hex()) for p in managers]
    snapshot['path_cache_input']=dict(address=hex(path),data=baseline['path_cache'].hex())
    snapshot['rng']=baseline['rng'].hex();snapshot['exe_settings_rva']=world.SETTINGS_RVA
    def execute():
        output=[]
        def after():output.append(audit.delta(baseline,state()));return []
        returned=o.call(0x28C180,ctx.addr(0x73A50,f['dying']),read_after=after)
        return dict(fixture=f,function_complete=True,result=hex(returned),after=output[0],events=copy.deepcopy(observer.events),entries=copy.deepcopy(observer.entries))
    try:
        o.poke(at,bytes.fromhex(f['host_pokes'][0][1]));case=ctx.run(f['pokes'],execute)
        if case is None:raise RuntimeError(ctx.failures[-1])
        expected={k:v for k,v in retained['after'].items() if k!='map_mark'}
        actual={k:v for k,v in case['after'].items() if k not in ('map_marks','path_cache')}
        if actual!=expected:raise RuntimeError('Semantic trace differs from retained whole result')
        old=[e for e in case['events'] if e['kind'] not in ('script','map_refresh','path_cache_reset')]
        if old!=retained['events']:raise RuntimeError('Previously recorded event sequence differs')
        ctx.save('ruler_succession_body_trace.json.gz',dict(input_sha256=digest,snapshot=snapshot,case=case))
        return case
    finally:
        o.poke(at,control);observer.close();o.unstub()
        if state()!=baseline:raise RuntimeError('Succession did not roll back')


def publish(ctx,retained,stem='ruler_succession_body'):
    # Packaging consumes retained results only; the caller forbids native calls and live process access.
    snapshot=copy.deepcopy(retained['snapshot']);case=copy.deepcopy(retained['case']);digest=retained['input_sha256']
    if not ctx.frozen or len(snapshot['tables'])!=32 or not case['function_complete']:
        raise RuntimeError('Complete coherent 32-table succession input required')
    control=snapshot['ui_event_guard_input']
    if case['fixture']['host_pokes']!=[[control['address'],control['fixture']]] or control['fixture']!='01000000':
        raise RuntimeError('Explicit native UI/history suppression fixture differs')
    if ctx.o.read(int(control['address'],16),4).hex()!=control['data']:
        raise RuntimeError('Original suppression input differs')
    refs={ctx.addr(0x85F20,i):i for i in range(80)}
    snapshot['doctrine_byid']=[refs[ctx.o.q(ctx.raw+0xBCE0+i*8)] for i in range(80)]
    snapshot['flags']['loyalty_join_limit']=int.from_bytes(ctx.o.read(ctx.g.exe+0x27D2638,4),'little',signed=True)
    snapshot['desertion_loyalty_limit']=int.from_bytes(ctx.o.read(ctx.g.exe+0x1A4968C,4),'little',signed=True)
    snapshot['flags']['departure_loyalty_limit']=snapshot['desertion_loyalty_limit']
    snapshot['rng']=ctx.o.read(ctx.g.exe+0x1A47B90,4).hex();snapshot['exe_settings_rva']=world.SETTINGS_RVA
    if 'map_mark' in case['after']:
        if case['after']['map_mark']!=case['after']['map_marks'][5]:raise RuntimeError('Duplicate sixth map mark differs')
        del case['after']['map_mark']
    for command in snapshot['commands']:
        address=int(command['address'],16)
        if ctx.o.read(address,len(bytes.fromhex(command['data']))).hex()!=command['data']:
            raise RuntimeError('Command input differs')
        vtable=ctx.o.q(address)
        command['handlers']={hex(at):hex(ctx.o.q(vtable+at)-ctx.g.exe) for at in (0x68,0x90,0xA8,0xB0,0xC8,0xD8,0xF0,0x110,0x118)}
    ctx.save(stem+'_world.json.gz',snapshot)
    ctx.save(stem+'.json.gz',dict(world=stem+'_world.json.gz',cases=[case],
        input_sha256=digest,complete=True,gameplay_stubs=False,allocator_stubs=False,presentation_stubs=False,
        scope='One whole NPC 0x28C180 with explicit copied UI/history suppression, two cities, same corps and no rebellion, optionally ordinary departure without items or commands; original enabled UI/history, player and physical UI/allocator heap remain unverified.'))
    return 1

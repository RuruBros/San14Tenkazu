# Actual event bodies and their localization/presentation requests, executed in one coherent frozen scene.
import copy,struct
from capstone.x86 import X86_OP_MEM
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8,UC_X86_REG_R9,UC_X86_REG_RSP
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from context import FORCES,CORPS,OFFICERS

COVER=[0x277140,0x274980,0x256DF0,0x274EE0,0x292C00,0x2700A0,0x2AD780]

class Observer:
    def __init__(self,ctx):
        self.ctx,self.o=ctx,ctx.o;self.events=[];self.hooks=[];ctx.im.md.detail=True
        self.records={ctx.addr(0x79E90,i):i for i in range(2001)}
        for fn in (0x277140,0x274980,0x256DF0):
            for ins in ctx.im.flow(fn):
                if ins.mnemonic=='call' and ins.operands[0].type==X86_OP_MEM and ins.operands[0].mem.disp==8:
                    self.hooks.append(self.o.mu.hook_add(UC_HOOK_CODE,self.selected,begin=ctx.g.exe+ins.address,end=ctx.g.exe+ins.address))
        for rva in (0x30A2F0,0x30D910,0x30D930,0x43A630,0x934400,0x404A00,0x1F0AD0,0x1F0DD0,0x1A3D40,0x3FBB00):
            self.hooks.append(self.o.mu.hook_add(UC_HOOK_CODE,self.event,begin=ctx.g.exe+rva,end=ctx.g.exe+rva))
    def selected(self,mu,address,size,user):self.events.append(dict(kind='message',id=mu.reg_read(UC_X86_REG_RDX)&0xffffffff))
    def text(self,pointer):
        data=bytearray()
        for i in range(4096):
            pair=self.o.read(pointer+i*2,2)
            if pair==bytes(2):return bytes(data).hex()
            data.extend(pair)
        raise RuntimeError('History text exceeds bound')
    def event(self,mu,address,size,user):
        o=self.o;rva=address-self.ctx.g.exe;args=[mu.reg_read(r)&0xffffffff for r in (UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8,UC_X86_REG_R9)];s=mu.reg_read(UC_X86_REG_RSP)
        if rva==0x30A2F0:self.events.append(dict(kind='reset'))
        elif rva==0x30D910:self.events.append(dict(kind='force',index=args[1],slot=args[2]))
        elif rva==0x30D930:self.events.append(dict(kind='pair',args=args[1:]+[struct.unpack('<I',o.read(s+0x28+i*8,4))[0] for i in range(2)]))
        elif rva==0x43A630:self.events.append(dict(kind='history_refresh',index=self.records[mu.reg_read(UC_X86_REG_RDX)]))
        elif rva==0x934400:
            stack=list(struct.unpack('<9Q',o.read(s+0x28,72)))
            self.events.append(dict(kind='history',args=args[1:]+[v&0xffffffff for v in stack[:2]]+[v&0xffffffff for v in stack[4:]],texts=[self.text(stack[2]),self.text(stack[3])]))
        elif rva==0x404A00:self.events.append(dict(kind='sound',value=args[1]))
        elif rva==0x1F0AD0:self.events.append(dict(kind='announcement'))
        elif rva==0x1F0DD0:self.events.append(dict(kind='speaker',id=args[0]))
        elif rva==0x1A3D40:self.events.append(dict(kind='coalition_refresh'))
        elif rva==0x3FBB00:self.events.append(dict(kind='presentation_rng',n=struct.unpack('<i',struct.pack('<I',args[0]))[0],
          before=struct.unpack('<i',o.read(self.ctx.g.exe+0x1A47B90,4))[0]))
    def close(self):
        for h in self.hooks:self.o.mu.hook_del(h)

def publish(ctx,snapshot,cases,scenario_cases,prefix='force_events'):
    """Package retained complete results without executing another native call."""
    if not ctx.frozen:raise RuntimeError('Force events require a coherent frozen input')
    snapshot=copy.deepcopy(snapshot)
    snapshot.update(coalition_relation_gain=struct.unpack('<i',ctx.o.read(ctx.g.exe+0x1A489A4,4))[0],
      coalition_refresh_available=bool(ctx.o.q(ctx.g.exe+0x1B91DA8)),
      rng=ctx.o.read(ctx.g.exe+0x1A47B90,4).hex(),exe_settings_rva=0x1A46CA0)
    for c in cases+scenario_cases:
        if 'events' not in c or 'after' not in c:raise RuntimeError('Incomplete retained event')
    ctx.save(prefix+'_world.json.gz',snapshot)
    ctx.save(prefix+'.json.gz',dict(world=prefix+'_world.json.gz',cases=cases,scenario_cases=scenario_cases,complete=True,
      gameplay_stubs=False,allocator_stubs=False,
      scope='Whole force events with nontribal coalition relations and exact semantic localization/history/presentation requests; scenario helper guards; physical UI heap separate.'))
    return len(cases),len(scenario_cases)

def generate(ctx,snapshot,retained,completed=(),include_speakers=False):
    if not ctx.frozen:raise RuntimeError('Force events require a coherent frozen input')
    o=ctx.o;capture_native_pools(ctx);audit=EngineAuditState(ctx)
    audit.tables[0x79E90]=(ctx.addr(0x79E90,0),0x140,2001);audit.tables[0x85A90]=(ctx.addr(0x85A90,0),0x78,101)
    audit.wrappers.append(('registered_counties',ctx.raw+0xB0));baseline=audit.read()
    snapshot=copy.deepcopy(snapshot);gain=struct.unpack('<i',o.read(ctx.g.exe+0x1A489A4,4))[0]
    snapshot.update(coalition_relation_gain=gain,coalition_refresh_available=bool(o.q(ctx.g.exe+0x1B91DA8)))
    initial=list(retained);prior={c['fixture']['name']:c for c in initial+list(completed)};requests=[]
    for c in initial:
        f=copy.deepcopy(c['fixture']);f['gain']=gain;requests.append(f)
        c['fixture']=f
    base=copy.deepcopy(initial[2]['fixture']['pokes'])
    def case(name,rva=0x274980,defeated=1,victor=23,pokes=(),**kwargs):
        f=dict(name=name,rva=rva,defeated=defeated,victor=victor,pokes=base+list(pokes),gain=gain);f.update(kwargs);requests.append(f)
    case('leader_mismatch',rva=0x277140,defeated=23,pokes=[['worldstate',0,0x1657,'03']])
    case('leader_invalid_central',rva=0x277140,defeated=23,pokes=[['worldstate',0,0x1656,'0017']])
    case('target_mismatch',defeated=23)
    case('target_invalid_central',defeated=0,victor=0,pokes=[['worldstate',0,0x1656,'0003']])
    case('victory_invalid_leader',pokes=[['worldstate',0,0x1657,'00']])
    lead_flags=ctx.rec[FORCES][3][0x12]&0xF9|6
    # Speaker UI allocations need their own independently guarded scene; never overlay its pages here.
    if include_speakers:
        case('victory_npc_speaker',pokes=[['0xdf30',3,0x12,bytes([lead_flags]).hex()]])
        case('victory_player_speaker',pokes=[['0xdf30',3,0x12,bytes([lead_flags]).hex()],['worldstate',0,0x3A,'03']])
    for value in (0,-20,100,2147483647):case(f'relationship_gain_{value}',gain=value)
    corps_for=lambda force:next(i for i,b in enumerate(ctx.rec[CORPS]) if i and b[0x10]==force and b[0x11] and struct.unpack_from('<H',b,0x12)[0])
    cities=[n[0][1] for n in ctx.snap['lists']['0x90']['items']]
    for force,value in ((3,0),(23,50)):
        pokes=[['worldstate',0,0x40,bytes(4).hex()],['0xdf30',3,0x13+23,'28'],['0xdf30',23,0x13+3,'1e']]
        pokes += [['0xdd38',i,0x30,bytes([corps_for(force)]).hex()] for i in cities]
        pokes += [['0xdd38',i,0xCE,'01'] for i in cities]
        pokes += [['vec',['0x85990',1],[0xA0,0],''],['0x85990',1,0x95,bytes([1,8,0,value,0]).hex()]]
        pokes += [['0xdc80',i,0x30,'01'] for i in range(1,23)]
        case(f'advantage_force_{force}_value_{value}',pokes=pokes,gain=100)
    scenario=[];p=ctx.byid[521]
    if p is None or o.q(ctx.raw+0x11A0)!=ctx.addr(OFFICERS,p):raise RuntimeError('raw+11A0 is not the ID521 lookup')
    ws=bytes.fromhex(snapshot['worldstate']);current_year,current_month,current_day=struct.unpack_from('<hBB',ws,0x34)
    def packed(year,month,day):return struct.pack('<I',((year&0x7fffff)<<9)|((month&15)<<5)|(day&31)).hex()
    setup=[['worldstate',0,0x169C,struct.pack('<I',struct.unpack_from('<I',ws,0x169C)[0]&~16).hex()],['worldstate',0,0x1656,'0103'],
      ['0xdf30',1,0x10,struct.pack('<H',736).hex()],['0xdf30',3,0x10,struct.pack('<H',35).hex()],
      ['0xdf30',1,0x12,bytes([ctx.rec[FORCES][1][0x12]|2]).hex()],['0x73a50',p,0xE6,'01'],
      ['0x73a50',p,0xE0,bytes([corps_for(1)]).hex()],['worldstate',0,0x460,'08000000'],
      ['worldstate',0,0x56C,packed(current_year,current_month,current_day)],['worldstate',0,0x1654,struct.pack('<H',521).hex()]]
    def sc(name,pokes=()):scenario.append(dict(name=name,rva=0x274EE0,defeated=0,victor=0,pokes=setup+list(pokes),gain=gain))
    sc('eligible');sc('already_set',[['worldstate',0,0x169C,'10000000']]);sc('central_invalid',[['worldstate',0,0x1656,'00']])
    sc('central_ruler_mismatch',[['0xdf30',1,0x10,'2300']]);sc('leader_invalid',[['worldstate',0,0x1657,'00']])
    sc('leader_ruler_mismatch',[['0xdf30',3,0x10,'e002']]);sc('specific_officer_not_ruler',[['0x73a50',p,0xE6,'02']])
    sc('not_coalition',[['0xdf30',1,0x12,bytes([ctx.rec[FORCES][1][0x12]&~2]).hex()]])
    sc('event3_absent',[['worldstate',0,0x460,bytes(4).hex()]]);sc('event5_present',[['worldstate',0,0x460,'28000000']])
    sc('elapsed_719',[['worldstate',0,0x56C,packed(current_year-2,current_month,current_day+1)]])
    sc('elapsed_720',[['worldstate',0,0x56C,packed(current_year-2,current_month,current_day)]])
    sc('future_date',[['worldstate',0,0x56C,packed(current_year+1,current_month,current_day)]])
    sc('missing_participant',[['worldstate',0,0x1654,'ffff']])
    observer=Observer(ctx);cases=[];scenario_cases=[];failures=[]
    try:
        for group,output in ((requests,cases),(scenario,scenario_cases)):
            for f in group:
                if f['name'] in prior:
                    if prior[f['name']]['fixture']!=f:raise RuntimeError('Retained event fixture differs')
                    output.append(prior[f['name']]);continue
                observer.events.clear();at=ctx.g.exe+0x1A489A4;original=o.read(at,4)
                try:
                    o.poke(at,struct.pack('<i',f['gain']))
                    def run():
                        before=audit.read();captured=[]
                        def after():captured.append(audit.delta(before,audit.read()));return []
                        args=[ctx.addr(FORCES,f['defeated'])]+([ctx.addr(FORCES,f['victor'])] if f['rva']==0x274980 else [])
                        o.call(f['rva'],*args,read_after=after)
                        if set(captured[0])-{'records','worldstate'}:raise RuntimeError('Unexpected event gameplay mutation')
                        return dict(fixture=f,after=captured[0],events=copy.deepcopy(observer.events))
                    c=ctx.run(f['pokes'],run)
                    if c is None:
                        failures.append(dict(name=f['name'],failure=ctx.failures[-1]))
                        print('  force event',f['name'],'INCOMPLETE',ctx.failures[-1]['error'],flush=True)
                        continue
                    output.append(c);ctx.save('force_events_progress.json.gz',dict(cases=cases,scenario_cases=scenario_cases))
                    print('  force event',f['name'],'complete',flush=True)
                finally:o.poke(at,original)
        if audit.read()!=baseline:raise RuntimeError('Force events did not roll back')
        if failures:
            ctx.save('force_events_failures.json.gz',dict(failures=failures))
            raise RuntimeError('Incomplete cases excluded; no final vectors published: '+','.join(f['name'] for f in failures))
        return publish(ctx,snapshot,cases,scenario_cases)
    finally:observer.close();o.unstub()

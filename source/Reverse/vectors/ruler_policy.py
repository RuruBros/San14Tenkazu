# Native policy/declaration reset and ruler composition on one coherent frozen input.
import copy, struct
from context import FORCES, OFFICERS, world
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from unit_group_cache import cache_tree
from unicorn import UC_HOOK_CODE

COVER = [0x26CCC0,0x2E8D50,0x2E8C60,0x335120,0x322D50,0x235570,0x2ED340,0x224E30,0x224CC0,0x28DB70]


def generate(ctx, retained=(), kinds=None, basename='ruler_policy'):
    if not ctx.frozen: raise RuntimeError('Policy collection requires a coherent frozen input')
    o=ctx.o;capture_native_pools(ctx);audit=EngineAuditState(ctx)
    first=ctx.addr(0x85DB8,0);size,count=0x38,7
    if any(ctx.addr(0x85DB8,i)!=first+i*size for i in range(count)):raise RuntimeError('Loyalty table differs')
    audit.tables[0x85DB8]=(first,size,count)
    audit.snapshot['tables']['0x85db8']=dict(size=size,address=hex(first),extra={},records=[o.read(first+i*size,size).hex() for i in range(count)])
    world.EXE_VALUES['loyalty_join_limit']=(0x27D2638,'int')
    audit.snapshot['flags']['loyalty_join_limit']=struct.unpack('<i',o.read(ctx.g.exe+0x27D2638,4))[0]
    for rva,value in ((0x1A48050,10),(0x1A48644,1),(0x1A49CAC,2)):
        if struct.unpack('<i',o.read(ctx.g.exe+rva,4))[0]!=value:raise RuntimeError('Policy constant differs')
    refs={ctx.addr(t,i):[hex(t),i] for t,(_,_,n) in audit.tables.items() for i in range(n)}
    doctrine_byid=[refs[o.q(ctx.raw+0xBCE0+i*8)][1] for i in range(80)]
    if any(refs[o.q(ctx.raw+0xBCE0+i*8)][0]!='0x85f20' for i in range(80)):raise RuntimeError('Doctrine ID pointer outside its table')
    audit.wrappers.extend((f'force:{i}:{at:x}',ctx.addr(FORCES,i)+at) for i in range(52) for at in (0x250,0x260))
    manager=o.q(ctx.g.exe+0x1B9A4A0)
    def state():
        s=audit.read();s['corps_cache']={hex(k):hex(v) for _,k,v in cache_tree(o.read,o.q,manager,0x90)[0]}
        s['lists'].update({hex(h):[[refs[int(p,16)]] for p in audit.members(o.q(ctx.raw+h))] for h in (0xA0,0xD0,0xE0,0x120)})
        return s
    baseline=state();snapshot=copy.deepcopy(audit.snapshot);snapshot['nested']=baseline['nested'];snapshot['corps_cache']=baseline['corps_cache']
    snapshot['doctrine_byid']=doctrine_byid
    for h in (0xA0,0xD0,0xE0,0x120):snapshot['lists'][hex(h)]={'items':baseline['lists'][hex(h)]}
    for key in snapshot['tables']:snapshot['tables'][key]['address']=hex(ctx.addr(int(key,16),0))
    for i in range(31):
        vector=world.vector(o,ctx.addr(0x73958,i),0x50)
        if vector:snapshot['tables']['0x73958']['extra'].setdefault(str(i),{})['vec50']=vector
    output=o.alloc(16);o.poke(output,struct.pack('<QQ',ctx.g.exe+0x13436B0,o.q(ctx.addr(FORCES,3)+0x260)))
    fixtures=[]
    def p(t,i,at,value,fmt='B'):return [hex(t),i,at,struct.pack('<'+fmt,value).hex()]
    def add(name,kind,pokes=(),**kw):fixtures.append(dict(name=name,kind=kind,pokes=list(pokes),**kw))
    fixtures.append(dict(name='original',kind='reset',force=3,successor=176,old=551,notify=False,pokes=[]))
    add('same_clan','reset',[p(OFFICERS,176,0x14C,ctx.rec[OFFICERS][551][0x14C])],force=3,successor=176,old=551,notify=False)
    for name,kw in [('null_force',{'force':None}),('force_zero',{'force':0}),('null_successor',{'successor':None}),('null_old',{'old':None})]:
        args=dict(force=3,successor=176,old=551,notify=False);args.update(kw);add(name,'reset',**args)
    for force in (3,0,None):
        for refresh in (False,True):add(f'advisor_{force}_{refresh}','advisor',force=force,refresh=refresh)
    for field in (-1,0,1,2,3,4,5,2**31-1):add(f'field_{field}','field',force=3,field=field)
    for force in (0,None):
        for field in (1,3,4):add(f'field_{force}_{field}','field',force=force,field=field)
    for panel in (0,1,2,7,79,80):add(f'cost_{panel}','cost',panel=panel,person=551)
    for person in (None,0,176):add(f'cost_person_{person}','cost',panel=2,person=person)
    for name,packet,clan in [('one_discount','010100020200',1),('both_discount','020b00020b00',2),('zero_first','010000020300',1),
            ('zero_second','010300020000',1),('zero_kind','000000000000',0),('maximum','01ff0002ff00',1)]:
        add(name,'cost',[['0x85f20',2,0x3D,packet],p(OFFICERS,551,0x14C,clan)],panel=2,person=551)
    for name,extra,person in [('refund_original',[],551),('refund_default',[],None),
            ('refund_all',[[hex(FORCES),3,0x1CB,'ff'*10]],551),('refund_cap',[p(FORCES,3,0x1C5,98),[hex(FORCES),3,0x1CB,'ff'*10]],551),
            ('refund_invalid_person',[],0)]:add(name,'refund',extra,force=3,person=person)
    add('reset_doctrine_original','doctrine',force=3)
    add('reset_doctrine_no_ruler','doctrine',[p(FORCES,3,0x10,0,'H')],force=3)
    # The field-1 refresh is checked with over-cap morale and the field-3 refresh with over-cap durability.
    owned_bases=[n[0][1] for n in baseline['lists']['0x80'] if n[0][0]=='0xdd38' and ctx.rec[0xDD38][n[0][1]][0x30]==ctx.rec[OFFICERS][551][0xE0]]
    if owned_bases:
        city=owned_bases[0];building=ctx.u16(0xDD38,city,0x4E)
        add('morale_cap','field',[p(0xDD38,city,0x44,255)],force=3,field=1)
        add('durability_cap','field',[p(0x6DA98,building,0x14,9999,'H')],force=3,field=3)
    gates=[n[0][1] for n in baseline['lists']['0xa0']]
    if gates:
        gate=gates[0];b=ctx.u16(0xDED8,gate,0x4E);tile=ctx.u16(0x6DA98,b,0x10);county=ctx.u16(0xE270,tile,0x12);center=ctx.u16(0x6CAF0,county,0x3A)
        add('gate_cap','gate',[p(FORCES,3,0x1F5,0x80),p(FORCES,3,0x1D1,0),p(0xE270,center,0x14,3),p(0xDED8,gate,0x3C,999999,'i')],force=3)
    buildings=[n[0][1] for n in baseline['lists']['0xe0'] if ctx.rec[0x6DA98][n[0][1]][0x12] in range(1,31) and ctx.rec[0x73958][ctx.rec[0x6DA98][n[0][1]][0x12]][0x2C]==2]
    if buildings:
        b=buildings[0];kind=ctx.rec[0x6DA98][b][0x12]
        for complete in (False,True):add(f'facility_cap_{complete}','facility',[p(FORCES,3,0x1F6,0x20),p(FORCES,3,0x1D2,0),p(0x6DA98,b,0x13,3),p(0x6DA98,b,0x16,int(complete)),p(0x6DA98,b,0x14,9999,'H')],force=3)
        add('facility_raw_cap','facility',[p(FORCES,3,0x1F6,0x20),p(FORCES,3,0x1D2,0),p(0x6DA98,b,0x13,3),p(0x6DA98,b,0x14,9999,'H'),['vec',[hex(0x73958),kind],[0x50,0],'']],force=3)
    dying=ctx.officer_by_id(ctx.u16(FORCES,3,0x10));corps=ctx.rec[OFFICERS][dying][0xE0]
    successor=next(i for i,b in enumerate(ctx.rec[OFFICERS]) if i!=dying and b[0xE0]==corps and b[0xE6] in (2,3,4) and not ctx.u16(OFFICERS,i,0x15E)&0x4000)
    add('change_with_policy','change',dying=dying,successor=successor)
    add('change_same_clan_with_policy','change',[p(OFFICERS,successor,0x14C,ctx.rec[OFFICERS][dying][0x14C])],dying=dying,successor=successor)
    if kinds is not None: fixtures=[f for f in fixtures if f['kind'] in kinds]
    saved={c['fixture']['name']:c for c in retained};cases=[];events=[];hooks=[]
    def host(mu,address,size,data):events.append(hex(address-ctx.g.exe))
    try:
        for rva in (0x1F0AD0,0x1B4E60,0x450D30,0x88D380):hooks.append(o.mu.hook_add(UC_HOOK_CODE,host,begin=ctx.g.exe+rva,end=ctx.g.exe+rva))
        for f in fixtures:
            if f['name'] in saved:
                if saved[f['name']]['fixture']!=f:raise RuntimeError('Retained fixture differs')
                cases.append(saved[f['name']]);continue
            events.clear()
            def run():
                before=state();after=[];packet=[];kind=f['kind']
                def inspect():after.append(state());return []
                ptr=lambda t,k:0 if f[k] is None else ctx.addr(t,f[k])
                if kind=='reset':o.call(0x26CCC0,ptr(FORCES,'force'),ptr(OFFICERS,'successor'),ptr(OFFICERS,'old'),int(f['notify']),read_after=inspect)
                elif kind=='advisor':o.call(0x2E8D50,ptr(FORCES,'force'),int(f['refresh']),read_after=inspect)
                elif kind=='field':o.call(0x2E8C60,ptr(FORCES,'force'),f['field'],read_after=inspect)
                elif kind=='cost':
                    def read_cost():
                        from unicorn.x86_const import UC_X86_REG_RAX
                        addr=o.mu.reg_read(UC_X86_REG_RAX);packet.append(o.read(addr,6).hex() if addr else None);return inspect()
                    o.call(0x322D50,f['panel'],ptr(OFFICERS,'person'),read_after=read_cost)
                elif kind=='refund':o.call(0x335120,ptr(FORCES,'force'),ptr(OFFICERS,'person'),read_after=inspect)
                elif kind=='doctrine':o.call(0x235570,ptr(FORCES,'force'),read_after=inspect)
                elif kind=='gate':o.call(0x224E30,ptr(FORCES,'force'),read_after=inspect)
                elif kind=='facility':o.call(0x224CC0,ptr(FORCES,'force'),read_after=inspect)
                elif kind=='change':o.call(0x28DB70,ptr(OFFICERS,'dying'),ptr(OFFICERS,'successor'),output,1,read_after=inspect)
                else:raise RuntimeError('Unknown policy fixture')
                delta=audit.delta(before,after[0])
                if kind!='change' and any(k!='records' for k in delta):raise RuntimeError('Unexpected policy engine mutation')
                if kind=='cost' and delta!={'records':[]}:raise RuntimeError('Cost helper mutated game records')
                return dict(fixture=f,after=delta,cost=packet[0] if packet else None,host_events=list(events))
            result=ctx.run(f['pokes'],run)
            if result is None:raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
            cases.append(result);ctx.save(basename+'_progress.json.gz',dict(cases=cases))
            print('  policy',f['name'],'records',len(result['after']['records']),flush=True)
        if state()!=baseline:raise RuntimeError('Policy collection did not roll back')
        ctx.save(basename+'_world.json.gz',snapshot)
        ctx.save(basename+'.json.gz',dict(world=basename+'_world.json.gz',cases=cases,gameplay_stubs=False,presentation_stubs=False,
            output_wrapper='force:3:260',groups={k:sum(c['fixture']['kind']==k for c in cases) for k in {c['fixture']['kind'] for c in cases}}))
        return f'{len(cases)} complete native policy cases'
    finally:
        for h in hooks:o.mu.hook_del(h)
        o.unstub();world.EXE_VALUES.pop('loyalty_join_limit')

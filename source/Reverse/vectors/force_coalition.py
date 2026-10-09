# Coalition cleanup and native five-byte date arithmetic on one coherent frozen input.
import copy,struct
from context import FORCES,TILES,OFFICERS,CORPS,UNITS
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from unicorn import UC_HOOK_CODE,UC_HOOK_MEM_WRITE
from unicorn.x86_const import UC_X86_REG_RDX

COVER=[0x292C00,0x31CBA0,0x31ABE0,0x3331F0,0x1A3D40]


def repaint(ctx,snapshot,probe,retained=None):
    if not ctx.frozen:raise RuntimeError('Coalition repaint requires a coherent frozen input')
    o=ctx.o;tile=probe['tile'];manager=o.q(ctx.g.exe+0x1BB3D00);map_host=o.q(ctx.g.exe+0x1B91DD0)
    fixture=dict(name='coalition_repaint',pokes=probe['pokes'],days=snapshot['coalition_days'],tile=tile)
    if retained is not None:
        if retained['fixture']!=fixture:raise RuntimeError('Retained repaint fixture differs')
        output,events=retained['output'],retained['events']
    else:
        capture_native_pools(ctx);audit=EngineAuditState(ctx)
        audit.tables[0x85A90]=(ctx.addr(0x85A90,0),0x78,101);audit.wrappers.append(('registered_counties',ctx.raw+0xB0))
        events=[];out=[];hooks=[]
        def phase():return dict(worldstate=o.read(audit.ws,len(bytes.fromhex(ctx.snap['worldstate']))).hex(),forces=[o.read(ctx.addr(FORCES,i),0x268).hex() for i in range(52)],tile=o.read(ctx.addr(TILES,tile),0x20).hex(),county_bits=o.read(ctx.g.exe+0x2141AE0,0x2000).hex())
        def effect(mu,address,size,user):events.append(dict(kind='effect',mode=mu.reg_read(UC_X86_REG_RDX)&0xffffffff,phase=phase()))
        def refresh(mu,address,size,user):events.append(dict(kind='refresh',phase=phase()))
        def mark(mu,access,address,size,value,user):
            if address==manager+0x30B20:events.append(dict(kind='mark',value=value))
        try:
            for r,fn in ((0x37D660,effect),(0x1A3D40,refresh)):hooks.append(o.mu.hook_add(UC_HOOK_CODE,fn,begin=ctx.g.exe+r,end=ctx.g.exe+r))
            hooks.append(o.mu.hook_add(UC_HOOK_MEM_WRITE,mark))
            def run():
                before=audit.read()
                def after():out.append(dict(after=audit.delta(before,audit.read()),manager_mark=o.read(manager+0x30B20,4).hex(),county_bits=o.read(ctx.g.exe+0x2141AE0,0x2000).hex(),profile=o.read(ctx.g.exe+0x2784AB0,0x278).hex(),map_mark=o.read(map_host+0xA0,4).hex() if map_host else bytes(4).hex()));return []
                o.call(0x292C00,0,read_after=after);return out[0]
            output=ctx.run(probe['pokes'],run)
            if output is None:raise RuntimeError(str(ctx.failures[-1]))
        finally:
            for h in hooks:o.mu.hook_del(h)
            o.unstub()
    for key in ('after','manager_mark','county_bits'):
        if output[key]!=probe['output'][key]:raise RuntimeError('Repaint replay differs from retained collection '+key)
    snapshot=copy.deepcopy(snapshot);snapshot['coalition_refresh_available']=bool(o.q(ctx.g.exe+0x1B91DA8))
    ctx.save('force_coalition_repaint_world.json.gz',snapshot)
    ctx.save('force_coalition_repaint.json.gz',dict(world='force_coalition_repaint_world.json.gz',fixture=fixture,output=output,events=events,complete=True,gameplay_stubs=False,allocator_stubs=False,
        scope='Whole 292C00 param1=0 including one territory repaint and native effect/mark/refresh request phases.'))
    return '1 whole coalition repaint with host-boundary phase evidence'


def calendar(ctx,retained=()):
    o=ctx.o;buf=o.alloc(24);cases=[];requests=[];prior={c['fixture']['name']:c for c in retained}
    def add(name,data,hours):requests.append(dict(name=name,kind='add',data=data.hex(),hours=hours))
    current=bytes.fromhex(ctx.snap['worldstate'])[0x34:0x3C]
    for hours in (-2147483648,-8641,-720,-25,-24,-1,0,1,23,24,25,720,8640,2147483647):add(f'current_hours_{hours}',current,hours)
    for year,month,day,hour in ((188,12,30,23),(0,1,1,0),(-1,1,1,0),(32767,12,30,23),(-32768,1,1,0),(188,255,255,255),(188,128,128,128),(188,0,0,0)):
        add(f'date_{year}_{month}_{day}_{hour}',struct.pack('<hBBB',year,month,day,hour)+b'\xa5\xb6\xc7',24)
    for delta in (0,1,2,3,4,5,8):requests.append(dict(name=f'copy_overlap_{delta}',kind='copy',data=bytes(range(24)).hex(),source=0,target=delta))
    for f in requests:
        if f['name'] in prior:
            if prior[f['name']]['fixture']!=f:raise RuntimeError('Calendar retained fixture differs')
            cases.append(prior[f['name']]);continue
        b=bytes.fromhex(f['data']);o.poke(buf,b)
        if f['kind']=='add':o.call(0x31CBA0,buf,f['hours'],read_after=[(buf,len(b))])
        else:o.call(0x31ABE0,buf+f['target'],buf+f['source'],read_after=[(buf,len(b))])
        cases.append(dict(fixture=f,after=o.after[0].hex()));ctx.save('native_calendar_progress.json.gz',dict(cases=cases))
    ctx.save('native_calendar.json.gz',dict(cases=cases,complete=True,gameplay_stubs=False))
    return len(cases)


def generate(ctx,snapshot,probe,retained=()):
    if not ctx.frozen:raise RuntimeError('Coalition cleanup requires a coherent frozen input')
    o=ctx.o;capture_native_pools(ctx);audit=EngineAuditState(ctx)
    audit.tables[0x85A90]=(ctx.addr(0x85A90,0),0x78,101);audit.wrappers.append(('registered_counties',ctx.raw+0xB0))
    baseline=audit.read();slot=ctx.g.exe+0x1B91DA8;ui=o.q(slot);days=struct.unpack('<i',o.read(ctx.g.exe+0x1A48998,4))[0]
    snapshot=copy.deepcopy(snapshot);snapshot['coalition_refresh_available']=bool(ui)
    requests=[]
    def case(name,pokes=(),**kw):
        f=dict(name=name,pokes=[['exe','challenge_active',0,'00000000']]+list(pokes),days=days,ui_available=bool(ui));f.update(kw);requests.append(f)
    case('original',pokes=probe['pokes'])
    requests[0]['pokes']=probe['pokes']
    force_indices=[n[0][1] for n in ctx.snap['lists']['0xc0']['items']]
    case('clear_bit4_only',[[hex(FORCES),i,0x12,bytes([(ctx.rec[FORCES][i][0x12]|4)&~2]).hex()] for i in force_indices])
    case('world_markers', [['worldstate',0,0x1656,'012d'],['worldstate',0,0x165C,'01']])
    for value in (0,1,-1,360):case(f'cooldown_days_{value}',days=value)
    case('refresh_absent',ui_available=False)
    case('empty_force_list',[['listdata','0xc0',[], '']])
    def unit_force(i):
        person_id=ctx.u16(UNITS,i,0x12);p=ctx.byid[person_id] if person_id<=6000 else 0
        if p is None or p<0 or p==0:return 0
        b=ctx.rec[OFFICERS][p];person_flags=struct.unpack_from('<H',b,0x15E)[0]
        if person_flags&0x4000:
            if b[0x1C5]==0:return 0
        elif not (5001<=struct.unpack_from('<H',b,0x10)[0]<=5100 or b[0xE6]):return 0
        c=b[0xE0] if b[0xE0]<=51 else 0;d=ctx.rec[CORPS][c]
        return d[0x10] if c and d[0x10] and d[0x11] and struct.unpack_from('<H',d,0x12)[0] and d[0x10]<=51 else 0
    units=[n[0][1] for n in ctx.snap['lists']['0x60']['items']];by_force={i:unit_force(i) for i in units}
    own_tiles={ctx.u16(UNITS,i,0x2A) if ctx.u16(UNITS,i,0x2A)<=48399 else 0:f for i,f in by_force.items() if f}
    all_bits=[[hex(FORCES),i,0x12,bytes([ctx.rec[FORCES][i][0x12]|6]).hex()] for i in force_indices]
    case('coalition_units_on_owned_tiles',all_bits+[[hex(TILES),at,0x14,bytes([f]).hex()] for at,f in own_tiles.items()])
    cases=[];prior={c['fixture']['name']:c for c in retained};phase=[];hooks=[]
    if 'original' not in prior:prior['original']=dict(fixture=requests[0],after=probe['output']['after'],refresh_phase=None)
    def refresh(mu,address,size,user):phase.append(dict(worldstate=o.read(audit.ws,len(bytes.fromhex(ctx.snap['worldstate']))).hex(),forces=[o.read(ctx.addr(FORCES,i),0x268).hex() for i in range(52)]))
    try:
        hooks.append(o.mu.hook_add(UC_HOOK_CODE,refresh,begin=ctx.g.exe+0x1A3D40,end=ctx.g.exe+0x1A3D40))
        for f in requests:
            if f['name'] in prior:
                if prior[f['name']]['fixture']!=f:raise RuntimeError('Coalition retained fixture differs')
                cases.append(prior[f['name']]);continue
            saved=[(ctx.g.exe+0x1A48998,o.read(ctx.g.exe+0x1A48998,4)),(slot,o.read(slot,8))];phase.clear()
            try:
                o.poke(ctx.g.exe+0x1A48998,struct.pack('<i',f['days']));o.poke(slot,struct.pack('<Q',ui if f['ui_available'] else 0))
                def run():
                    before=audit.read();out=[]
                    def after():out.append(audit.delta(before,audit.read()));return []
                    o.call(0x292C00,0,read_after=after)
                    if set(out[0])-{'records','worldstate'}:raise RuntimeError('Unexpected coalition engine mutation '+str(set(out[0])))
                    return dict(fixture=f,after=out[0],refresh_phase=list(phase))
                c=ctx.run(f['pokes'],run)
                if c is None:raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
                cases.append(c);ctx.save('force_coalition_progress.json.gz',dict(cases=cases));print('  coalition',f['name'],'complete',flush=True)
            finally:
                for at,b in reversed(saved):o.poke(at,b)
        if audit.read()!=baseline:raise RuntimeError('Coalition output did not roll back')
        ctx.save('force_coalition_world.json.gz',snapshot)
        ctx.save('force_coalition.json.gz',dict(world='force_coalition_world.json.gz',cases=cases,complete=True,gameplay_stubs=False,allocator_stubs=False,scope='Whole 292C00 with param1=0 and no territory repaint required.'))
        return len(cases)
    finally:
        for hook in hooks:o.mu.hook_del(hook)
        o.unstub()

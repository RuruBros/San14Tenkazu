# Native tile/profile rules with recorded CRT tm input and explicit Steam request boundaries.
import copy,struct
from context import FORCES,TILES
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from oracle import OracleError
from unicorn import UC_HOOK_CODE,UC_HOOK_MEM_WRITE
from unicorn.x86_const import UC_X86_REG_RCX,UC_X86_REG_RDX

COVER=[0x2412D0,0x234440,0x332D60,0x3E68B0,0x3EF4D0,0x3331B0]
SCENERY,COUNTIES=0x85A90,0x6CAF0


def generate(ctx,retained=(),kinds=None,basename='tile_profile'):
    if not ctx.frozen:raise RuntimeError('Tile/profile collection requires a coherent frozen input')
    o=ctx.o;capture_native_pools(ctx);audit=EngineAuditState(ctx)
    first=ctx.addr(SCENERY,0);size=0x78;count=101;data=o.read(first,size*count)
    if any(ctx.addr(SCENERY,i)!=first+i*size for i in range(count)):raise RuntimeError('Scenery is not contiguous')
    audit.tables[SCENERY]=(first,size,count)
    audit.snapshot['tables'][hex(SCENERY)]=dict(name='scenery',size=size,address=hex(first),extra={},records=[data[i*size:(i+1)*size].hex() for i in range(count)])
    audit.wrappers.append(('registered_counties',ctx.raw+0xB0))
    baseline=audit.read();snapshot=copy.deepcopy(audit.snapshot);snapshot['nested']=baseline['nested']
    for key in snapshot['tables']:snapshot['tables'][key]['address']=hex(ctx.addr(int(key,16),0))
    refs={ctx.addr(COUNTIES,i):i for i in range(501)};county_indices=[refs[int(p,16)] for p in baseline['nested']['registered_counties']]
    snapshot['lists']['0xb0']=dict(items=[[['0x6caf0',i]] for i in county_indices])
    profile=ctx.g.exe+0x2784AB0;initial_profile=o.read(profile,0x278);rules=list(struct.unpack('<9I',o.read(ctx.g.exe+0x1A28098,36)))
    notifier=o.q(ctx.g.exe+0x2784990);map_slot=ctx.g.exe+0x1B91DD0;map_host=o.q(map_slot)
    snapshot.update(profile=initial_profile.hex(),achievement_excluded_bits=rules,notification_available=bool(notifier),map_repaint_available=bool(map_host))
    player=next(i for i in range(1,52) if ctx.u16(FORCES,i,0x10))
    player_poke=[['worldstate',0,0x3A,bytes([player]).hex()]]
    prefix=[['worldstate',0,0x16A8,'00000000'],['exe','challenge_active',0,'00000000']]
    requests=[]
    def case(name,kind,pokes=(),**kwargs):
        f=dict(name=name,kind=kind,pokes=prefix+list(pokes),rules=rules,map_available=bool(map_host),disable_steam=True,profile=initial_profile.hex())
        f.update(kwargs);requests.append(f)
    case('original_quiet_bit16','bit',index=16,enabled=1,silent=1)
    for flag in [0]+[1<<bit for bit in range(32)]:case(f'eligibility_flags_{flag:x}','eligible',[['worldstate',0,0x16A8,struct.pack('<I',flag).hex()]])
    for challenge in (1,-1):case(f'eligibility_challenge_{challenge}','eligible',[['exe','challenge_active',0,struct.pack('<i',challenge).hex()]])
    case('eligibility_rules_out_of_range','eligible',[['worldstate',0,0x16A8,'04000000']],rules=[40]*9)
    for index in (-1,0,16,31,32,56,57):
        b=bytearray(initial_profile)
        if 0<=index<57:struct.pack_into('<I',b,0x34+(index>>5)*4,struct.unpack_from('<I',b,0x34+(index>>5)*4)[0]&~(1<<(index&31)))
        case(f'new_bit_{index}','bit',index=index,enabled=1,silent=1,profile=b.hex())
    for enabled in (0,1,-2):
        b=bytearray(initial_profile);struct.pack_into('<I',b,0x34,struct.unpack_from('<I',b,0x34)[0]|0x10000)
        case(f'existing_bit_16_enabled_{enabled}','bit',index=16,enabled=enabled,silent=1,profile=b.hex())
    case('notify_before_first_date','bit',index=16,enabled=1,silent=0,prefix_only=True,disable_steam=False)
    def ownership(n):
        seats=list(dict.fromkeys(ctx.u16(COUNTIES,i,0x3A) for i in county_indices))
        return player_poke+[['0xe270',at if at<=48399 else 0,0x14,bytes([player if i<n else 0]).hex()] for i,at in enumerate(seats)]
    all_bits=bytearray(initial_profile);struct.pack_into('<Q',all_bits,0x34,((1<<38)-1)&~1&~0x10000)
    for n in (199,200,201):case(f'territory_{n}','territory',ownership(n),require=1,all=1,silent=1)
    case('territory_all_bits','territory',ownership(200),require=1,all=1,silent=1,profile=all_bits.hex())
    case('territory_no_all_check','territory',ownership(200),require=1,all=0,silent=1)
    case('territory_bypass_eligibility','territory',ownership(200)+[['worldstate',0,0x16A8,'00010000']],require=0,all=1,silent=1)
    case('territory_invalid_player','territory',[['worldstate',0,0x3A,'ff']],require=0,all=1,silent=1)
    case('territory_admin_owner_irrelevant','territory',ownership(200)+[['0x6caf0',i,0x34,'00'] for i in county_indices],require=1,all=1,silent=1)
    tile=next(i for i,b in enumerate(ctx.rec[TILES]) if i and b[0x1E]==0)
    for owner in (-2147483648,-1,0,1,45,51,52,2147483647):case(f'tile_clamp_{owner}','tile',[['0xe270',tile,0x10,'00']],tile=tile,owner=owner)
    visible=next(i for i in range(1,101) if struct.unpack_from('<H',data,i*size+0x5C)[0]<=48399 and ctx.u16(TILES,struct.unpack_from('<H',data,i*size+0x5C)[0],0x12)!=0)
    scene_pokes=[['0xe270',tile,0x1E,bytes([visible]).hex()],['0x85a90',visible,0x5F,bytes([data[visible*size+0x5F]|1]).hex()]]
    case('tile_visible_scenery','tile',scene_pokes,tile=tile,owner=0)
    case('tile_map_absent','tile',scene_pokes,tile=tile,owner=0,map_available=False)
    case('tile_scenery_invalid_seat','tile',scene_pokes+[['0x85a90',visible,0x5C,'ffff']],tile=tile,owner=0)
    case('tile_scenery_byte_out_of_range','tile',[['0xe270',tile,0x1E,'ff']],tile=tile,owner=0)
    case('tile_player_territory_200','tile',ownership(200)+[['0xe270',tile,0x10,'0f']],tile=tile,owner=player)
    case('tile_player_all_bits','tile',ownership(200)+[['0xe270',tile,0x10,'12']],tile=tile,owner=player,profile=all_bits.hex())
    if kinds is not None:requests=[f for f in requests if f['kind'] in kinds]
    cases=[];prior={c['fixture']['name']:c for c in retained};active=[None];clock=[];notifications=[];repaints=[];captured=[];hooks=[]
    def inspect():
        captured.append(dict(state=audit.read(),profile=o.read(profile,0x278).hex(),map_mark=0 if not map_host else struct.unpack('<I',o.read(map_host+0xA0,4))[0]));return []
    def date(mu,address,size,data):clock.append(o.read(mu.reg_read(UC_X86_REG_RCX),36).hex())
    def notify(mu,address,size,data):
        notifications.append(dict(index=mu.reg_read(UC_X86_REG_RDX)&0xffffffff,profile=o.read(profile,0x278).hex()))
        if active[0].get('prefix_only'):inspect();mu.emu_stop()
    def repaint(mu,access,address,size,value,data):
        if map_host and address==map_host+0xA0:repaints.append(value)
    try:
        for r,fn in ((0x401BD0,date),(0x3EDDC0,notify)):hooks.append(o.mu.hook_add(UC_HOOK_CODE,fn,begin=ctx.g.exe+r,end=ctx.g.exe+r))
        hooks.append(o.mu.hook_add(UC_HOOK_MEM_WRITE,repaint))
        for f in requests:
            if f['name'] in prior:
                c=prior[f['name']]
                if c['fixture']!=f:raise RuntimeError('Retained fixture differs')
                cases.append(c);continue
            active[0]=f;clock.clear();notifications.clear();repaints.clear();captured.clear()
            extras=[(profile,o.read(profile,0x278)),(ctx.g.exe+0x1A28098,o.read(ctx.g.exe+0x1A28098,36)),(map_slot,o.read(map_slot,8))]
            if map_host:extras.append((map_host+0xA0,o.read(map_host+0xA0,4)))
            if notifier:extras.append((notifier+0x14,o.read(notifier+0x14,1)))
            try:
                o.poke(profile,bytes.fromhex(f['profile']));o.poke(ctx.g.exe+0x1A28098,struct.pack('<9I',*f['rules']))
                if map_host:o.poke(map_host+0xA0,bytes(4))
                if not f['map_available']:o.poke(map_slot,bytes(8))
                if notifier and f['disable_steam']:o.poke(notifier+0x14,b'\0')
                def run():
                    before=audit.read();complete=True;returned=None
                    r,args={'eligible':(0x332D60,()),'bit':(0x3EF4D0,(profile,f.get('index',0),f.get('enabled',0),f.get('silent',1))),
                        'territory':(0x3E68B0,(profile,f.get('require',1),f.get('all',1),f.get('silent',1))),
                        'tile':(0x2412D0,(ctx.addr(TILES,f.get('tile',tile)),f.get('owner',0)))}[f['kind']]
                    try:returned=o.call(r,*args,read_after=inspect)&0xffffffff
                    except OracleError as e:
                        if not f.get('prefix_only') or not str(e).startswith('stopped at 0x3eddc0'):raise
                        complete=False
                    if len(captured)!=1:raise RuntimeError('Missing native output boundary')
                    delta=audit.delta(before,captured[0]['state'])
                    if set(delta)-{'records'}:raise RuntimeError('Unexpected engine state mutation '+str(set(delta)))
                    return dict(fixture=f,after=delta,profile=captured[0]['profile'],map_mark=captured[0]['map_mark'],
                        clock_tm=list(clock),notifications=list(notifications),repaints=list(repaints),complete=complete,result=returned)
                c=ctx.run(f['pokes'],run)
                if c is None:raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
                cases.append(c);ctx.save(basename+'_progress.json.gz',dict(cases=cases));print('  tile/profile',f['name'],'complete',c['complete'],'clock',len(c['clock_tm']),flush=True)
            finally:
                for a,b in reversed(extras):o.poke(a,b)
        if audit.read()!=baseline or o.read(profile,0x278)!=initial_profile:raise RuntimeError('Tile/profile collection did not roll back')
        ctx.save(basename+'_world.json.gz',snapshot)
        ctx.save(basename+'.json.gz',dict(world=basename+'_world.json.gz',cases=cases,gameplay_stubs=False,allocator_stubs=False,
            scope='Complete tile/profile bodies with Steam backend disabled, plus an explicit notification prefix; clock executes natively.'))
        return f'{sum(c["complete"] for c in cases)} complete cases, {sum(not c["complete"] for c in cases)} explicit prefixes'
    finally:
        for hook in hooks:o.mu.hook_del(hook)
        o.unstub()

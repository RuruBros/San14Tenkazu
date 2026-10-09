# Whole tile transfer on frozen pages; presentation is disabled explicitly in the fixtures.
import copy,struct
from context import TILES,TERRAINS,FORCES,OFFICERS,CORPS
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from unicorn import UC_HOOK_CODE

COVER=[0x185B30,0x2C6700,0x2409A0,0x2412D0,0x333110]


def generate(ctx,probe,retained=()):
    if not ctx.frozen:raise RuntimeError('Tile transfer requires a coherent frozen input')
    o=ctx.o;capture_native_pools(ctx);audit=EngineAuditState(ctx)
    first=ctx.addr(0x85A90,0);data=o.read(first,101*0x78)
    audit.tables[0x85A90]=(first,0x78,101)
    audit.snapshot['tables']['0x85a90']=dict(name='scenery',size=0x78,extra={},records=[data[i*0x78:(i+1)*0x78].hex() for i in range(101)])
    audit.wrappers.append(('registered_counties',ctx.raw+0xB0));baseline=audit.read()
    snap=copy.deepcopy(audit.snapshot);snap['nested']=baseline['nested']
    for k in snap['tables']:snap['tables'][k]['address']=hex(ctx.addr(int(k,16),0))
    refs={ctx.addr(0x6CAF0,i):['0x6caf0',i] for i in range(501)}
    snap['lists']['0xb0']=dict(items=[[refs[int(p,16)]] for p in baseline['nested']['registered_counties']])
    bits=o.read(ctx.g.exe+0x2141AE0,0x2000);manager=o.q(ctx.g.exe+0x1BB3D00)
    mark=o.read(manager+0x30B20,4);profile=ctx.g.exe+0x2784AB0;profile_before=o.read(profile,0x278)
    map_host=o.q(ctx.g.exe+0x1B91DD0);map_mark=o.read(map_host+0xA0,4) if map_host else bytes(4)
    snap.update(county_bits=bits.hex(),manager_mark=mark.hex(),profile=profile_before.hex(),
        achievement_excluded_bits=list(struct.unpack('<9I',o.read(ctx.g.exe+0x1A28098,36))),notification_available=bool(o.q(ctx.g.exe+0x2784990)),map_repaint_available=bool(map_host),map_mark=map_mark.hex())
    tile=probe['tile'];owner=ctx.rec[TILES][tile][0x14];county=ctx.u16(TILES,tile,0x12)
    requests=[]
    def case(name,pokes=(),**kw):
        f=dict(name=name,tile=tile,destination=0,city=0,visuals=0,announce=0,override=0,clear_dirty=True,counter=0,
            pokes=[['worldstate',0,0x16A8,'00010000'],['exe','challenge_active',0,'00000000']]+list(pokes))
        f.update(kw);requests.append(f)
    case('original',pokes=probe['pokes'],clear_dirty=False)
    case('same_owner',destination=owner)
    case('normal_owner',destination=1)
    terrain=ctx.rec[TILES][tile][0x10]
    case('terrain_rejected',[[hex(TERRAINS),terrain,0x2A,'0000']])
    case('override_without_building',[[hex(TERRAINS),terrain,0x2A,'0000']],override=1)
    for kind in (1,2,3,4):
        definition=next(i for i,b in enumerate(ctx.rec[0x73958]) if i and b[0x2C]==kind)
        p=[[hex(TILES),tile,0x1A,struct.pack('<H',100).hex()],['0x6da98',100,0x10,struct.pack('<H',tile).hex()],['0x6da98',100,0x12,bytes([definition]).hex()]]
        case(f'building_class_{kind}',p)
        if kind==3:case('fortress_override',p,override=1)
    for match in (True,False):
        case(f'gate_seat_match_{match}',[[hex(TILES),tile,0x1A,'3400'],['0x6da98',52,0x10,struct.pack('<H',tile if match else tile+1).hex()]])
    case('hostile_gate',[[hex(TILES),tile,0x1A,'3400'],['0x6da98',52,0x10,struct.pack('<H',tile).hex()],['worldstate',0,0x165C,'00'],[hex(FORCES),1,0x194,'00'],[hex(FORCES),owner,0x194,'00'],[hex(FORCES),1,0xEA+owner,'00']],destination=1)
    for ground in (15,16,17,18):case(f'county_terrain_{ground}',[[hex(TILES),tile,0x10,bytes([ground]).hex()]],destination=1,city=1)
    free=next(i for i,b in enumerate(ctx.rec[OFFICERS]) if i and struct.unpack_from('<H',b,0x10)[0] and b[0xE6]==5)
    free_seed=[[hex(OFFICERS),free,0xE4,struct.pack('<H',county+561).hex()],[hex(OFFICERS),free,0xE2,'6300']]
    case('free_officer_new_city',[[hex(TILES),tile,0x10,'0f']]+free_seed,destination=1,city=1)
    district=ctx.rec[0xDD38][1][0x30]
    case('parent_already_owned',[[hex(TILES),tile,0x10,'0f'],['0x6caf0',county,0x35,'01'],[hex(CORPS),district,0x10,'01'],[hex(CORPS),district,0x11,'01']]+free_seed,destination=1,city=2)
    for destination in (46,47,48,49,50,51):case(f'special_owner_{destination}',destination=destination)
    case('tribe_retains_suzerain',[[hex(FORCES),46,0x194,bytes([owner]).hex()]],destination=46)
    case('invalid_tribe_does_not_retain',[[hex(FORCES),46,0x194,bytes([owner]).hex()],[hex(FORCES),46,0x10,'0000']],destination=46)
    case('tribe_owns_parent', [['0x6caf0',county,0x35,'01'],[hex(CORPS),district,0x10,'2e'],[hex(CORPS),district,0x11,'01']],destination=46)
    case('special_county_city',[[hex(TILES),tile,0x10,'0f']]+free_seed,destination=51)
    visible=next(i for i in range(1,101) if struct.unpack_from('<H',data,i*0x78+0x5C)[0]<=48399 and ctx.u16(TILES,struct.unpack_from('<H',data,i*0x78+0x5C)[0],0x12))
    case('visible_scenery',[[hex(TILES),tile,0x1E,bytes([visible]).hex()],['0x85a90',visible,0x5F,bytes([data[visible*0x78+0x5F]|1]).hex()]])
    case('destination_above_limit',destination=99)
    for county_id in (0,31,32,500,65535):case(f'county_dirty_bit_{county_id}',[[hex(TILES),tile,0x12,struct.pack('<H',county_id).hex()]])
    for value in (0,2147483647):case(f'challenge_counter_{value}',[['exe','challenge_active',0,'01000000']],counter=value)
    prior={c['fixture']['name']:c for c in retained};cases=[];hooks=[];counter_address=o.alloc(4);requests_seen=[]
    # This original result came from the guarded collector; keep its exact caller input.
    original=requests[0];original['pokes']=probe['pokes']
    if 'original' not in prior:
        prior['original']=dict(fixture=original,after=probe['output']['after'],county_bits=probe['output']['county_bits'],manager_mark=probe['output']['manager_mark'],
            profile=profile_before.hex(),map_mark=map_mark.hex(),counter=0,result=probe['result'],presentation=[])
    def presentation(mu,address,size,user):
        requests_seen.append(hex(address-ctx.g.exe))
        raise RuntimeError('Presentation unexpectedly reached '+hex(address-ctx.g.exe))
    try:
        for rva in (0x1B4990,0x42DB60,0x37D660):hooks.append(o.mu.hook_add(UC_HOOK_CODE,presentation,begin=ctx.g.exe+rva,end=ctx.g.exe+rva))
        for f in requests:
            if f['name'] in prior:
                if prior[f['name']]['fixture']!=f:raise RuntimeError('Retained tile fixture differs')
                cases.append(prior[f['name']]);continue
            saved=[(ctx.g.exe+0x2141AE0,o.read(ctx.g.exe+0x2141AE0,0x2000)),(manager+0x30B20,o.read(manager+0x30B20,4)),(ctx.g.exe+0x27D2A30,o.read(ctx.g.exe+0x27D2A30,8))]
            requests_seen.clear()
            try:
                o.poke(counter_address,struct.pack('<i',f['counter']));o.poke(ctx.g.exe+0x27D2A30,struct.pack('<Q',counter_address))
                def run():
                    dirty=bytearray(bits);at=struct.unpack('<H',o.read(ctx.addr(TILES,f['tile'])+0x12,2))[0]
                    if f['clear_dirty']:dirty[at>>3]&=~(1<<(at&7));o.poke(manager+0x30B20,bytes(4))
                    o.poke(ctx.g.exe+0x2141AE0,bytes(dirty));before=audit.read();captured=[]
                    def after():
                        captured.append(dict(state=audit.read(),county_bits=o.read(ctx.g.exe+0x2141AE0,0x2000).hex(),manager_mark=o.read(manager+0x30B20,4).hex(),profile=o.read(profile,0x278).hex(),map_mark=o.read(map_host+0xA0,4).hex() if map_host else bytes(4).hex(),counter=struct.unpack('<i',o.read(counter_address,4))[0]));return []
                    result=o.call(0x185B30,manager,ctx.addr(TILES,f['tile']),f['destination'],f['city'],stack_args=[f['visuals'],f['announce'],f['override']],read_after=after)&0xffffffff
                    output=captured[0];delta=audit.delta(before,output.pop('state'))
                    if set(delta)-{'records'}:raise RuntimeError('Unexpected tile engine changes '+str(set(delta)))
                    return dict(fixture=f,after=delta,**output,result=result,presentation=list(requests_seen))
                c=ctx.run(f['pokes'],run)
                if c is None:raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
                cases.append(c);ctx.save('tile_transfer_progress.json.gz',dict(cases=cases));print('  tile transfer',f['name'],c['result'],flush=True)
            finally:
                for address,b in reversed(saved):o.poke(address,b)
        if audit.read()!=baseline or o.read(profile,0x278)!=profile_before:raise RuntimeError('Tile transfer did not roll back')
        ctx.save('tile_transfer_world.json.gz',snap)
        ctx.save('tile_transfer.json.gz',dict(world='tile_transfer_world.json.gz',cases=cases,complete=True,gameplay_stubs=False,allocator_stubs=False,
            scope='Whole 185B30 with presentation disabled, no matching unit notifications, explicit challenge counter storage.'))
        return f'{len(cases)} whole tile transfers'
    finally:
        for hook in hooks:o.mu.hook_del(hook)
        o.unstub()

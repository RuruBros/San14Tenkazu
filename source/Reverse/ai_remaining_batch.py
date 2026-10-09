# One read-only live capture, then frozen-only remaining-state classifiers.
# python ai_remaining_batch.py capture --snapshot <new.pkl> --output <capture.json>
# python ai_remaining_batch.py inventory --snapshot <new.pkl> --output <inventory.json>
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import pickle
from pathlib import Path
import struct
import time

import ai
from ai_executor_audit import Harness
from ai_facility_special import read_clock
from ai_frozen_preflight import FrozenImage, relocated_text, validate_pool
from image import Image
from oracle import Oracle, OracleError


class CapturePages:
    # No emulator is needed to copy bytes. Missing pages remain unreadable.
    def __init__(self, game):
        self.g=game;self.pages={}

    def prefetch(self, address, size):
        for page in range(address&-4096,address+size,4096):
            if page not in self.pages:
                data=self.g.read(page,4096)
                if data is not None:self.pages[page]=data

    def read(self, address, size):
        if size==0:return b''
        self.prefetch(address,size)
        chunks=[]
        for page in range(address&-4096,address+size,4096):
            if page not in self.pages:raise OracleError(f'unreadable {page:#x}')
            lo=max(address,page)-page;hi=min(address+size,page+4096)-page
            chunks.append(self.pages[page][lo:hi])
        return b''.join(chunks)

    def q(self, address):return struct.unpack('<Q',self.read(address,8))[0]


def capture(path):
    # This is the sole live entry; Game is opened by ListPoolSnapshot with read-only rights.
    from ai_native_snapshot import ListPoolSnapshot
    from ai_path_global_branch import capture_global
    import world
    if path.exists():raise ValueError('capture destination already exists; use a fresh filename')
    print('capture: validate original list pools',flush=True)
    game=ListPoolSnapshot(integer_pool=True);before=read_clock(game);time.sleep(3)
    if before!=read_clock(game):raise ValueError('game changed before capture')
    im=Image();begin,text,relocations=relocated_text(im,game.exe)
    actual=game.read(game.exe+begin,len(text))
    if actual!=text:raise ValueError('current original text differs from saved image')
    global_capture=capture_global(game)
    print('capture: copy original image without emulator mapping',flush=True)
    o=CapturePages(game);o.prefetch(game.exe,game.exe_size)
    for start,data in game._pool_ranges:o.prefetch(start,len(data))
    raw=game.base-0x290;o.prefetch(raw,0x88000)
    manager_root=game.q(game.exe+ai.AI_MANAGER)
    if not manager_root:raise ValueError('AI manager missing')
    manager=game.q(manager_root+8);context=game.q(manager+0x38);board=game.q(context+0x18)
    for address,size in ((manager_root,0x40),(manager,0x70),(context,0x80),(board,0x100)):
        if not address:raise ValueError('AI context chain missing')
        o.prefetch(address,size)
    print('capture: copy domain records and linked vectors',flush=True)
    snapshot=world.snapshot(o)
    # Every original record and linked vector read by world.snapshot is now in the copy.
    # No classifier, path search or fire-writing function runs during capture.
    after=read_clock(game)
    if before!=after:raise ValueError('game changed during capture')
    pool=validate_pool(o.read,game.exe+0x27D10E0)
    if not pool['valid']:raise ValueError('copied generic pool is inconsistent')
    # Resolve import addresses by reading PE metadata; do not construct/call Oracle.
    imports=Oracle.imports(o);teb=game.main_teb();o.prefetch(teb,4096)
    # Unicorn must map the original import-entry bytes before its standard import
    # observer runs (even for EnterCriticalSection/memset). Do not fabricate code.
    for address in imports.values():o.prefetch(address,1)
    print('capture: copy original TLS and initialized allocator arena',flush=True)
    tls_index=struct.unpack('<I',o.read(game.exe+0x27F1320,4))[0]
    if tls_index>4095:raise ValueError('unexpected executable TLS index')
    tls_array=o.q(teb+0x58);tls_block=o.q(tls_array+tls_index*8)
    if not tls_array or not tls_block:raise ValueError('original executable TLS block missing')
    o.prefetch(tls_block,0x1000)
    arena=o.q(game.exe+0x27D1070)
    if not arena:raise ValueError('original allocator arena is not initialized')
    # Original 3FB925/3FB94E allocate and initialize exactly B800000 bytes.
    # Read existing storage only; never create replacement allocator/TLS data.
    arena_size=0xB800000;o.prefetch(arena,arena_size)
    allocator=dict(pointer=arena,size=arena_size,tlsIndex=tls_index,tlsArray=tls_array,tlsBlock=tls_block,
                   tlsEpoch=struct.unpack('<i',o.read(tls_block+0x10,4))[0],
                   managerEpoch=struct.unpack('<i',o.read(game.exe+0x27D10A0,4))[0])
    first_tile=o.q(raw+0xE270)
    tile_pointers=struct.unpack('<48400Q',o.read(raw+0xE270,48400*8))
    if any(pointer!=first_tile+i*32 for i,pointer in enumerate(tile_pointers)):
        raise ValueError('tile records are not contiguous')
    tile_data=o.read(first_tile,48400*32)
    if tile_data!=game.read(first_tile,len(tile_data)) or before!=read_clock(game):
        raise ValueError('tile records or game clock changed during capture')
    print('capture: save and independently validate frozen pages',flush=True)
    path.parent.mkdir(parents=True,exist_ok=True)
    with path.open('xb') as stream:
        pickle.dump(dict(exe=game.exe,exe_size=game.exe_size,base=game.base,teb=teb,imports=imports,pages=o.pages),stream)
    pages=len(o.pages)
    checked=FrozenImage(path,im,False)
    if not checked.pool_snapshot['valid']:raise ValueError('saved pool failed independent validation')
    tables={key:dict(name=value['name'],records=len(value['records'])) for key,value in snapshot['tables'].items()}
    return dict(utc=datetime.now(timezone.utc).isoformat(),clockBefore=before,clockAfter=after,clockStable=True,
                snapshot=dict(file=str(path),sha256=checked.snapshot_sha256,pages=pages),
                imageSha256=hashlib.sha256(im.img).hexdigest(),comparedTextBytes=len(text),textRelocations=relocations,
                globalContext=global_capture,pool=pool,tables=tables,gameAccess='PROCESS_QUERY_INFORMATION|PROCESS_VM_READ',
                allocator=allocator,
                tileBytesCompared=len(tile_data),tileBytesSha256=hashlib.sha256(tile_data).hexdigest(),
                nativeCalls=0,gameWrites=0,method='one live paused capture with full original image, validated pools, world tables/vectors and AI context; subsequent calls use FrozenImage only')


def inventory(path):
    im=Image();game=FrozenImage(path,im,False)
    if not game.pool_snapshot['valid']:raise ValueError('frozen pool validation failed')
    before=read_clock(game);h=Harness(game);o=h.o
    tiles=struct.unpack('<48400Q',game.read(h.raw+0xE270,48400*8));nulls=[];fires=[];terrain=Counter()
    for index,tile in enumerate(tiles):
        if not tile:nulls.append(index);continue
        prefix=o.read(tile,32);terrain[prefix[0x10]]+=1;timer=struct.unpack_from('<H',prefix,0x16)[0]
        if timer:fires.append(dict(hex=index,tile=tile,word16=timer))
    armies=[]
    for army in h.valid_table(0x7E838,501):
        armies.append(dict(pointer=army,owner=o.call32(0x22CC40,army),leader=struct.unpack('<H',o.read(army+0x12,2))[0],special=o.call32(0x232730,army)))
    sites=[];rows=[];points=o.alloc(4)
    for index,tile in enumerate(tiles):
        if not tile:continue
        building_id=struct.unpack('<H',o.read(tile+0x1A,2))[0]
        if not 1<=building_id<=3000:continue
        building=o.call(0x22FB10,tile)
        if not building or not o.call32(0x32F500,building) or o.call32(0x232650,building) or o.call32(0x232BD0,building):continue
        definition=o.call(0xEE90,building);ground=o.call(0x2318B0,tile)
        neighbor=next((n for d in range(1,7) if 0<=(n:=o.call32(0x2A7720,index,d))<48400 and tiles[n]),None)
        if neighbor is None:raise ValueError('linked facility has no actual neighbor')
        site=dict(hex=index,tile=tile,buildingId=building_id,building=building,category=o.read(definition+0x2C,1)[0],
                  terrainCode=o.call32(0x22E0D0,ground),neighbor=neighbor)
        sites.append(site)
        # Only categories2/3 can satisfy the currently missing facility conditions.
        if site['category'] not in (2,3):continue
        for army in [0,*[a['pointer'] for a in armies]]:
            owner=o.call32(0x22CC40,army) if army else 0
            for flag in (0,1):
                for goal in (index,neighbor):
                    o.poke(points,struct.pack('<i',goal))
                    value=o.call32(0x18A080,army,tile,owner,0,stack_args=(flag,points,1))
                    rows.append(dict(site=building_id,army=army,force=owner,contextFlag=flag,goal=goal,native=value))
    after=read_clock(game)
    return dict(utc=datetime.now(timezone.utc).isoformat(),clockBefore=before,clockAfter=after,clockStable=before==after,
                snapshot=dict(file=str(path),sha256=game.snapshot_sha256),gameAccess=False,tileCount=len(tiles),nullTiles=nulls,
                nonzeroWord16=fires,terrainCounts=dict(terrain),armies=armies,ownerCounts=dict(Counter(a['owner'] for a in armies)),
                force46To50=[a for a in armies if 46<=a['owner']<=50],leaderAbove6000=[a for a in armies if a['leader']>6000],
                specialMinorForce=[a for a in armies if 46<=a['owner']<=51 and a['special']],sites=sites,cases=rows,
                admittedCategory2Or3=[r for r in rows if r['native']],listPoolSnapshot=game.pool_snapshot,
                method='frozen-only actual state inventory and complete original18A080 on real category2/3 facilities; mask64=0; no helper substitution or field edits')


def main():
    parser=argparse.ArgumentParser();parser.add_argument('mode',choices=('capture','inventory'))
    parser.add_argument('--snapshot',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    try:
        result=capture(args.snapshot) if args.mode=='capture' else inventory(args.snapshot)
        result['analyzerSha256']=hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    except (Exception,SystemExit) as error:
        result=dict(completed=False,error=str(error),mode=args.mode,utc=datetime.now(timezone.utc).isoformat())
        if args.mode=='capture':result['nativeCalls']=0
        args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
        raise
    args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    if args.mode=='capture':print('captured',result['snapshot'],'pool valid',result['pool']['valid'],flush=True)
    else:print('actual inputs: fire',len(result['nonzeroWord16']),'minor armies',len(result['force46To50']),
               'leader>6000',len(result['leaderAbove6000']),'null tiles',len(result['nullTiles']),
               'facilities',len(result['sites']),'admitted category2/3',len(result['admittedCategory2Or3']),flush=True)


if __name__=='__main__':main()

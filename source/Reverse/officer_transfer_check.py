from pathlib import Path
import sys,json,struct,time,gzip
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'source'/'Reverse'))
from ai_native_snapshot import ListPoolSnapshot
from game import Game
from oracle import Oracle, OracleError
from image import Image
from ai_frozen_preflight import relocated_text

ROOT=Path('.cache/transfer-check')
input=json.loads((ROOT/'input.json').read_text())
class Captured(ListPoolSnapshot):
    def __init__(self):
        super().__init__();self.segments=[(r['address'],bytes.fromhex(r['hex'])) for r in input['reads']]
        if self.exe!=input['exe'] or self.base-0x290!=input['raw']:raise Exception('Game changed')
    def read(self,a,n):
        b=super().read(a,n)
        if b is None:return None
        b=bytearray(b)
        for p,v in getattr(self,'segments',[]):
            lo=max(a,p);hi=min(a+n,p+len(v))
            if lo<hi:b[lo-a:hi-a]=v[lo-p:hi-p]
        return bytes(b)

game=Captured();im=Image();begin,text,relocs=relocated_text(im,game.exe)
for rva in [0x233440,0x241C90,0x240CB0,0x2408E0,0x2EA1B0,0x2E8E70,0x29EDE0,0x301AB0,0x183A30,0x18A080,0x2D95F0]:
    bounds=im.func(rva)
    n=bounds[1]-bounds[0] if bounds else 32
    if game.read(game.exe+rva,n)!=text[rva-begin:rva-begin+n]:raise Exception(f'Code differs {rva:X}')
out=dict(gameWrites=False,helperReplacements=[],verifiedCode=True,routeCases=[],frameCases=[])
rows=[r for r in input['rows'] if 'body' in r]
print(json.dumps(dict(prepared=len(rows),refused=[(r['name'],r['home'],r['refused'])for r in input['rows'] if 'refused'in r][:8]),ensure_ascii=False),flush=True)

def unitfields(b):return dict(kind=struct.unpack_from('<H',b,0x10)[0],tile=struct.unpack_from('<H',b,0x2A)[0],order=b[0x37],target=struct.unpack_from('<H',b,0x3A)[0],next=struct.unpack_from('<H',b,0x48)[0],group=struct.unpack_from('<H',b,0x58)[0],flags=b[0x150])

# Both accepted/rejected transport routes and combat routes, keeping their actual terrain/owner inputs.
selected=[]
for kind in [2,1]:
    for accepted in [False,True]:
        cases=[r for r in input['routes']if r['kind']==kind and (r['count']>0)==accepted]
        selected.extend(cases[:3])
for r in ([] if '--frames' in sys.argv else selected):
    o=Oracle(game);o.insn_limit=800_000_000
    person=game.q(input['raw']+0x158+r['commander']*8)
    base=game.q(input['raw']+0xDD38+r['home']*8)
    corps=game.read(base+0x30,1)[0]
    o.emu_write(person+0xE0,bytes([corps]));o.emu_write(person+0xE2,struct.pack('<H',r['home']))
    started=time.monotonic()
    try:
        o.call(0x2EA1B0,r['address'],0,0,0,read_after=[(r['address'],0x200)])
        fields=unitfields(o.after[0]);actual=fields['next']<48400 or r['count']==1
        case=dict(slot=r['slot'],kind=r['kind'],home=r['home'],expected=r['count']>0,actual=actual,result=fields,seconds=time.monotonic()-started)
    except OracleError as ex:case=dict(slot=r['slot'],home=r['home'],error=str(ex))
    out['routeCases'].append(case);print(json.dumps(case),flush=True)

# Exact C# generated transaction bytes, containing real original native functions, no result substitutions.
chosen=[]
for predicate in [lambda r:any('군단장 해제'in e for e in r['evidence']) and bool(r['units']),
                  lambda r:any('군단장 해제'in e for e in r['evidence']) and not r['units'],
                  lambda r:bool(r['units']) and next(v for v in input['routes']if v['slot']==r['units'][0]['slot'])['kind']==2,
                  lambda r:bool(r['units']) and not any('군단장 해제'in e for e in r['evidence'])]:
    r=next((r for r in rows if predicate(r)),None)
    if r is not None and r not in chosen:chosen.append(r)
chosen.extend(r for r in rows if r.get('inputChanges') and r not in chosen)
if '--routes' in sys.argv:chosen=[]
for r in chosen:
    o=Oracle(game);o.insn_limit=800_000_000
    for patch in r.get('inputChanges',[]):o.emu_write(patch['address'],bytes.fromhex(patch['hex']))
    address=o.alloc(len(bytes.fromhex(r['body'])));o.emu_write(address,bytes.fromhex(r['body']))
    source=game.q(input['raw']+0xE0D0+r['oldCorps']*8)
    base=game.q(input['raw']+0xDD38+r['home']*8)
    targets=[(r['address'],0x1F0),(source,0x30),(base,0x168)]+[(u['address'],0x200) for u in r['units']]
    groups=[]
    for unit in r['units']:
        group=struct.unpack('<H',o.read(unit['address']+0x58,2))[0]
        if group:
            group_address=game.q(input['raw']+0x7F8D8+group*8);groups.append((group_address,0x40))
    targets+=groups
    started=time.monotonic()
    try:
        beforeBase=o.read(base,0x168)
        result=o.call(address-game.exe,read_after=targets,keep=True)
        p,c,b,*more=o.after;units=more[:len(r['units'])];groupData=more[len(r['units']):]
        pending=[o.call32(0x233440,u['address'],keep=True)for u in r['units']]
        case=dict(id=r['id'],home=r['home'],inputChanges=r.get('inputChanges',[]),result=result,corps=p[0xE0],base=struct.unpack_from('<H',p,0xE2)[0],place=struct.unpack_from('<H',p,0xE4)[0],status=p[0xE6],oldLeader=struct.unpack_from('<H',c,0x12)[0],oldForce=c[0x10],units=[unitfields(u)for u in units],pending=pending,baseTroopDelta=struct.unpack_from('<I',b,0x3C)[0]-struct.unpack_from('<I',beforeBase,0x3C)[0],seconds=time.monotonic()-started)
        case['groupCommanders']=[struct.unpack_from('<H',b,0x2C)[0]for b in groupData]
        case['movement']=[]
        for unit,removed in zip(r['units'],pending):
            if removed:continue
            if any(patch['address']==r['address']+0xE6 for patch in r.get('inputChanges',[])):continue
            before=o.read(unit['address'],0x200)
            o.call(0x2D95F0,unit['address'],keep=True,read_after=[(unit['address'],0x200)])
            after=o.after[0]
            case['movement'].append(dict(beforeProgress=struct.unpack_from('<H',before,0x14)[0],afterProgress=struct.unpack_from('<H',after,0x14)[0],beforeTile=struct.unpack_from('<H',before,0x2A)[0],afterTile=struct.unpack_from('<H',after,0x2A)[0]))
    except OracleError as ex:case=dict(id=r['id'],home=r['home'],error=str(ex))
    out['frameCases'].append(case);print(json.dumps(case),flush=True)
    # Deliberately stale guard: the first changed byte must reject before native writes.
    o=Oracle(game)
    for patch in r.get('inputChanges',[]):o.emu_write(patch['address'],bytes.fromhex(patch['hex']))
    address=o.alloc(len(bytes.fromhex(r['body'])));o.emu_write(address,bytes.fromhex(r['body']))
    g=r['guards'][0];value=bytearray(o.read(g['address'],len(bytes.fromhex(g['hex']))));value[0]^=1;o.emu_write(g['address'],bytes(value))
    before=[o.read(a,n)for a,n in targets];result=o.call(address-game.exe,read_after=targets)
    case=dict(id=r['id'],home=r['home'],stale=True,result=result,unchanged=before==o.after)
    out['frameCases'].append(case);print(json.dumps(case),flush=True)

capturedClock=next(bytes.fromhex(r['hex'])[0x34:0x39] for r in input['reads'] if r['address']==input['state'] and len(bytes.fromhex(r['hex']))>=0x39)
out['clockStable']=Game.read(game,input['state']+0x34,5)==capturedClock
out['utc']=__import__('datetime').datetime.now(__import__('datetime').timezone.utc).isoformat()
out['pid']=game.pid
out['unitRecordsStable']=all(Game.read(game,r['address'],0x200)==bytes.fromhex(r['unit'])for r in input['routes'][::3])
out['inputDate']=input['date']
name='route-replay.json' if '--routes'in sys.argv else 'frame-replay.json'
(ROOT/name).write_text(json.dumps(out,ensure_ascii=False,indent=2))
print(json.dumps(dict(output=str(ROOT/name),clockStable=out['clockStable'],unitRecordsStable=out['unitRecordsStable'])),flush=True)

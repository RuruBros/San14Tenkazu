# Remaining path input contracts from original saved code and one existing frozen image.
# No live Game, invented pages, helper replacement, tile edits or partial-block execution.
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct

from ai_executor_audit import Harness
from ai_facility_special import read_clock
from ai_frozen_preflight import FrozenImage
from ai_path_list_boundary import closure
from ai_static_facts import anchored
from image import Image
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_EAX


def code_contracts(im):
    constructors=anchored(im,0x3262E0,{0x326B5A:'mov r14d, 0xbd10',0x326B60:'test rax, rax',
        0x326B63:'je 0x326b91',0x326B68:'lea rsi, [rax + 8]',0x326B78:'lea r9, [rip - 0x10598f]',
        0x326B7F:'mov r8d, r14d',0x326B82:'mov edx, 0x20',0x326B8A:'call 0xff945c',
        0x326B91:'mov rsi, r15',0x326B94:'lea rdx, [rbx + 0xe270]',0x326BA3:'rep stosq qword ptr [rdi], rax',
        0x326BB0:'mov qword ptr [rdx], rsi',0x326BB3:'add rsi, 0x20',0x326BB7:'lea rdx, [rdx + 8]',
        0x326BBB:'sub r14, 1',0x326BBF:'jne 0x326bb0'})
    dispatcher=anchored(im,0xFF945C,{0xFF9478:'mov rsi, r9',0xFF947B:'mov r15, r8',0xFF947E:'mov r14, rdx',
        0xFF948D:'cmp rbx, r15',0xFF949A:'mov rcx, rdi',0xFF949D:'call rsi',0xFF949F:'add rdi, r14',0xFF94A7:'inc rbx'})
    lookups=anchored(im,0x183A30,{0x183F60:'cmp esi, 0xbd0f',0x183F66:'ja 0x183f79',
        0x183F6F:'mov rbx, qword ptr [rcx + rsi*8 + 0xe270]',0x183F80:'mov rbx, qword ptr [rax + 0xe270]',
        0x183F87:'test rbx, rbx',0x183F8A:'je 0x1842e3'})
    lookups+=anchored(im,0x184EB0,{0x1851E2:'cmp esi, 0xbd0f',0x1851E8:'ja 0x1851fb',
        0x1851F1:'mov rbx, qword ptr [rcx + rsi*8 + 0xe270]',0x185202:'mov rbx, qword ptr [rax + 0xe270]',
        0x185209:'test rbx, rbx',0x18520C:'je 0x18553c'})
    generic=anchored(im,0x2C6700,{0x2C6806:'test r14d, r14d',0x2C6809:'jne 0x2c682c',
        0x2C680E:'call 0xee90',0x2C6819:'call 0x32f500',0x2C6822:'cmp byte ptr [rbx + 0x2c], 4',
        0x2C6826:'je 0x2c682c',0x2C6828:'xor eax, eax',0x2C682C:'mov eax, 1'})
    fallback=anchored(im,0x18A080,{0x18A0E8:'call 0x2c6640',0x18A0EF:'je 0x18a14f',
        0x18A102:'call 0x2c6700',0x18A109:'je 0x18a14f',0x18A194:'call 0x22e0d0',
        0x18A199:'sub eax, 0x10',0x18A19C:'je 0x18a256',0x18A1A2:'cmp eax, 1',0x18A1A5:'jne 0x18a391',
        0x18A391:'xor eax, eax'})
    caller=anchored(im,0x183A30,{0x183E93:'mov r14d, 1',0x183EA0:'mov dword ptr [rsp + 0x30], 0',
        0x183EA8:'mov dword ptr [rsp + 0x28], r14d',0x183EAD:'mov dword ptr [rsp + 0x20], r14d',
        0x183EB2:'xor r9d, r9d',0x183EB5:'lea r8d, [r9 + 0x2c]',0x183EBF:'call 0x185b30'})
    return dict(tileTable=dict(count=48400,stride=32,offset=0xE270,constructor=0x2211F0,
                  anchors=constructors+dispatcher+lookups,unobservedDirections=[0x183F8A,0x18520C],
                  scope='successful original allocation and intact original pointer table; construction/load transitions or corrupt pointers are outside this contract'),
                facility=dict(anchors=generic+fallback,exceptionTerrainCodes=[16,17],
                  fact='With mask64=0, valid non-gate category2/3 definitions fail the generic admission. 18A080 can then return true only through terrain16/17 exception blocks; those blocks still have their own gates.'),
                globalCaller=dict(anchors=caller,registers={'r8':44,'r9':0},stack={0x28:1,0x30:1,0x38:0}))


def checked_tiles(game):
    raw=game.base-0x290;data=game.read(raw+0xE270,48400*8)
    if data is None:raise ValueError('frozen tile table missing')
    tiles=struct.unpack('<48400Q',data)
    if not tiles[0] or tiles[0]+48400*32>2**64 or any(pointer!=tiles[0]+i*32 for i,pointer in enumerate(tiles)):
        raise ValueError('frozen table does not match successfully constructed contiguous tile records')
    return dict(pointers=tiles,first=tiles[0],pointerBytesSha256=hashlib.sha256(data).hexdigest(),
                verified=True,count=48400,stride=32,nullCount=0)


def facility_trials(game,inventory):
    if inventory['snapshot']['sha256']!=game.snapshot_sha256 or inventory['clockBefore']!=read_clock(game):
        raise ValueError('inventory and frozen image identities differ')
    if not inventory['clockStable'] or inventory['clockBefore']!=inventory['clockAfter']:
        raise ValueError('inventory is unstable')
    selected={}
    for site in inventory['sites']:
        if site['category'] in (2,3):selected.setdefault((site['category'],site['terrainCode']),site)
    h=Harness(game);o=h.o;point=o.alloc(4);active=[None];observed=[]
    def observe(uc,address,size,user):
        row=active[0]
        if row is None:return
        pc=address-o.exe;value=uc.reg_read(UC_X86_REG_EAX)&0xFFFFFFFF
        if pc in (0x18A0ED,0x18A107):row['firstAdmission']=value
        elif pc==0x18A199:row['fallbackTerrain']=value
        elif pc==0x18A391:row['reachedFallbackReject']=True
    for pc in (0x18A0ED,0x18A107,0x18A199,0x18A391):
        o.mu.hook_add(UC_HOOK_CODE,observe,None,o.exe+pc,o.exe+pc)
    army=inventory['armies'][0]['pointer']
    if o.call32(0x32F500,army)!=1:raise ValueError('actual selected army is invalid in frozen image')
    for site in selected.values():
        for unit in (0,army):
            owner=o.call32(0x22CC40,unit) if unit else 0
            row=dict(site=site,army=unit,force=owner,mask64=0,contextFlag=0,goal=site['hex'],reachedFallbackReject=False)
            active[0]=row;o.poke(point,struct.pack('<i',site['hex']))
            row['native']=o.call32(0x18A080,unit,site['tile'],owner,0,stack_args=(0,point,1))
            observed.append(row);active[0]=None
    return dict(cases=observed,selectedCategoryTerrainPairs=len(selected),helperReplacements=[],fieldEdits=[],
                method='complete original18A080 on representative actual category/terrain pairs; original first-admission and fallback instructions observed; scratch arguments only')


def build(snapshot,inventory):
    im=Image();facts=code_contracts(im);game=FrozenImage(snapshot,im,False)
    if not game.pool_snapshot['valid']:raise ValueError('frozen list pool invalid')
    before=read_clock(game);tiles=checked_tiles(game);native=facility_trials(game,inventory)
    narrowed=closure(im,switches=True,known_iterators=True,scalar_context=facts['globalCaller'])
    return dict(utc=datetime.now(timezone.utc).isoformat(),clockBefore=before,clockAfter=read_clock(game),clockStable=before==read_clock(game),
                snapshot=dict(file=str(snapshot),sha256=game.snapshot_sha256),imageSha256=hashlib.sha256(im.img).hexdigest(),
                analyzerSha256={name:hashlib.sha256(Path(__file__).with_name(name).read_bytes()).hexdigest()
                    for name in ('ai_path_state_contracts.py','ai_scalar_flow.py','ai_path_list_boundary.py')},
                gameAccess=False,codeContracts=facts,tileTable=tiles,facilityAdmission=native,globalCallClosure=narrowed,
                nativeSearchCalls=0,loopTakenObserved=False,transitiveMutationClosureProved=False,
                limits=['native branch ledger is unchanged','null-tile exclusion requires the explicitly scoped successfully constructed intact table',
                        'facility exception eligibility is not complete admission or proof that such actual states exist',
                        'scalar specialization concerns the root185B30 only; other callee branches and indirect targets remain conservative'])


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--snapshot',type=Path,required=True)
    parser.add_argument('--inventory',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    result=build(args.snapshot,json.loads(args.inventory.read_bytes()))
    args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print('tile contract',result['tileTable']['verified'],'facility calls',len(result['facilityAdmission']['cases']),
          'root closure',result['globalCallClosure']['summary'],'native search calls',result['nativeSearchCalls'])


if __name__=='__main__':main()

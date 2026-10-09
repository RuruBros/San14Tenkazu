# Execute all 342 documented info cases against real objects in copied pages.
# python ai_info_audit.py --vectors ../Tests/AiInfoAuditVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct
import ai
import units
from ai_friend import HERE, vectors_text
from ai_native_trace import linked_list, Trace
from ai_native_snapshot import ListPoolSnapshot


def audit(game):
    from oracle import Oracle
    raw=game.base-0x290
    manager=game.q(game.q(game.exe+ai.AI_MANAGER)+8)
    context=game.q(manager+0x38);board=game.q(context+0x18)
    oracle=Oracle(game=game)
    def table(offset,count):
        return next(p for i in range(1,count) if (p:=game.q(raw+offset+8*i)) and oracle.call32(0x32F500,p)==1)
    force=table(0xDF30,52);district=oracle.call(0x22E7E0,force)
    objects={'Person':game.officer(736),'Force':force,'District':district,
             'Foothold':oracle.call(0x22D3A0,district),'Area':table(0x6CAF0,501),
             'Hex':table(0xE270,48400),'Scenery':table(0x85DB8,33),'Troops':table(0x7F8D8,600),
             'Army':table(units.UNITS,units.UNIT_COUNT),'Building':table(0x6DA98,3063),
             'Formation':table(units.FORMATIONS,21),'Advantage':table(0x85990,12),
             'CautionTroops':table(0x80BA8,501),'CautionArmy':table(0x81B50,501)}
    classes={v['name']:v for v in ai.classes().values()}
    rows=[r for line in Path(ai.INFO).read_text(encoding='utf-8').splitlines()
          if line and not line.startswith('#') and (r:=line.split('\t'))[1].isdigit()]
    answers=[];context_cases=[]
    source,target=0x61A17081,0x61A17082
    for name,kind,output_type,*_ in rows:
        kind=int(kind);myself=name.startswith('CGetMyself');game_data=name=='CVariableGetGameDataNode'
        family=name.replace('CVariableGet','').replace('CGetMyself','').replace('InfoNode','')
        oracle=Oracle(game=game);node=oracle.alloc(0x80);out=oracle.alloc(8)
        # The actual AI context initializer (0xC05F0/0xD3284) creates this native STL tracking list.
        # Without it, 0x1A2F0 deliberately cannot create output lists.
        heap=oracle.call(0x94A00,keep=True)
        if oracle.call32(0xB26BA0,board,ai.name_hash('HeapListInfo'),heap,keep=True)!=1:
            raise AssertionError('native HeapListInfo registration')
        agent=objects.get(family,0) if myself else district
        oracle.call32(0xB26990,board,ai.name_hash('AgentType'),{'Force':4,'District':3,'Troops':2}.get(family,3) if myself else 3,keep=True)
        if not myself and not game_data:oracle.call32(0xB26BA0,board,source,objects[family],keep=True)
        params=[kind,target] if myself or game_data else [source,kind,target]
        oracle.poke(node+0x38,struct.pack('<'+'I'*len(params),*params))
        trace=Trace(oracle,{0x6D7980:1,0x32F500:None,0xB6180:None}) if family=='Force' and kind==38 else None
        status=oracle.call32(classes[name]['exec'],node,context,agent,keep=True)
        row={'class':name,'kind':kind,'type':output_type,'status':status}
        if output_type=='int':
            ok=oracle.call32(0xB26300,board,target,out,0,read_after=[(out,4)])
            row['written']=bool(ok)
            if ok:row['native']=struct.unpack('<i',oracle.after[0])[0]
        elif output_type in ('object','list'):
            ok=oracle.call32(0xB26470,board,target,out,0,read_after=[(out,8)])
            row['written']=bool(ok)
            if ok:
                pointer=struct.unpack('<Q',oracle.after[0])[0];row['pointer']=pointer
                row['native']=linked_list(oracle,pointer) if output_type=='list' else pointer
        else:row['written']=False
        reference=None
        obj=objects.get(family,0)
        if family=='Force' and kind in [19,21,33,38]:
            offset={19:0x210,21:0x220,33:0x230,38:0x248}[kind]
            members=linked_list(oracle,obj+offset)
            validity=[oracle.call32(0x32F500,p)!=0 for p in members]
            reference=[p for p,v in zip(members,validity) if v] if kind==38 else members
            row.update(referenceRule='valid-list' if kind==38 else 'copy-list',members=members,validity=validity)
        elif family=='Force' and kind==37:
            members=[oracle.call(0x58440,obj,i) for i in range(3)]
            validity=[oracle.call32(0x32F500,p)!=0 for p in members]
            reference=[p for p,v in zip(members,validity) if v]
            row.update(referenceRule='valid-list',members=members,validity=validity)
        elif family=='Force' and kind==31:
            members=linked_list(oracle,raw+0xA8)
            force_id=oracle.call32(oracle.q(oracle.q(obj)+0x60)-game.exe,obj)
            selected=[oracle.call(0x22C810,p)!=obj and oracle.read(p+0x73,1)[0]==force_id for p in members]
            reference=[p for p,yes in zip(members,selected) if yes]
            row.update(referenceRule='selected-list',members=members,validity=selected)
        elif family=='Force' and kind==29 or game_data and kind==5:
            index=oracle.read(obj+0x15F if obj else game.q(raw+0x871E8)+0x1657,1)[0]
            force_table=[oracle.q(raw+0xDF30+8*i) for i in range(52)]
            reference=force_table[index if index<52 else 0]
            row.update(referenceRule='force-index',index=index,forceTable=force_table)
        elif family=='Foothold' and kind==40:
            reference=oracle.q(obj+0xA8);row.update(referenceRule='pointer',sourcePointer=reference)
        elif family=='CautionTroops' and kind==0:
            reference=struct.unpack('<H',oracle.read(obj+0x18,2))[0];row.update(referenceRule='caution-number',stored=reference)
        elif family=='Army' and kind==18:
            ids=list(struct.unpack('<5H',oracle.read(obj+0x2C,10)))
            members=[oracle.q(raw+0xE270+8*(i if i<48400 else 0)) for i in ids]
            validity=[oracle.call32(0x32F500,p)!=0 for p in members]
            reference=[p for p,v in zip(members,validity) if v]
            row.update(referenceRule='valid-list',members=members,validity=validity,hexIds=ids)
        if reference is not None:
            if not row['written'] or row['native']!=reference:raise AssertionError((row,reference,trace.events if trace else None))
        if trace:trace.close()
        answers.append(row)
        if family=='District' and kind in [8,9,10,11]:
            # These nodes alias the stored pointer; they do not copy or check its pointed-to list type.
            # Register a real native list pointer in the native corps context to exercise the present branch.
            corps_context=oracle.call(0xBA250,oracle.call(0xB5F00),obj)
            corps_board=oracle.q(corps_context+0x18)
            list_name={8:'AREA_OF_INFLUENCE',9:'CAUTION_FOOTHOLD_LIST',10:'CAUTION_TROOPS_LIST',11:'CAUTION_ARMY_LIST'}[kind]
            input_pointer=objects['Force']+0x210
            oracle.call32(0xB26BA0,corps_board,ai.name_hash(list_name),input_pointer,keep=True)
            status=oracle.call32(classes[name]['exec'],node,context,agent,keep=True)
            ok=oracle.call32(0xB26470,board,target,out,0,read_after=[(out,8)])
            returned=struct.unpack('<Q',oracle.after[0])[0]
            if status!=1 or ok!=1 or returned!=input_pointer:raise AssertionError('district stored pointer alias')
            context_cases.append({'class':name,'kind':kind,'rule':'pointer-alias','input':input_pointer,'native':returned,'status':status})
        if family=='Troops' and not myself and kind==5:
            oracle.call32(0xB26990,board,ai.name_hash('AgentType'),2,keep=True)
            status=oracle.call32(classes[name]['exec'],node,context,obj,keep=True)
            ok=oracle.call32(0xB26300,board,target,out,0,read_after=[(out,4)])
            if status!=1 or not ok:raise AssertionError('troops agent kind 5 output')
            context_cases.append({'class':name,'kind':kind,'rule':'troops-agent-output','status':status,'native':struct.unpack('<i',oracle.after[0])[0]})
        print(f'{name} {kind}: {status}',flush=True)
    for name,kind,obj,offset in [('CVariableGetForceInfoNode',29,force,0x15F),
                                 ('CGetMyselfForceInfoNode',29,force,0x15F),
                                 ('CVariableGetGameDataNode',5,game.q(raw+0x871E8),0x1657)]:
        for value in [0,1,51,52,127,128,255]:
            oracle=Oracle(game=game);node=oracle.alloc(0x80);out=oracle.alloc(8)
            oracle.poke(obj+offset,bytes([value]))
            oracle.call32(0xB26990,board,ai.name_hash('AgentType'),4,keep=True)
            oracle.call32(0xB26BA0,board,source,force,keep=True)
            params=[source,kind,target] if name.startswith('CVariableGetForce') else [kind,target]
            oracle.poke(node+0x38,struct.pack('<'+'I'*len(params),*params))
            status=oracle.call32(classes[name]['exec'],node,context,force,keep=True)
            ok=oracle.call32(0xB26470,board,target,out,0,read_after=[(out,8)])
            returned=struct.unpack('<Q',oracle.after[0])[0];table=[oracle.q(raw+0xDF30+8*i) for i in range(52)]
            if status!=1 or not ok or returned!=table[value if value<52 else 0]:raise AssertionError('force index boundary')
            context_cases.append({'class':name,'kind':kind,'rule':'force-index','index':value,'forceTable':table,'native':returned,'status':status})
    return {'utc':datetime.now(timezone.utc).isoformat(),'pid':game.pid,'listPoolSnapshot':game.pool_snapshot,
            'method':'all documented info cases, native node and blackboard, copied pages only',
            'oracle_sha256':hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),'cases':answers,'contextCases':context_cases}


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--vectors',type=Path);args=parser.parse_args()
    result=audit(ListPoolSnapshot());text=vectors_text(result);folder=HERE/'.cache'/'ai_info_audit';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');path.write_text(text,encoding='utf-8')
    if args.vectors:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} info cases executed; {path}")


if __name__=='__main__':main()

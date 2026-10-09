# Offline triage of missing path edges; no Game imports or process access.
# python ai_path_triage.py --inventory .cache/ai_path_state_inventory/<stamp>.json --admission .cache/ai_path_facility_admission/<stamp>.json --output .cache/ai_path_triage.json
# Categories summarize the scoped code contracts in notes/ai.md; they are not new native evidence.
import argparse
from collections import Counter
import gzip
import hashlib
import json
from pathlib import Path
from ai_path_coverage import ledger

HERE=Path(__file__).resolve().parent
FIXTURES=('AiPathSearchBranchVectors','AiPathGlobalBranchVectors','AiPathWorldBranchVectors','AiPathPoolBranchVectors','AiPathTileFilterVectors','AiPathFacilityBranchVectors','AiPathReinsertVectors','AiPathSmallTrialVectors','AiPathFireVectors','AiPathMinorVectors')

# RVA: exact jump, missing edge, category, requirement, scoped explanation.
RULES={
 0x183B2D:('ja 0x183b42','fallthrough','changed_state','force46To50','A valid actual army with original owner46..50 is required for this direction.'),
 0x184FAD:('jbe 0x184ff9','taken','changed_state','force46To50','A valid actual army with original owner46..50 is required for this direction.'),
 0x183B39:('je 0x183b8f','fallthrough','changed_state','specialMinorForce','Original 232730 must return 1 for an army with owner 46..51.'),
 0x183B4E:('ja 0x183b5a','taken','changed_state','leaderAbove6000','An actual leader index above 6000 is needed.'),
 0x184FB8:('ja 0x184fc4','taken','changed_state','leaderAbove6000','An actual leader index above 6000 is needed.'),
 0x184084:('ja 0x1842df','taken','changed_state','nonzeroWord16','Context +28 and a real tile +16 word greater than zero are needed.'),
 0x183F8A:('je 0x1842e3','taken','changed_state','nullTiles','No null pointer exists in the audited tile table.'),
 0x18520C:('je 0x18553c','taken','changed_state','nullTiles','No null pointer exists in the audited tile table.'),
 0x1840DF:('je 0x1842df','taken','changed_state','admittedCategory2Or3','Valid non-gate category2 with option64 disabled needs terrain16/17 exception admission through original18A080; terrain eligibility alone is insufficient.'),
 0x1840E8:('jne 0x184100','fallthrough','changed_state','admittedCategory2Or3','Valid non-gate category3 with option64 disabled needs terrain16/17 exception admission through original18A080; terrain eligibility alone is insufficient.'),
 0x1852E5:('je 0x185538','taken','changed_state','admittedCategory2Or3','Valid non-gate category2 with option64 disabled needs terrain16/17 exception admission through original18A080; terrain eligibility alone is insufficient.'),
 0x183DE1:('ja 0x183df7','taken','outside_caller_contract',None,'Valid goal indices address the clean 48400-node array; 18AFA0 does unchecked indexing.'),
 0x183E6E:('je 0x183e8c','taken','outside_caller_contract',None,'An initialized allocator has a non-null node array; do not corrupt its header.'),
 0x183E7C:('je 0x183e8c','taken','outside_caller_contract',None,'An initialized allocator has a non-null head table; do not corrupt its header.'),
 0x183E84:('jae 0x183e8c','taken','outside_caller_contract',None,'Original allocated index entries are within allocator capacity.'),
 0x183EEB:('je 0x183f36','taken','outside_caller_contract',None,'Initialized pool arrays and genuine exhaustion were audited separately.'),
 0x184049:('je 0x184079','taken','outside_caller_contract',None,'An initialized allocator has a non-null head table; do not corrupt its header.'),
 0x184073:('jne 0x1842df','fallthrough','code_contract_scoped',None,'184059 skips null and 184063 enters this block only on a non-null matching node; rcx is unchanged.'),
 0x184117:('jle 0x184137','taken','code_contract_scoped',None,'Clean initial head=0 and stable goal count<=0 skip 18AFA0 insertion and cannot enter the search loop.'),
 0x18534C:('jle 0x185369','taken','code_contract_scoped',None,'Clean initial head=0 and stable goal count<=0 skip 18AFA0 insertion and cannot enter the search loop.'),
 0x185437:('jg 0x185538','taken','code_contract_scoped',None,'For limit>=0 and clean nodes, seeds cost0 and new insertion cost<=limit; queued improvement only reduces cost.'),
 0x183ECB:('jne 0x183ea0','taken','code_analysis',None,'Fixed owner44/previousOwner0 prune both old-force action blocks, but graphics pool calls and unresolved indirect targets remain. Transitive fresh-index mutation closure is unproved; do not repeat the same source=goal trial.'),
 0x184207:('je 0x184225','taken','code_contract_scoped',None,'For clean nodes, distinct valid goals, stable terrain and an intact queue, options&5==0 gives fixed destination cost10000/20000 and monotone parents; existing nodes cannot improve. See ai_path_cost_contract.py.'),
 0x185454:('je 0x185472','taken','code_contract_scoped',None,'For clean nodes, distinct valid goals, stable terrain and an intact queue, options&5==0 gives fixed destination cost10000 and monotone parents; existing nodes cannot improve. See ai_path_cost_contract.py.'),
 0x184230:('jne 0x18423f','fallthrough','code_analysis',None,'Derive a valid search sequence leaving an empty queue after removing an improved node.'),
 0x18547D:('jne 0x18548c','fallthrough','code_analysis',None,'Derive a valid search sequence leaving an empty queue after removing an improved node.'),
}


def read_evidence(path):
    data=path.read_bytes();result=json.loads(gzip.decompress(data) if path.suffix=='.gz' else data)
    if not result['clockStable'] or result['clockBefore']!=result['clockAfter']:raise ValueError('Unstable evidence')
    return result,dict(path=str(path),sha256=hashlib.sha256(data).hexdigest())


def edges(report):
    return {(backend,row['rva'],side) for backend,b in report['backends'].items() for row in b['conditions'] for side in ('taken','fallthrough') if row[side]}


def classify(report,inventory,admission,contracts=None):
    if inventory['clockBefore']!=admission['clockBefore']:raise ValueError('Inventory and admission concern different worlds')
    if contracts is not None:
        if contracts['clockBefore']!=inventory['clockBefore'] or contracts['snapshot']['sha256']!=inventory['snapshot']['sha256']:
            raise ValueError('Contracts and inventory concern different frozen images')
        table=contracts['tileTable'];pointers=table['pointers'];first=pointers[0] if pointers else 0
        if (not table['verified'] or table['count']!=48400 or table['stride']!=32 or table['nullCount']!=0
            or len(pointers)!=48400 or not first or first+48400*32>2**64
            or any(value!=first+i*32 for i,value in enumerate(pointers)) or inventory['nullTiles']):
            raise ValueError('Constructed table evidence is invalid or contradictory')
    rows=[]
    for backend,b in report['backends'].items():
        for condition in b['conditions']:
            for side in condition['missingEdges']:
                rva=condition['rva'];rule=RULES.get(rva)
                if rule is None or (condition['text'],side)!=rule[:2]:
                    category='unclassified';requirement=None;reason='New instruction or missing direction: inspect code before assigning an old explanation.'
                else:
                    _,_,category,requirement,reason=rule
                    if contracts is not None and rva in (0x183F8A,0x18520C):
                        category='code_contract_scoped';requirement=None
                        reason='Successful original world allocation fills all48400 tile pointers at stride32. The audited intact constructed table makes both indexed and fallback0 lookups non-null; allocation failure, load/construction transitions and corrupt tables are outside this scope.'
                row=dict(backend=backend,rva=rva,missingEdge=side,category=category,reason=reason,observed=False)
                if requirement:
                    evidence=admission if requirement=='admittedCategory2Or3' else inventory
                    values=evidence[requirement]
                    if requirement=='admittedCategory2Or3':
                        wanted=3 if rva==0x1840E8 else 2;categories={s['buildingId']:s['category'] for s in admission['sites']}
                        values=[v for v in values if categories[v['site']]==wanted]
                    row.update(requirement=requirement,eligibleInputs=len(values),nextAction='derive_small_native_trial' if values else 'wait_for_changed_state')
                else:row['nextAction']='inspect_code' if category in ('code_analysis','unclassified') else 'retain_scoped_code_evidence'
                rows.append(row)
    return rows


def build(paths,inventory_path,admission_path,contracts_path=None):
    report=ledger(paths);previous=ledger(paths[:-1]) if len(paths)>1 else dict(completedCalls=0,backends={})
    inventory,inventory_source=read_evidence(inventory_path);admission,admission_source=read_evidence(admission_path)
    contracts,contract_source=read_evidence(contracts_path) if contracts_path else (None,None)
    rows=classify(report,inventory,admission,contracts)
    return dict(method='offline scheduling summary of existing code/evidence; no process reads and no missing edge marked observed',
                sources=report['sources']+[inventory_source,admission_source]+([contract_source] if contract_source else []),completedNativeSearches=report['completedCalls'],
                lastBatch=dict(calls=report['completedCalls']-previous['completedCalls'],newEdges=len(edges(report)-edges(previous))),
                missingDirectionCount=len(rows),categories=dict(Counter(r['category'] for r in rows)),targets=rows,
                nextStaticTargets=[r for r in rows if r['nextAction']=='inspect_code'],
                nextNativeTargets=[r for r in rows if r['nextAction']=='derive_small_native_trial'])


def main():
    parser=argparse.ArgumentParser();parser.add_argument('fixtures',nargs='*',type=Path);parser.add_argument('--inventory',type=Path,required=True);parser.add_argument('--admission',type=Path,required=True);parser.add_argument('--contracts',type=Path);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    paths=args.fixtures or [HERE.parent/'Tests'/(name+'.json.gz') for name in FIXTURES]
    result=build(paths,args.inventory,args.admission,args.contracts);args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('last batch',result['lastBatch'],'missing directions',result['missingDirectionCount'],'categories',result['categories'])
    print('next static conditions',[hex(r['rva']) for r in result['nextStaticTargets']],'eligible native conditions',len(result['nextNativeTargets']))


if __name__=='__main__':main()

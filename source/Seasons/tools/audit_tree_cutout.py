"""Inspect saved tree assembly/Reflection receipts, without game or GPU access."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'Reverse'))
from season_shader_cutout import recognize_cutouts,bind_declarations


def sha(data):return hashlib.sha256(data).hexdigest()


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--graph',type=Path,required=True)
    parser.add_argument('--instructions',type=Path,required=True)
    parser.add_argument('--out',type=Path,required=True)
    args=parser.parse_args();graph_raw=args.graph.read_bytes();graph=json.loads(graph_raw)
    raw=args.instructions.read_bytes();instructions=json.loads(raw)
    if instructions['graphSha256']!=sha(graph_raw):raise ValueError('Shader graph receipt changed')
    if set(graph['bytecodes'])!=set(instructions['bytecodes']):raise ValueError('Shader coverage differs')
    results={};rejected=[];counts=Counter()
    for digest,facts in instructions['bytecodes'].items():
        if not facts['discardInstructionCount']:continue
        if facts['profile']['stage']!=0 or graph['bytecodes'][digest]['metadata']['version']>>16!=0:
            raise ValueError('Cutout stage/Reflection differs')
        path=Path(facts['assemblyFile'])
        if path.resolve()!=(args.instructions.parent/'assembly'/(digest+'.asm')).resolve():
            raise ValueError('Cutout assembly is outside the recorded shader folder')
        text=path.read_bytes()
        if sha(text)!=facts['assemblySha256']:raise ValueError('Saved assembly changed')
        try:
            patterns=recognize_cutouts(text.decode('ascii'))
            if len(patterns)!=facts['discardInstructionCount']:raise ValueError('Discard receipt count differs')
            receipts=[dict(pattern=p,declarations=bind_declarations(p,graph['bytecodes'][digest])) for p in patterns]
        except ValueError as error:
            rejected.append(dict(sha256=digest,reason=str(error)));continue
        for receipt in receipts:counts[receipt['pattern']['pattern']]+=1
        results[digest]=dict(assemblySha256=sha(text),cutouts=receipts,
            outputSignature=graph['bytecodes'][digest]['metadata']['outputs'],actualRuntimePassVerified=False)
    result=dict(graphSha256=sha(graph_raw),instructionsSha256=sha(raw),gameProcessAccess=False,deviceCreated=False,
        shaders=results,rejected=rejected,patternCounts=dict(counts),actualAlphaCoverageVerified=False,
        actualBindingValuesVerified=False,readyForNextGameTrial=False,productionReady=False)
    args.out.parent.mkdir(parents=True,exist_ok=True);args.out.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(recognizedShaders=len(results),recognizedCutouts=sum(counts.values()),patterns=dict(counts),
        rejectedShaders=len(rejected),gameProcessAccess=False)))
    if rejected:raise SystemExit(1)


if __name__=='__main__':main()

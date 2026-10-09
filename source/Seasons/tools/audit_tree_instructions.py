"""Index installed tree Shader instructions from verified local snapshots only."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'Reverse'))
from season_shader_layout import LayoutReflector
from season_shader_instructions import disassemble,instruction_facts


def sha(data):return hashlib.sha256(data).hexdigest()


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--graph',required=True,type=Path)
    parser.add_argument('--shader-directory',required=True,type=Path)
    parser.add_argument('--out',required=True,type=Path)
    args=parser.parse_args();graph_raw=args.graph.read_bytes();graph=json.loads(graph_raw)
    index_raw=(args.shader_directory/'system-index.json').read_bytes();index=json.loads(index_raw)
    if sha(index_raw)!=graph['systemIndexSha256']:raise ValueError('Graph catalog changed')
    files={}
    for row in index:files.setdefault(row['sha256'],row)
    reflector=LayoutReflector();records={};stage_counts=Counter()
    assembly_folder=args.out.parent/'assembly';assembly_folder.mkdir(parents=True,exist_ok=True)
    for digest,layout in graph['bytecodes'].items():
        source=files[digest];data=(args.shader_directory/source['file']).read_bytes()
        if sha(data)!=digest:raise ValueError('Verified archived DXBC changed')
        text=disassemble(reflector,data);facts=instruction_facts(text)
        version=layout['metadata']['version']
        expected=((version>>16)&0xffff,(version>>4)&15,version&15)
        profile=facts['profile']
        if expected!=(profile['stage'],profile['major'],profile['minor']) or source['stage']!=profile['stage']:
            raise ValueError('Local assembly/Reflection/catalog stage mismatch')
        cb={b['binding']['slot']:b for b in layout['constantBuffers'] if b['binding'] is not None}
        for reference in facts['constantBufferReferences']:
            if not reference['directIndex']:continue
            buffer=cb.get(reference['slot'])
            if buffer is None or any(offset+4>buffer['size'] for offset in reference['byteOffsets']):
                raise ValueError('Assembly constant-buffer reference exceeds reflected buffer')
            reference['bufferName']=buffer['name']
            reference['variableNames']=sorted({v['name'] for v in buffer['variables']
                if any(v['offset']<=offset<v['offset']+v['size'] for offset in reference['byteOffsets'])})
        file=assembly_folder/(digest+'.asm');file.write_text(text,encoding='utf-8')
        facts['assemblyFile']=str(file);facts['assemblySha256']=sha(file.read_bytes())
        records[digest]=facts;stage_counts[profile['stage']]+=1
    result=dict(gameProcessAccess=False,deviceCreated=False,graphSha256=sha(graph_raw),
        compilerPath=str(reflector.path),compilerSha256=reflector.sha,bytecodes=records,
        actualGamePassVerified=False,actualAlphaDataflowVerified=False,productionReady=False)
    args.out.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(uniqueBytecodes=len(records),stageCounts=dict(stage_counts),
        shadersWithDiscard=sum(bool(r['discardInstructionCount']) for r in records.values()),
        discardInstructions=sum(r['discardInstructionCount'] for r in records.values()),
        shadersWithResourceWrites=sum(bool(r['resourceWriteInstructionCount']) for r in records.values()),
        resourceWriteInstructions=sum(r['resourceWriteInstructionCount'] for r in records.values()),
        gameProcessAccess=False)))


if __name__=='__main__':main()

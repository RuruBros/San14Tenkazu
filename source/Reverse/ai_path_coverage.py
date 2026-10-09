# Offline ledger of observed whole-backend condition edges; reads logged evidence only.
# python ai_path_coverage.py <log-or-json.gz>... --output .cache/ai_path_coverage.json
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import re


def ledger(paths):
    functions={};sources=[];cases=0
    for path in paths:
        data=path.read_bytes();root=json.loads(gzip.decompress(data) if path.suffix=='.gz' else data)
        sources.append(dict(path=str(path),sha256=hashlib.sha256(data).hexdigest()))
        if not root['clockStable']:raise AssertionError('unstable game evidence is not coverage')
        for run in root.get('stages',[root]):
            if run['failures'] or not run['clockStable']:raise AssertionError('native fault evidence is not successful coverage')
            cases+=len(run['cases'])
            for backend,branches in run['branchCoverage'].items():
                if not branches:continue
                actual=functions.setdefault(backend,{})
                flow=run['static'][str(int(backend,16))].splitlines()
                positions={int(m[1],16):i for i,line in enumerate(flow) if (m:=re.match(r'^\s*([0-9a-f]+)\s',line))}
                for b in branches:
                    if b['rva'] not in positions:raise AssertionError('branch is outside the logged native flow')
                    i=positions[b['rva']]
                    record=actual.setdefault(b['rva'],dict(rva=b['rva'],text=b['text'],taken=False,fallthrough=False,staticContext=flow[max(0,i-5):i+2]))
                    if record['text']!=b['text']:raise AssertionError('native branch text differs across evidence files')
                    record['taken']|=b['taken'];record['fallthrough']|=b['fallthrough']
    result=dict(sources=sources,completedCalls=cases,backends={})
    for backend,records in functions.items():
        rows=[]
        for rva,b in sorted(records.items()):
            missing=[edge for edge in ('taken','fallthrough') if not b[edge]]
            rows.append(dict(b,missingEdges=missing,status='both' if not missing else 'unobserved' if len(missing)==2 else 'one'))
        result['backends'][backend]=dict(total=len(rows),both=sum(b['status']=='both' for b in rows),one=sum(b['status']=='one' for b in rows),unobserved=sum(b['status']=='unobserved' for b in rows),conditions=rows)
    return result


def main():
    parser=argparse.ArgumentParser();parser.add_argument('logs',type=Path,nargs='+');parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    result=ledger(args.logs);args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(result['completedCalls'],'completed native calls')
    for backend,b in result['backends'].items():print(backend,b['both'],'/',b['total'],'both;',b['one'],'one;',b['unobserved'],'unobserved')


if __name__=='__main__':main()

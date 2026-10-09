"""Reflect verified local DXBC in the analysis process; no game GPU calls."""
import argparse
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'Reverse'))
from season_shader_match import Reflector


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--links',required=True,type=Path)
    parser.add_argument('--shader-directory',required=True,type=Path)
    parser.add_argument('--out',required=True,type=Path)
    args=parser.parse_args();raw=args.links.read_bytes();links=json.loads(raw)
    if links['errors'] or not all(r['recordedCatalogMatches'] for r in links['resources'].values()):
        raise ValueError('Installed shader resource receipt is incomplete')
    reflector=Reflector();entries=[]
    for identity,resource in links['resources'].items():
        for container in resource['containers']:
            file=args.shader_directory/('system-'+container['sha256']+'.dxbc');data=file.read_bytes()
            if hashlib.sha256(data).hexdigest()!=container['sha256']:
                raise ValueError('Verified DXBC snapshot differs')
            result=reflector.inspect(data)
            entries.append(dict(resourceKtid=identity,offset=container['offset'],sha256=container['sha256'],
                                catalogStage=container['stage'],**result))
    result=dict(mode='D3DReflect in this analysis process only',gameProcessAccess=False,gameComCalls=False,
        linksSha256=hashlib.sha256(raw).hexdigest(),compilerPath=str(reflector.path),compilerSha256=reflector.sha,
        entries=entries,gameDrawPassVerified=False,shaderBytecodeIdentityVerified=False,productionReady=False)
    args.out.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(reflectedContainers=len(entries),resources=len(links['resources']),gameProcessAccess=False)))


if __name__=='__main__':main()

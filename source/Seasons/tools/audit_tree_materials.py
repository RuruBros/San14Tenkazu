"""Compare installed G1M attributes with tree DXBC declarations offline only."""
import argparse
from collections import Counter,defaultdict
import hashlib
import json
from pathlib import Path

import ktgl
from g1m_material_audit import read_material_sections,correlate_attribute
from audit_tree_state_graph import reachable_shader_objects,shader_fields


def sha(data):return hashlib.sha256(data).hexdigest()


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--directory',required=True,type=Path)
    parser.add_argument('--trees',required=True,type=Path)
    parser.add_argument('--graph',required=True,type=Path)
    parser.add_argument('--links',required=True,type=Path)
    parser.add_argument('--out',required=True,type=Path)
    args=parser.parse_args()
    tree_raw=(args.trees/'manifest.json').read_bytes();trees=json.loads(tree_raw)
    graph_raw=args.graph.read_bytes();graph=json.loads(graph_raw)
    links_raw=args.links.read_bytes();links=json.loads(links_raw)
    if sha(links_raw)!=graph['rootLinksSha256'] or links['errors']:raise ValueError('Tree Shader link receipt differs')
    root_by_group={g['meshGroup']:{c['objectId'] for c in g['candidates']} for g in links['groups']}
    folder=args.directory/'0002';catalog=ktgl.index(folder,'FieldEditor4');records=[];names=Counter();matches=0
    for tree in trees:
        entries=[r for r in catalog if f'{r["ktid"]:08X}'==tree['modelKtid']]
        if len(entries)!=1:raise ValueError('Installed tree G1M is ambiguous or absent')
        data=ktgl.read(folder,entries[0])
        if sha(data)!=tree['modelSha256'] or (args.trees/(tree['Name']+'.g1m')).read_bytes()!=data:
            raise ValueError('Installed tree G1M differs from original export')
        parsed=read_material_sections(data)
        if len(parsed['materials'])!=len(tree['materials']):raise ValueError('Material export count differs')
        for material in tree['materials']:
            if parsed['materials'][material['Index']]['rawTextureBindings']!=[b['RawWords'] for b in material['bindings']]:
                raise ValueError('Independent G1MG texture binding reader differs')
        groups_by_material=defaultdict(set)
        for group in tree['meshGroups']:
            for index in group['Submeshes']:
                groups_by_material[tree['submeshes'][index]['MaterialIndex']].add(group['RawName'])
        correlated=[]
        for material in parsed['namedAttributes']:
            groups=sorted(groups_by_material[material['index']]);roots=set().union(*(root_by_group[g] for g in groups)) if groups else set()
            if not roots:raise ValueError('Exported material has no identified static mesh group')
            reachable,_=reachable_shader_objects(graph['objects'],roots)
            resource_ids={shader_fields(v)[0] for versions in reachable.values() for v in versions}
            shas={c['sha256'] for r in resource_ids for c in graph['resources'][r]['containers']}
            variables=defaultdict(list)
            for digest in sorted(shas):
                bc=graph['bytecodes'][digest]
                for buffer in bc['constantBuffers']:
                    for variable in buffer['variables']:
                        variables[variable['name']].append(dict(sha256=digest,bufferName=buffer['name'],
                            binding=buffer['binding'],variable=variable))
            values=[]
            for attribute in material['attributes']:
                names[attribute['name']]+=1;candidates=[]
                for candidate in variables[attribute['name']]:
                    comparison=correlate_attribute(attribute,candidate['variable'])
                    candidates.append(dict(**candidate,comparison=comparison));matches+=comparison['nameAndByteSizeMatched']
                values.append(dict(**attribute,shaderDeclarationCandidates=candidates))
            correlated.append(dict(materialIndex=material['index'],staticMeshGroups=groups,staticShaderRoots=sorted(roots),
                attributes=values,actualRuntimeVersionOrPassVerified=False))
        records.append(dict(name=tree['Name'],modelKtid=tree['modelKtid'],sha256=sha(data),source=entries[0],
            rawMaterialAudit=parsed,correlations=correlated,actualMaterialUploadVerified=False))
    result=dict(gameProcessAccess=False,deviceCreated=False,treeManifestSha256=sha(tree_raw),
        graphSha256=sha(graph_raw),linksSha256=sha(links_raw),models=records,
        namedAttributeCounts=dict(names),nameAndByteSizeMatches=matches,actualAlphaThresholdVerified=False,
        actualConstantBufferValuesVerified=False,productionReady=False)
    args.out.parent.mkdir(parents=True,exist_ok=True)
    args.out.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(treeModels=len(records),materials=sum(len(r['correlations']) for r in records),
        namedAttributes=sum(names.values()),uniqueAttributeNames=len(names),nameAndByteSizeMatches=matches,
        gameProcessAccess=False)))


if __name__=='__main__':main()

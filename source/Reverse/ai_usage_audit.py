# Separate registered classes from actual call sites in the current behaviour-tree file.
# python ai_usage_audit.py
# Static image/file reads only; no game execution, writes or hooks.
import argparse
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
from pathlib import Path

import ai
from ai_friend import HERE, vectors_text
from image import Image, IMAGE


def audit():
    path = Path(ai.game_dir()) / ai.FILE
    trees, classes, image = ai.read_file(path), ai.classes(), Image()
    callers = defaultdict(list)
    for instruction, target, function in image.xref()[1]:
        callers[target].append([instruction, function])
    sites = {key: [] for key in classes}
    for tree, nodes in enumerate(trees):
        for node in nodes:
            sites[node.cls].append([tree, node.i])
    rows = []
    for key, cls in sorted(classes.items()):
        rows.append({'hash': key, 'name': cls['name'], 'kind': cls['kind'],
                     'exec': cls['exec'], 'ctor': cls['ctor'], 'treeNodes': sites[key],
                     'directExecCallers': callers[cls['exec']],
                     'directCtorCallers': callers[cls['ctor']]})
    return {'utc': datetime.now(timezone.utc).isoformat(),
            'method': 'current bt_exce.bin class membership and static image direct-call xrefs; absence is not a claim about every possible indirect call',
            'tree_sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
            'image_sha256': hashlib.sha256(Path(IMAGE).read_bytes()).hexdigest(),
            'treeNodeCounts': [len(nodes) for nodes in trees],
            'registeredClasses': len(rows), 'referencedClasses': sum(bool(row['treeNodes']) for row in rows),
            'classes': rows,
            'facilityAttackNextRelay': {
                'stateNextHexOffset': 0x10,
                'code': image.text(0x671AB, 0x671F8),
                'areaResolverCode': image.text(0x229EA0, 0x229ECC)}}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    result = audit()
    folder = HERE / '.cache' / 'ai_usage_audit'
    folder.mkdir(parents=True, exist_ok=True)
    path = args.output or folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    path.write_text(vectors_text(result), encoding='utf-8')
    print(f"{result['referencedClasses']}/{result['registeredClasses']} registered classes have current tree nodes; {path}")
    for row in result['classes']:
        if row['name'] in ('CExecuteAreaOccupyNode', 'CExecuteAttackAreaBuildingNode', 'CDecideAreaOccupyRouteNode'):
            print(row['name'], row['treeNodes'], 'direct execute callers', row['directExecCallers'])


if __name__ == '__main__':
    main()

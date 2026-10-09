# Check the non-heuristic path contract against saved code and complete native logs.
# python ai_path_cost_contract.py --output .cache/ai_path_cost_contract.json
# No image dumping, Game imports, process access, native execution or new coverage.
import argparse
from collections import Counter
import gzip
import hashlib
import json
from pathlib import Path
import re

from ai_path_triage import HERE, FIXTURES


# These are evidence anchors for the written argument, not a whole-program prover.
ANCHORS = {
    0x183A30: {
        0x183C5A: 'and r14d, 5', 0x183C5E: 'mov dword ptr [rbp + 0x77], r14d',
        0x183ECD: 'mov r14d, dword ptr [rbp + 0x77]',
        0x183F9C: 'mov edi, dword ptr [rsp + 0x40]', 0x183FA0: 'test r14d, r14d',
        0x183FA3: 'jne 0x183fad', 0x183FA5: 'mov r14d, 0x2710',
        0x183FBF: 'cmp esi, dword ptr [rbp + 0x57]', 0x183FC2: 'je 0x184177',
        0x183FCD: 'jne 0x1840ef', 0x18408A: 'cmp dword ptr [rdx + 0x2c], 0',
        0x184090: 'cmp byte ptr [rbx + 0x10], 7', 0x184096: 'add r14d, 0x2710',
        0x18414F: 'cmp al, 1', 0x184151: 'jne 0x184177',
        0x1841AF: 'call 0x1864f0', 0x1841B9: 'add ebx, eax',
        0x184207: 'je 0x184225', 0x184270: 'mov r14d, dword ptr [rbp + 0x77]',
        0x184200: 'mov r14d, dword ptr [rbp + 0x77]',
        0x1842DF: 'mov r14d, dword ptr [rbp + 0x77]',
        0x184242: 'cmp dword ptr [rax + 0x24], ebx',
        0x1842B0: 'cmp dword ptr [rax + 0x24], ebx',
    },
    0x184EB0: {
        0x1850C3: 'and r14d, 5', 0x1850C7: 'mov dword ptr [rbp + 0x77], r14d',
        0x18521E: 'test r14d, r14d', 0x185221: 'jne 0x18522b',
        0x185223: 'mov r14d, 0x2710', 0x185240: 'je 0x1853b1',
        0x185384: 'cmp al, 1', 0x185386: 'jne 0x1853b1',
        0x1853EC: 'call 0x1864f0', 0x1853F6: 'add ebx, eax',
        0x185454: 'je 0x185472', 0x1854C1: 'mov r14d, dword ptr [rbp + 0x77]',
        0x18544D: 'mov r14d, dword ptr [rbp + 0x77]',
        0x185538: 'mov r14d, dword ptr [rbp + 0x77]',
        0x185490: 'cmp dword ptr [rax + 0x24], ebx',
        0x185503: 'cmp dword ptr [rax + 0x24], ebx',
    },
    0x1864F0: {
        0x186510: 'test dl, 1', 0x186513: 'je 0x1866d9',
        0x1866DE: 'mov eax, ebp',
    },
}


def instructions(flow):
    result = {}
    for line in flow.splitlines():
        match = re.match(r'^\s*([0-9a-f]+)\s+(.+?)(?:\s*;.*)?$', line)
        if match:
            result[int(match[1], 16)] = ' '.join(match[2].split())
    return result


def check_code(static):
    anchors = []
    for entry, expected in ANCHORS.items():
        actual = instructions(static[entry])
        for rva, text in expected.items():
            if actual.get(rva) != text:
                raise ValueError(f'Code anchor changed: {rva:X}, expected {text}, got {actual.get(rva)}')
            anchors.append(dict(entry=entry, rva=rva, text=text))
    return anchors


def check_run(run):
    if run['options'] & 5:
        return None
    if len(set(run['goals'])) != len(run['goals']) or any(not 0 <= h < 48400 for h in run['goals']):
        raise ValueError('Contract requires distinct valid goals')
    bounded = run['rva'] == 0x184EB0
    penalty_flag = dict(run.get('contextWords', [])).get(0x2C, 0)
    previous_parent_cost = -1
    steps, counts = {}, Counter()
    for row in run['relaxations']:
        step = row['stepCost']
        if step not in (10000, 20000):
            raise ValueError('Non-heuristic step is outside the code contract')
        if step == 20000 and (bounded or not penalty_flag or run['options'] & 0xC or row['hex'] == run['source']):
            raise ValueError('Terrain penalty bypass differs from the code contract')
        if row['hex'] in steps and steps[row['hex']] != step:
            raise ValueError('Destination cost changed within a search')
        steps[row['hex']] = step
        parent = row['parentCost']
        if parent < previous_parent_cost:
            raise ValueError('Observed parent costs are not nondecreasing')
        previous_parent_cost = parent
        if not 0 <= parent <= 20000 * 48399 or row['newCost'] != parent + step:
            raise ValueError('Observed cost violates the clean-node no-overflow bound')
        if row['accepted'] and row['flags'] & 3:
            raise ValueError('An existing-node improvement contradicts this scoped contract')
        counts['decisions'] += 1
        counts['penalizedSteps'] += step == 20000
        counts['acceptedNewNodes'] += row['accepted']
    return counts


def build(paths, helper_path):
    sources, static, counts = [], {}, Counter()
    for path in [*paths, helper_path]:
        data = path.read_bytes()
        root = json.loads(gzip.decompress(data) if path.suffix == '.gz' else data)
        sources.append(dict(path=str(path), sha256=hashlib.sha256(data).hexdigest()))
        if not root['clockStable']:
            raise ValueError('Unstable evidence')
        for stage in root.get('stages', [root]):
            if stage['failures'] or not stage['clockStable']:
                raise ValueError('Faulted or unstable evidence')
            for key, flow in stage['static'].items():
                entry = int(key, 0) if key.startswith('0x') else int(key)
                if entry not in ANCHORS:
                    continue
                if entry in static and instructions(flow) != instructions(static[entry]):
                    raise ValueError('Code differs across saved evidence')
                static[entry] = flow
            if path == helper_path:
                continue
            for run in stage['cases']:
                result = check_run(run)
                if result is not None:
                    counts['searches'] += 1
                    counts.update(result)
    return dict(method='saved code anchors and existing native logs; scoped written argument, not a new runtime edge or whole-program proof',
                sources=sources, codeAnchors=check_code(static), observations=dict(counts),
                contract=dict(optionsMask5=0, cleanNodeCount=48400, distinctValidGoals=True,
                              stableTileTerrain=True, validQueue=True, seedCost=0, minimumStep=10000,
                              maximumStep=20000, maximumSimplePathCost=20000 * 48399),
                scopedDirections=[dict(rva=rva, edge='taken', observed=False) for rva in (0x184207, 0x185454)],
                argument='Destination step is fixed at 10000, plus only the unbounded terrain-7 penalty. Bit0 disables mobility and threat costs. Priority equals stored cost; parents pop in nondecreasing cost order. Later discovery adds the same destination step to an equal or greater parent cost, so it cannot improve an existing node. First-discovery parent chains are acyclic and contain at most 48400 nodes; costs stay below signed INT_MAX. Applies only to clean scratch, distinct valid goals, stable tile terrain and an intact original queue.')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = build([HERE.parent / 'Tests' / (n + '.json.gz') for n in FIXTURES], HERE.parent / 'Tests' / 'AiPathHelperVectors.json')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(result['observations'], 'scoped missing directions', [hex(r['rva']) for r in result['scopedDirections']])


if __name__ == '__main__':
    main()

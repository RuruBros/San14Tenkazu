# Saved-image direct-call graph for185B30; no process reads, execution or mutation proof.
# python ai_path_list_boundary.py --output .cache/ai_path_list_boundary/report.json
import argparse
from collections import Counter, deque
import hashlib
import json
from pathlib import Path

from capstone.x86_const import X86_OP_IMM, X86_OP_MEM, X86_REG_RIP
from ai_attack_boundaries import imports
from image import Image
from ai_static_switch import flow as switch_flow

HERE = Path(__file__).resolve().parent
POOL_START, POOL_END = 0x27D10E0, 0x27D1168


def closure(im, entry=0x185B30, maximum=3000, imported=None, switches=False, known_iterators=False, scalar_context=None):
    im.md.detail = True
    imported = imports(im) if imported is None else imported
    pending, seen, parents = deque([entry]), {}, {entry: None}
    edges, unresolved, pool, dispatches = [], [], [], []
    iterator = None
    if known_iterators:
        from ai_static_facts import bindings
        iterator = bindings(im)
    iterator_sites = set(iterator['callSites']) if iterator else set()
    scalar=None
    if scalar_context is not None:
        from ai_scalar_flow import specialize
        scalar=specialize(im,entry,scalar_context['registers'],scalar_context.get('stack'))
    while pending and len(seen) < maximum:
        start = pending.popleft()
        if start in seen: continue
        code, tables = switch_flow(im, start) if switches else (list(im.flow(start)), [])
        if scalar is not None and start==entry:
            reachable=set(scalar['reachable'])
            code=[ins for ins in code if ins.address in reachable]
        dispatches.extend(tables); resolved = {row['rva'] for row in tables}
        positions = {i.address for i in code}
        seen[start] = dict(entry=start, instructions=[dict(rva=i.address, size=i.size,
                              bytes=bytes(i.bytes).hex(), text=i.mnemonic + ' ' + i.op_str) for i in code])
        if not code:
            unresolved.append(dict(entry=start, reason='no decoded instructions'))
        for i in code:
            for operand in i.operands:
                if operand.type == X86_OP_MEM and operand.mem.base == X86_REG_RIP:
                    target = i.address + i.size + operand.mem.disp
                    if POOL_START <= target < POOL_END:
                        pool.append(dict(entry=start, rva=i.address, target=target, text=i.mnemonic + ' ' + i.op_str))
            if i.mnemonic not in ('call', 'jmp') or not i.operands: continue
            if i.address in resolved: continue
            if i.address in iterator_sites:
                edge=dict(entry=start,rva=i.address,kind='scoped_iterator_call',text=i.mnemonic+' '+i.op_str,target=iterator['target'])
                edges.append(edge)
                if iterator['target'] not in parents: parents[iterator['target']] = edge
                pending.append(iterator['target']); continue
            operand = i.operands[0]
            if i.mnemonic == 'jmp' and operand.type == X86_OP_IMM and operand.imm in positions: continue
            edge = dict(entry=start, rva=i.address, kind=i.mnemonic, text=i.mnemonic + ' ' + i.op_str)
            if operand.type == X86_OP_IMM:
                target = operand.imm; edge['target'] = target; edges.append(edge)
                if 0 < target < len(im.img):
                    if target not in parents: parents[target] = edge
                    pending.append(target)
                else: unresolved.append(dict(edge, reason='target outside saved image'))
            else:
                edge['reason'] = 'indirect target not resolved or followed'
                if operand.type == X86_OP_MEM and operand.mem.base == X86_REG_RIP:
                    slot = i.address + i.size + operand.mem.disp
                    edge['slot'] = slot
                    if slot in imported: edge['import'] = imported[slot]
                unresolved.append(edge)
    paths = {}
    for target in (0x18B60, 0x18DF0, 0x18770, 0x18830):
        if target not in seen: continue
        at, path = target, []
        while parents[at] is not None:
            edge = parents[at]; path.append(edge); at = edge['entry']
        paths[hex(target)] = path[::-1]
    return dict(entry=entry, functions=list(seen.values()), directEdges=edges, poolReferences=pool,
                unresolvedBoundaries=unresolved, resolvedSwitches=dispatches, iteratorBinding=iterator, scalarContext=scalar, pendingEntries=sorted(set(pending) - set(seen)),
                directWorklistComplete=not (set(pending) - set(seen)), operationPaths=paths,
                summary=dict(functions=len(seen), instructions=sum(len(r['instructions']) for r in seen.values()),
                    directEdges=len(edges), poolReferences=len(pool), unresolved=len(unresolved),
                    importedBoundaries=sum('import' in r for r in unresolved)),
                unresolvedKinds=dict(Counter((r.get('import') or {}).get('name', r['reason']) for r in unresolved)),
                loopTakenObserved=False, transitiveMutationClosureProved=False,
                method='saved-image direct calls and external direct tail jumps; optional specified scalar root CFG, guarded tables and constructor-bound iterator slots; other callee feasibility not inferred; imports named but never executed',
                limits=['unbound register/virtual/IAT call targets and unrecognized indirect dispatches not followed', 'pool-index alias closure not proved',
                        'pool references are address references, not claims that the fresh index is mutated',
                        'complete direct worklist does not mean whole-program closure or183ECB taken coverage'])


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--maximum', type=int, default=3000); parser.add_argument('--switches', action='store_true')
    parser.add_argument('--known-iterators', action='store_true'); args = parser.parse_args()
    result = closure(im := Image(), maximum=args.maximum, switches=args.switches, known_iterators=args.known_iterators)
    result['imageSha256'] = hashlib.sha256(im.img).hexdigest()
    result['analyzerSha256'] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    if args.switches: result['switchAnalyzerSha256'] = hashlib.sha256((HERE / 'ai_static_switch.py').read_bytes()).hexdigest()
    if args.known_iterators: result['factsAnalyzerSha256'] = hashlib.sha256((HERE / 'ai_static_facts.py').read_bytes()).hexdigest()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(result['summary'], 'direct worklist complete', result['directWorklistComplete'])
    print('native loop edge observed', result['loopTakenObserved'], 'mutation closure proved', result['transitiveMutationClosureProved'])


if __name__ == '__main__': main()

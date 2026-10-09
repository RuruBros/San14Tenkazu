# Follow direct calls carrying the attack node in registers or read stack slots, using saved image.bin.
# python ai_attack_boundaries.py [--self-check] [--max-contexts 256] [--sdk-header <synchapi.h>] [--output <json>]
# Imported/virtual calls, external escapes and unknown pointer returns remain explicitly bounded.
import argparse
from collections import deque
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct

import capstone
from capstone.x86_const import X86_OP_MEM, X86_REG_RIP
from ai_attack_access import FUNCTIONS, audit
from image import Image

HERE = Path(__file__).resolve().parent


def imports(im):
    pe = im.u32(0x3C)
    descriptor, size = struct.unpack_from('<II', im.img, pe + 24 + 112 + 8)
    result = {}
    def string(rva):
        return im.img[rva:im.img.index(b'\0', rva)].decode('ascii')
    for at in range(descriptor, descriptor + size, 20):
        original, _, _, module, first = struct.unpack_from('<5I', im.img, at)
        if not module: break
        if not original: continue
        index = 0
        while (name := im.u64(original + index * 8)):
            label = f'ordinal {name & 0xFFFF}' if name >> 63 else string(name + 2)
            result[first + index * 8] = {'module': string(module), 'name': label}
            index += 1
    return result


def closure(im, maximum, entries=FUNCTIONS, import_signatures=None):
    imported = imports(im)
    rows, pending, scheduled = {}, deque(), set()
    edges, unresolved, stack_checks, stack_indirect, non_arguments = {}, {}, {}, {}, {}
    stack_probes, return_summaries, call_returns = {}, {}, {}
    site_children, child_sites, limited = {}, {}, {}
    runs = 0

    def key_for(start, arguments):
        return (start, tuple(sorted(arguments.items())))

    def schedule(key):
        if key not in scheduled:
            pending.append(key); scheduled.add(key)

    def update_site(site):
        parent, pc = site
        values = [return_summaries.get(child) for child in site_children[site]]
        value = values[0] if values and values[0] is not None and all(v == values[0] for v in values) else None
        previous = call_returns.setdefault(parent, {}).get(pc)
        if value != previous:
            if value is None: call_returns[parent].pop(pc, None)
            else: call_returns[parent][pc] = value
            schedule(parent)

    for start in entries: schedule(key_for(start, {'rcx': ('node', 0)}))
    while pending:
        key = pending.popleft(); scheduled.remove(key)
        start, items = key; arguments = dict(items)
        if key not in rows and len(rows) >= maximum:
            limited[key] = {'entry': start, 'arguments': arguments, 'reason': 'context limit'}
            continue
        runs += 1
        if runs > maximum * 8:
            limited[key] = {'entry': start, 'arguments': arguments, 'reason': 'analysis iteration limit'}
            break
        row = audit(im, start, arguments, call_returns.get(key))
        row['contextId'] = rows[key]['contextId'] if key in rows else len(rows)
        rows[key] = row
        insns = {i.address: i for i in im.flow(start)}
        local_edges, local_unresolved, local_checks, local_indirect, local_non_arguments = [], [], [], [], []
        calls = {json.dumps(c, sort_keys=True): c for c in row['callBoundaries'] + row['stackCallCandidates']}
        sites = set()
        for call in calls.values():
            edge = {'fromContext': row['contextId'], **call}
            target = call['target']
            if target.startswith('0x'):
                rva = int(target, 16)
                if 0 < rva < len(im.img) and im.flow(rva):
                    followed = {k: tuple(v) for k, v in call['args'].items()}
                    if call['stackCandidates']:
                        if rva not in stack_probes:
                            stack_probes[rva] = audit(im, rva, {})['incomingStackReads']
                        slots = {r['offset'] for r in stack_probes[rva] if r['size'] == 8}
                        matched = {k: tuple(v) for k, v in call['stackCandidates'].items() if int(k[1:]) in slots}
                        local_checks.append(dict(fromContext=row['contextId'], rva=call['rva'], callee=rva,
                                                 candidates=call['stackCandidates'], matched=matched))
                        followed.update(matched)
                    # A saved local above outgoing stack arguments does not prove consumption.
                    if not followed: continue
                    edge['callee'] = rva
                    edge['followedArguments'] = {k: list(v) for k, v in followed.items()}
                    child = key_for(rva, followed)
                    site = (key, call['rva']); sites.add(site)
                    site_children.setdefault(site, set()).add(child)
                    child_sites.setdefault(child, set()).add(site)
                    if child not in rows and child not in limited: schedule(child)
                else: edge['reason'] = 'target outside saved image or no decoded instructions'
            else:
                ins = insns[call['rva']]
                operand = ins.operands[0]
                if operand.type == X86_OP_MEM and operand.mem.base == X86_REG_RIP:
                    slot = ins.address + ins.size + operand.mem.disp
                    edge['slot'] = slot
                    if slot in imported: edge['import'] = imported[slot]
                if 'import' in edge:
                    identity=(edge['import']['module'].lower(),edge['import']['name'])
                    signature=(import_signatures or {}).get(identity)
                    if signature:
                        edge['signature']=signature
                        edge['rawAliasCandidates']=edge['args']
                        edge['args']={k:v for k,v in edge['args'].items() if k in signature['registerParameters']}
                        declared_stack={k:v for k,v in edge['stackCandidates'].items() if k in signature['stackParameters']}
                        edge['rawStackCandidates']=edge['stackCandidates'];edge['stackCandidates']=declared_stack
                        if not edge['args'] and not declared_stack:
                            edge['reason']='known node aliases are outside the declared import parameters; unknown aliases and import side effects are not ruled out'
                            local_non_arguments.append(edge);continue
                edge['reason'] = 'indirect/import call; no external implementation analyzed'
                if not edge['args']:
                    local_indirect.append(edge); continue
            local_edges.append(edge)
            if 'reason' in edge: local_unresolved.append(edge)
        edges[key], unresolved[key] = local_edges, local_unresolved
        stack_checks[key], stack_indirect[key] = local_checks, local_indirect
        non_arguments[key] = local_non_arguments
        values = [tuple(r['value']) if r['value'] else None for r in row['returnAliases']]
        value = values[0] if values and values[0] is not None and values[0][0] == 'node' and all(v == values[0] for v in values) else None
        changed = return_summaries.get(key) != value
        return_summaries[key] = value
        for site in sites: update_site(site)
        if changed:
            for site in child_sites.get(key, ()): update_site(site)
    flatten = lambda groups: [v for values in groups.values() for v in values]
    return {'contexts': list(rows.values()), 'callEdges': flatten(edges),
        'unresolvedBoundaries': flatten(unresolved) + list(limited.values()),
        'stackBoundaryChecks': flatten(stack_checks), 'unresolvedIndirectStackLayouts': flatten(stack_indirect),
        'nonArgumentImportLayouts':flatten(non_arguments),
        'stackProbeCount': len(stack_probes), 'analysisRuns': runs, 'converged': not pending and not limited,
        'pointerReturnSummaries': [dict(contextId=rows[k]['contextId'], value=list(v)) for k, v in return_summaries.items() if v],
        'method': 'saved image only; direct register/stack-argument closure with affine aliases; explicit node-field stores reloaded until next call; common pointer returns propagated to callers until fixed point; 64-bit pointers only; event union is not a whole-program non-use proof',
        'limits': ['node-field pointer aliases invalidated at every call', 'external escaped/reloaded aliases not resolved',
            'indexed node accesses retained as unresolved diagnostics', 'indirect stack layouts are candidates, not proven arguments',
            'optional SDK signatures filter only declared arguments, not unknown aliases or import side effects',
            'indirect targets not executed', 'unknown or differing returns not propagated', 'widened aliases not a proof of absence'],
        'maximumContexts': maximum}


def self_check():
    class BytesImage:
        def __init__(self, raw):
            self.raw = raw
            self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
            self.md.detail = True
        def flow(self, start): return list(self.md.disasm(self.raw, start))
        def flowtext(self, start):
            return '\n'.join(f'{i.address:X} {i.mnemonic} {i.op_str}' for i in self.flow(start))
    cases = [
        ('direct node read', '8B8180000000C3', 1),
        ('subobject read', '488D41688B4818C3', 1),
        ('vtable slot', '488B01488B9080000000C3', 0),
        ('32-bit zero extension discards pointer', '4889C889C08B9080000000C3', 0),
        ('stack spill reload', '48894C2408488B4424088B9080000000C3', 1),
        ('narrow stack overwrite', '48894C2408C644240900488B4424088B9080000000C3', 0),
        ('low byte write discards affine pointer', '4889C8B0018B9080000000C3', 0),
        ('node-field pointer store and reload', '488D416848894178488B51788B4218C3', 1),
        ('call invalidates node-field pointer', '4889CB488D416848894378E800000000488B53788B4218C3', 0),
        ('incoming fifth argument pointer', '488B4424288B8880000000C3', 1),
        ('overwrite incoming pointer before read', '48C744242800000000488B4424288B8880000000C3', 0),
    ]
    for label, code, expected in cases:
        result = audit(BytesImage(bytes.fromhex(code)), 0x1000,{'rcx':('node',0),'@40':('node',0)})
        actual = sum(e['kind'] != 'virtualSlot' for e in result['accesses'])
        if actual != expected: raise AssertionError((label, actual, expected, result))
    return {'cases': len(cases), 'passed': len(cases),
        'method': 'synthetic instruction sequences test the static analyzer only; no game functions executed'}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--max-contexts', type=int, default=256)
    parser.add_argument('--self-check', action='store_true')
    parser.add_argument('--output', type=Path)
    parser.add_argument('--sdk-header',type=Path)
    args = parser.parse_args()
    checks = self_check()
    if args.self_check:
        print(json.dumps(checks)); return
    im = Image()
    signatures=None
    if args.sdk_header:
        from ai_import_signatures import load
        signatures=load(args.sdk_header)
    result = {'utc': datetime.now(timezone.utc).isoformat(), 'imageSha256': hashlib.sha256(im.img).hexdigest(),
        'analyzerSha256': {name:hashlib.sha256((HERE/name).read_bytes()).hexdigest() for name in ('ai_attack_access.py','ai_attack_boundaries.py')},
        'analyzerChecks': checks, **closure(im, args.max_contexts, import_signatures=signatures)}
    if signatures:result['importSignatureAnalyzerSha256']=hashlib.sha256((HERE/'ai_import_signatures.py').read_bytes()).hexdigest()
    path = args.output or HERE / '.cache' / 'ai_attack_boundaries' / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(len(result['contexts']), 'contexts;', len(result['callEdges']), 'call edges;',
        len(result['unresolvedBoundaries']), 'unresolved boundaries;', path)
    for row in result['contexts']:
        if row['accesses']: print(hex(row['entry']), row['arguments'], row['accesses'])


if __name__ == '__main__': main()

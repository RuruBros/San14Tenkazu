"""Capture and compare native formula bindings without opening the game process."""
import argparse
import ast
import bisect
import collections
import gzip
import hashlib
import json
import pickle
import struct
from pathlib import Path

import capstone as cs
from capstone.x86_const import X86_OP_IMM, X86_OP_MEM, X86_REG_RIP

HERE = Path(__file__).resolve().parent
SCHEMA = 1
FINGERPRINT = 'x64-flow-bytes-v1'
RULE_RANGE = (0x1A47C00, 0x1A4A800)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def json_bytes(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode('utf-8')


def read_json(path):
    path = Path(path)
    data = gzip.decompress(path.read_bytes()) if path.suffix == '.gz' else path.read_bytes()
    return json.loads(data)


def write_json(path, value):
    path = Path(path)
    data = json_bytes(value) + b'\n'
    if path.suffix == '.gz': data = gzip.compress(data, mtime=0)
    path.parent.mkdir(parents=True, exist_ok=True)
    # Baselines and reports are immutable unless the caller deliberately chooses a new path.
    with path.open('xb') as stream: stream.write(data)


class MemoryImage:
    """A virtual-address-layout PE32+ dump; disk PE files are deliberately rejected."""
    def __init__(self, data, base, rule_range=RULE_RANGE):
        self.data, self.base, self.rule_range = data, base, rule_range
        if data[:2] != b'MZ': raise ValueError('Not a PE memory image')
        pe = struct.unpack_from('<I', data, 0x3C)[0]
        if data[pe:pe+4] != b'PE\0\0' or struct.unpack_from('<H', data, pe+4)[0] != 0x8664:
            raise ValueError('An x64 PE memory image is required')
        opt = pe+24
        if struct.unpack_from('<H', data, opt)[0] != 0x20B: raise ValueError('PE32+ is required')
        self.size = struct.unpack_from('<I', data, opt+56)[0]
        if self.size != len(data): raise ValueError('Image must contain the complete virtual image, not a disk PE')
        n, opt_size = struct.unpack_from('<H', data, pe+6)[0], struct.unpack_from('<H', data, pe+20)[0]
        self.sections = []
        for i in range(n):
            at = opt+opt_size+i*40
            name = data[at:at+8].rstrip(b'\0').decode('ascii')
            size, rva = struct.unpack_from('<II', data, at+8)
            flags = struct.unpack_from('<I', data, at+36)[0]
            if rva+size > len(data): raise ValueError('Section exceeds image')
            self.sections.append((name, rva, size, flags))
        self.code = [(rva, rva+size) for name, rva, size, flags in self.sections if flags & 0x20000000]
        if not self.code: raise ValueError('No executable section')
        self.relocations, self.relocation_issues = set(), []
        rr, length = struct.unpack_from('<II', data, opt+112+5*8)
        if rr+length > len(data): raise ValueError('Relocation directory exceeds image')
        at = rr
        while at < rr+length:
            page, size = struct.unpack_from('<II', data, at)
            if not page and not size: break
            if size < 8 or size % 2 or at+size > rr+length: raise ValueError('Invalid relocation block')
            for off in range(at+8, at+size, 2):
                entry = struct.unpack_from('<H', data, off)[0]
                kind, site = entry >> 12, page+(entry & 0xFFF)
                if kind == 10:
                    if site+8 > len(data): raise ValueError('Relocation exceeds image')
                    self.relocations.add(site)
                elif kind: self.relocation_issues.append([kind, site])
            at += size
        self.relocation_sites = sorted(self.relocations)
        self.iat = struct.unpack_from('<II', data, opt+112+12*8)
        if self.iat[0]+self.iat[1] > len(data): raise ValueError('Import address table exceeds image')
        pr, size = struct.unpack_from('<II', data, opt+112+3*8)
        if pr+size > len(data) or size % 12: raise ValueError('Invalid exception directory')
        self.functions = sorted({(b, e) for b, e, unwind in struct.iter_unpack('<III', data[pr:pr+size])
                                 if b and b < e and self.is_code(b) and e <= len(data)})
        self.starts = {b for b, e in self.functions}
        self.ends = dict(self.functions)
        self.md = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
        self.md.detail = True
        self.lite = cs.Cs(cs.CS_ARCH_X86, cs.CS_MODE_64)
        self.cache = {}

    @classmethod
    def load(cls, path, base, rule_range=RULE_RANGE):
        return cls(Path(path).read_bytes(), base, rule_range)

    def is_code(self, rva):
        return any(b <= rva < e for b, e in self.code)

    def section(self, rva):
        return next((name for name, b, size, flags in self.sections if b <= rva < b+size), None)

    def canonical(self, rva, size, base=None, data=None):
        # Only PE DIR64 sites establish that a value is an address; ordinary constants are preserved.
        result = bytearray(self.data[rva:rva+size] if data is None else data)
        if len(result) != size: raise ValueError('Incomplete reference bytes')
        base = self.base if base is None else base
        left = bisect.bisect_left(self.relocation_sites, rva)
        right = bisect.bisect_right(self.relocation_sites, rva+size-8)
        for site in self.relocation_sites[left:right]:
            value = struct.unpack_from('<Q', result, site-rva)[0]
            if base <= value < base+self.size: struct.pack_into('<Q', result, site-rva, value-base)
        return bytes(result)

    def identity(self):
        sections = [[name, rva, size, digest(self.canonical(rva, size))]
                    for name, rva, size, flags in self.sections if flags & 0x20000000]
        return {'memory_image_sha256': digest(self.data), 'code_sha256': digest(json_bytes(sections)),
                'size': self.size, 'base': self.base, 'code_sections': sections,
                'relocation_issues': self.relocation_issues}

    def normalized_instruction(self, ins):
        data = bytearray(self.canonical(ins.address, ins.size))
        control = ins.group(cs.CS_GRP_CALL) or ins.group(cs.CS_GRP_JUMP)
        if control and ins.operands and ins.operands[0].type == X86_OP_IMM:
            data[ins.imm_offset:ins.imm_offset+ins.imm_size] = bytes(ins.imm_size)
        if any(op.type == X86_OP_MEM and op.mem.base == X86_REG_RIP for op in ins.operands):
            data[ins.disp_offset:ins.disp_offset+ins.disp_size] = bytes(ins.disp_size)
        # Absolute relocated immediates also need address-independent candidate fingerprints.
        for site in self.relocations.intersection(range(ins.address, ins.address+ins.size)):
            if site+8 <= ins.address+ins.size: data[site-ins.address:site-ins.address+8] = bytes(8)
        return data.hex()

    def prefix(self, rva):
        parts = []
        for ins in self.md.disasm(self.data[rva:rva+128], rva):
            parts.append(self.normalized_instruction(ins))
            if len(parts) == 8 or ins.group(cs.CS_GRP_RET) or ins.mnemonic in ('jmp', 'int3'): break
        return digest(json_bytes(parts))

    def describe(self, start):
        if start in self.cache: return self.cache[start]
        if not self.is_code(start): raise ValueError(f'Function outside executable sections: {start:#x}')
        todo, instructions, issues = [start], {}, set()
        while todo:
            at = todo.pop()
            while at not in instructions:
                if not self.is_code(at) or abs(at-start) >= 0x20000 or len(instructions) >= 10000:
                    issues.add('flow-boundary'); break
                ins = next(self.md.disasm(self.data[at:at+16], at, count=1), None)
                if ins is None: issues.add('undecodable-instruction'); break
                instructions[at] = ins
                if ins.group(cs.CS_GRP_RET) or ins.mnemonic in ('int3', 'ud2'): break
                if ins.group(cs.CS_GRP_JUMP):
                    if not ins.operands or ins.operands[0].type != X86_OP_IMM:
                        issues.add('indirect-jump'); break
                    target = ins.operands[0].imm
                    if ins.mnemonic == 'jmp':
                        if target in self.starts and target != start: break
                        at = target; continue
                    todo.append(target)
                at += ins.size
        ordered = sorted(instructions)
        indices = {at: i for i, at in enumerate(ordered)}
        shape, edges, references = [], [], []
        for i, at in enumerate(ordered):
            ins = instructions[at]
            token = [self.normalized_instruction(ins)]
            if ins.group(cs.CS_GRP_CALL) or ins.group(cs.CS_GRP_JUMP):
                if ins.operands and ins.operands[0].type == X86_OP_IMM:
                    target = ins.operands[0].imm
                    if ins.group(cs.CS_GRP_CALL) or target not in indices:
                        edges.append({'instruction': i, 'kind': 'call' if ins.group(cs.CS_GRP_CALL) else 'tail', 'rva': target})
                        token.append('external-control')
                    else: token.append(['branch', indices[target]])
                else: issues.add('indirect-call' if ins.group(cs.CS_GRP_CALL) else 'indirect-jump')
            for j, op in enumerate(ins.operands):
                if op.type != X86_OP_MEM or op.mem.base != X86_REG_RIP: continue
                target, size = at+ins.size+op.mem.disp, op.size
                section = self.section(target)
                address = ins.mnemonic == 'lea'
                imported = self.iat[0] <= target < self.iat[0]+self.iat[1]
                role = ('address' if address else 'runtime' if imported else 'rule' if self.rule_range[0] <= target < self.rule_range[1] else
                        'readonly' if section in ('.rdata', '_RDATA') else 'runtime')
                data = None if address or not 0 <= target <= len(self.data)-size else self.canonical(target, size).hex()
                references.append({'instruction': i, 'operand': j, 'rva': target, 'size': size,
                                   'role': role, 'section': section, 'bytes': data})
                if address: issues.add('address-reference-extent')
            for site in self.relocations.intersection(range(at, at+ins.size)):
                if site+8 <= at+ins.size:
                    value = struct.unpack_from('<Q', self.data, site)[0]
                    if self.base <= value < self.base+self.size:
                        target = value-self.base
                        references.append({'instruction': i, 'operand': -1, 'rva': target, 'size': 8,
                                           'role': 'address', 'section': self.section(target), 'bytes': None})
                        issues.add('address-reference-extent')
            shape.append(token)
        if not ordered: raise ValueError(f'Empty function: {start:#x}')
        row = {'rva': start, 'instruction_count': len(ordered), 'prefix_sha256': self.prefix(start),
               'shape_sha256': digest(json_bytes(shape)),
               'raw_code_sha256': digest(json_bytes([[at-start, instructions[at].bytes.hex()] for at in ordered])),
               'direct_edges': edges, 'references': references, 'issues': sorted(issues)}
        self.cache[start] = row
        return row

    def candidates(self, wanted, progress=None):
        # Discover leaf entries from real direct transfers, then use short prefixes before full flow decoding.
        starts = set(self.starts)
        for b, e in self.functions:
            for at, size, mnemonic, operands in self.lite.disasm_lite(self.data[b:e], b):
                if mnemonic in ('call', 'jmp') and operands.startswith('0x'):
                    target = int(operands, 16)
                    if self.is_code(target): starts.add(target)
        result = collections.defaultdict(list)
        for i, start in enumerate(sorted(starts)):
            prefix = self.prefix(start)
            if prefix in wanted: result[prefix].append(start)
            if progress and (i+1) % 10000 == 0: progress(f'candidate prefixes: {i+1}/{len(starts)}')
        return result


class CoverRegistry:
    """Read literal COVER declarations and local constants through AST; never import vector modules."""
    def __init__(self, directory):
        self.directory, self.environments, self.loading = Path(directory), {}, set()

    def value(self, node, env):
        if isinstance(node, ast.Constant): return node.value
        if isinstance(node, ast.Name): return env[node.id]
        if isinstance(node, (ast.List, ast.Tuple, ast.Set)):
            out = []
            for item in node.elts:
                if isinstance(item, ast.Starred): out.extend(self.value(item.value, env))
                else: out.append(self.value(item, env))
            return out
        if isinstance(node, ast.Dict): return {self.value(k, env): self.value(v, env) for k, v in zip(node.keys, node.values)}
        if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Add): return self.value(node.left, env)+self.value(node.right, env)
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) and node.func.attr == 'values' and not node.args and not node.keywords:
            value = self.value(node.func.value, env)
            if isinstance(value, dict): return list(value.values())
        raise ValueError('Nonliteral COVER expression')

    def load(self, module):
        if module in self.environments: return self.environments[module]
        if module in self.loading: raise ValueError(f'Cyclic constant imports: {module}')
        path = self.directory / (module+'.py')
        if not path.is_file(): return {}
        self.loading.add(module); env = {}
        tree = ast.parse(path.read_text(encoding='utf-8-sig'), filename=str(path))
        try:
            for node in tree.body:
                if isinstance(node, ast.ImportFrom) and node.module and '.' not in node.module:
                    if (self.directory / (node.module+'.py')).is_file():
                        imported = self.load(node.module)
                        for name in node.names:
                            if name.name == '*': env.update({k: v for k, v in imported.items() if not k.startswith('_')})
                            elif name.name in imported: env[name.asname or name.name] = imported[name.name]
                elif isinstance(node, ast.Assign):
                    try: value = self.value(node.value, env)
                    except (ValueError, KeyError, TypeError):
                        if any(isinstance(t, ast.Name) and t.id == 'COVER' for t in node.targets):
                            raise ValueError(f'Cannot resolve COVER in {path.name}')
                        continue
                    for target in node.targets:
                        if isinstance(target, ast.Name): env[target.id] = value
                        elif isinstance(target, (ast.Tuple, ast.List)):
                            for name, item in zip(target.elts, value):
                                if isinstance(name, ast.Name): env[name.id] = item
            self.environments[module] = env
            return env
        finally: self.loading.remove(module)

    def modules(self, names=None):
        result = {}
        for path in sorted(self.directory.glob('*.py')):
            if names and path.stem not in names: continue
            tree = ast.parse(path.read_text(encoding='utf-8-sig'))
            if not any(isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'COVER' for t in node.targets)
                       for node in tree.body): continue
            env = self.load(path.stem)
            if 'COVER' in env:
                values = env['COVER']
                if not values or any(type(v) is not int or v < 0 for v in values): raise ValueError(f'Invalid COVER: {path.name}')
                result[path.stem] = sorted(set(values))
        if names and set(names)-result.keys(): raise ValueError('Unknown modules: '+', '.join(sorted(set(names)-result.keys())))
        return result


def snapshot_evidence(image, path):
    # Only locally produced Oracle.save_pages dumps should be supplied here.
    raw = Path(path).read_bytes()
    saved = pickle.loads(raw)
    if saved['exe_size'] != image.size: raise ValueError(f'Snapshot image size differs: {path}')
    checked, ranges = 0, []
    for page, data in sorted(saved['pages'].items()):
        rva = page-saved['exe']
        for b, e in image.code:
            lo, hi = max(rva, b), min(rva+len(data), e)
            if lo < hi:
                checked += 1
                if ranges and ranges[-1][0]+len(ranges[-1][1]) == lo: ranges[-1][1].extend(data[lo-rva:hi-rva])
                else: ranges.append((lo, bytearray(data[lo-rva:hi-rva])))
    for lo, actual in ranges:
        normalized = image.canonical(lo, len(actual), saved['exe'], actual)
        if normalized != image.canonical(lo, len(actual)): raise ValueError(f'Snapshot code differs: {Path(path).name} at {lo:#x}')
    if not checked: raise ValueError('Snapshot contains no executable code pages')
    return {'name': Path(path).name, 'sha256': digest(raw), 'matching_code_page_intersections': checked,
            'scope': 'Captured code pages only; not a complete build identity or a vector-to-snapshot binding.'}


def assets(vectors):
    result = {}
    for directory, prefix, pattern in [(HERE.parent/'Core'/'Formulas', 'Core/Formulas', '*.cs'),
                                        (HERE.parent/'Tests'/'Formulas', 'Tests/Formulas', '*.cs'),
                                        (HERE/'vectors', 'Reverse/vectors', '*.py'),
                                        (Path(vectors), 'Tests/Vectors', '*.json.gz')]:
        for path in sorted(directory.glob(pattern)): result[prefix+'/'+path.name] = digest(path.read_bytes())
    for name in ['battle.md', 'bugs.md', 'recruit.md', 'diplomacy.md']:
        path = HERE/'notes'/name
        if path.is_file(): result['Reverse/notes/'+name] = digest(path.read_bytes())
    return result


def capture(image, modules, max_functions=10000, captured_assets=None, snapshots=None, progress=None):
    if not modules: raise ValueError('No COVER modules selected')
    roots = {rva for values in modules.values() for rva in values}
    if len(roots) > max_functions: raise ValueError('Function limit is smaller than the root set')
    pending, rows = collections.deque(sorted(roots)), {}
    queued = set(roots)
    while pending and len(rows) < max_functions:
        rva = pending.popleft(); row = image.describe(rva); rows[str(rva)] = row
        for edge in row['direct_edges']:
            target = edge['rva']
            if image.is_code(target) and target not in queued: pending.append(target); queued.add(target)
        if progress and len(rows) % 500 == 0: progress(f'captured functions: {len(rows)}, pending: {len(pending)}')
    return {'schema': SCHEMA, 'kind': 'native-formula-binding-catalog', 'image': image.identity(),
            'fingerprint': FINGERPRINT, 'decoder': {'name': 'capstone', 'version': cs.__version__},
            'rule_range': list(image.rule_range), 'modules': modules, 'functions': rows,
            'uncaptured_direct_targets': sorted(pending), 'assets': captured_assets or {},
            'snapshot_evidence': snapshots or [],
            'validation': 'Catalog only. No new version is oracle-verified by capture or compare.'}


def compare(old, image, mapping=None, progress=None):
    if old.get('schema') != SCHEMA: raise ValueError('Unsupported manifest schema')
    if old.get('fingerprint') != FINGERPRINT or old.get('decoder') != {'name': 'capstone', 'version': cs.__version__}:
        raise ValueError('Fingerprint algorithm or decoder version differs; regenerate the catalog with the original image')
    rows = old['functions']; explicit = mapping or {}
    if set(explicit)-rows.keys(): raise ValueError('Mapping contains functions outside the manifest')
    if len(set(explicit.values())) != len(explicit): raise ValueError('Mapping assigns multiple old functions to one new entry')
    identical_code = image.identity()['code_sha256'] == old['image']['code_sha256']
    candidates = {} if identical_code else image.candidates({row['prefix_sha256'] for row in rows.values()}, progress)
    matched, results = {}, {}
    for key, row in rows.items():
        if key in explicit:
            target = explicit[key]
            if not image.is_code(target): raise ValueError(f'Mapped target outside executable sections: {target:#x}')
            options, source = [target], 'explicit'
        elif identical_code:
            options, source = [row['rva']], 'identical-code-image'
        else:
            options = [rva for rva in candidates.get(row['prefix_sha256'], [])
                       if image.describe(rva)['shape_sha256'] == row['shape_sha256']]
            source = 'shape-candidates'
        result = {'old_rva': row['rva'], 'candidates': options, 'match_source': source, 'changes': [], 'review': [],
                  'reference_differences': [], 'direct_edge_differences': []}
        results[key] = result
        if len(options) != 1:
            result['status'] = 'ambiguous' if options else 'unmatched'; continue
        target = options[0]; matched[key] = target; new = image.describe(target)
        result['new_rva'] = target
        if new['shape_sha256'] != row['shape_sha256']: result['changes'].append('instruction-or-control-flow')
        result['review'] = sorted(set(row['issues']+new['issues']))
        if len(row['references']) != len(new['references']): result['changes'].append('reference-count')
        else:
            for a, b in zip(row['references'], new['references']):
                if (a['instruction'], a['operand'], a['size']) != (b['instruction'], b['operand'], b['size']):
                    result['changes'].append('reference-layout'); continue
                if a['role'] in ('rule', 'readonly') and a['bytes'] != b['bytes']:
                    result['changes'].append(a['role']+'-bytes')
                elif a['role'] == 'runtime' and a['bytes'] != b['bytes']:
                    result['review'].append('runtime-input-bytes')
                if a['bytes'] != b['bytes'] or a['rva'] != b['rva']:
                    result['reference_differences'].append({'old': a, 'new': b})
                if a['section'] != b['section']: result['review'].append('reference-section')
        if len(row['direct_edges']) != len(new['direct_edges']): result['changes'].append('direct-edge-count')
    # A unique shape does not establish identity when two old functions select the same new function.
    inverse = collections.defaultdict(list)
    for key, target in matched.items(): inverse[target].append(key)
    for target, keys in inverse.items():
        if len(keys) > 1:
            for key in keys:
                matched.pop(key); results[key].pop('new_rva', None); results[key]['status'] = 'mapping-collision'
    for key, target in matched.items():
        for a, b in zip(rows[key]['direct_edges'], image.describe(target)['direct_edges']):
            dependency = str(a['rva'])
            if dependency in results and dependency not in matched and (a['instruction'], a['kind']) == (b['instruction'], b['kind']):
                results[dependency].setdefault('call_site_hints', []).append(
                    {'old_caller': int(key), 'new_caller': target, 'instruction': a['instruction'], 'new_target': b['rva']})
    reverse = collections.defaultdict(set)
    for key, row in rows.items():
        result = results[key]
        if key not in matched: continue
        new = image.describe(matched[key])
        for a, b in zip(row['direct_edges'], new['direct_edges']):
            dependency = str(a['rva']); reverse[dependency].add(key)
            if (a['instruction'], a['kind']) != (b['instruction'], b['kind']): result['changes'].append('direct-edge-layout')
            if dependency not in rows: result['review'].append('uncaptured-or-external-direct-target')
            elif dependency not in matched: result['review'].append('unresolved-direct-target')
            elif matched[dependency] != b['rva']: result['changes'].append('direct-call-target')
            if dependency in matched and matched[dependency] != b['rva']:
                result['direct_edge_differences'].append({'old': a, 'new': b, 'mapped_old_target': matched[dependency]})
        result['changes'] = sorted(set(result['changes'])); result['review'] = sorted(set(result['review']))
        result['status'] = 'changed' if result['changes'] else 'candidate-with-review' if result['review'] else 'static-candidate'
    affected = {key for key, row in results.items() if row['status'] not in ('static-candidate', 'candidate-with-review')}
    # Include all old edges, including unresolved callers, in transitive impact propagation.
    for key, row in rows.items():
        for edge in row['direct_edges']: reverse[str(edge['rva'])].add(key)
    pending = list(affected)
    while pending:
        for caller in reverse[pending.pop()]:
            if caller not in affected: affected.add(caller); pending.append(caller)
    review = {key for key, row in results.items() if row['status'] != 'static-candidate'}
    pending = list(review)
    while pending:
        for caller in reverse[pending.pop()]:
            if caller not in review: review.add(caller); pending.append(caller)
    modules = {name: {'affected_roots': [rva for rva in roots if str(rva) in affected],
                      'review_roots': [rva for rva in roots if str(rva) in review],
                      'oracle_replay_required': True} for name, roots in old['modules'].items()}
    return {'schema': SCHEMA, 'kind': 'native-formula-version-difference', 'old_code_sha256': old['image']['code_sha256'],
            'new_image': image.identity(), 'functions': results, 'modules': modules,
            'counts': dict(collections.Counter(row['status'] for row in results.values())),
            'affected_functions': sorted(int(key) for key in affected),
            'validation': 'Static candidates and observed differences only. Every module still requires a fresh-version oracle replay.'}


def bundle(directory, manifest, image, vectors, snapshots):
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=False)
    def save(relative, data):
        target = directory/relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open('xb') as stream: stream.write(data)
    save('image.bin', image.data)
    save('image.meta', f'{image.base:#x}\n'.encode())
    for label, expected in manifest['assets'].items():
        path = Path(vectors)/Path(label).name if label.startswith('Tests/Vectors/') else HERE.parent/label
        data = path.read_bytes()
        if digest(data) != expected: raise ValueError('Asset changed during capture: '+label)
        save('assets/'+label, data)
    for path, evidence in zip(snapshots, manifest['snapshot_evidence']):
        data = Path(path).read_bytes()
        if digest(data) != evidence['sha256']: raise ValueError('Snapshot changed during capture: '+str(path))
        save('snapshots/'+Path(path).name, data)
    # This completion marker is written only after every artifact passed its recorded hash.
    write_json(directory/'manifest.json.gz', manifest)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['capture', 'compare'])
    parser.add_argument('--image', type=Path, default=HERE/'.cache'/'image.bin')
    parser.add_argument('--base', type=lambda s: int(s, 0))
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--manifest', type=Path)
    parser.add_argument('--mapping', type=Path, help='Explicit old/new RVA JSON object; decimal or 0x keys and values')
    parser.add_argument('--modules', nargs='+')
    parser.add_argument('--vectors', type=Path, default=HERE.parent/'Tests'/'Vectors')
    parser.add_argument('--max-functions', type=int, default=10000)
    parser.add_argument('--frozen', type=Path, action='append', default=[])
    parser.add_argument('--exe-file', type=Path, help='Optional disk exe artifact hash; association is supplied by the caller')
    parser.add_argument('--bundle', type=Path, help='New immutable artifact directory for capture')
    args = parser.parse_args()
    if args.out.exists(): parser.error('Output already exists; choose a new immutable output path')
    if args.bundle and (args.command != 'capture' or args.bundle.exists()): parser.error('--bundle requires capture and a new directory')
    base = args.base
    if base is None:
        if args.image.resolve() != (HERE/'.cache'/'image.bin').resolve(): parser.error('--base is required for a custom memory image')
        base = int((HERE/'.cache'/'image.meta').read_text().split()[0], 0)
    image = MemoryImage.load(args.image, base)
    progress = lambda message: print(message, flush=True)
    if args.command == 'capture':
        producer = digest(Path(__file__).read_bytes())
        registry = CoverRegistry(HERE/'vectors').modules(args.modules)
        evidence = [snapshot_evidence(image, path) for path in args.frozen]
        output = capture(image, registry, args.max_functions, assets(args.vectors), evidence, progress)
        output['producer_sha256'] = producer
        output['disk_exe_artifact'] = ({'name': args.exe_file.name, 'sha256': digest(args.exe_file.read_bytes()),
                                       'association': 'Caller supplied; encrypted disk code is not equated with the memory dump.'}
                                      if args.exe_file else None)
        if args.bundle: bundle(args.bundle, output, image, args.vectors, args.frozen)
    else:
        if not args.manifest: parser.error('compare requires --manifest')
        mapping = None
        if args.mapping:
            raw = read_json(args.mapping)
            mapping = {str(int(k, 0)): int(v, 0) if isinstance(v, str) else v for k, v in raw.items()}
        output = compare(read_json(args.manifest), image, mapping, progress)
    write_json(args.out, output)
    print(json.dumps({'out': str(args.out), 'modules': len(output['modules']),
                      'functions': len(output['functions']), 'counts': output.get('counts', {}),
                      'uncaptured_direct_targets': len(output.get('uncaptured_direct_targets', []))}, ensure_ascii=False), flush=True)


if __name__ == '__main__': main()

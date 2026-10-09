"""Exercise version matching with independently constructed x64 PE memory images."""
import pickle
import contextlib
import shutil
import struct
import unittest
import uuid
from pathlib import Path

from version_audit import CoverRegistry, MemoryImage, bundle, capture, compare, digest, read_json, snapshot_evidence, write_json

BASE = 0x140000000


@contextlib.contextmanager
def temporary():
    cache = Path(__file__).resolve().parent/'.cache'/'version_audit_tests'
    cache.mkdir(parents=True, exist_ok=True)
    target = cache/uuid.uuid4().hex
    target.mkdir()
    try: yield str(target)
    finally:
        resolved = target.resolve(strict=True)
        if not resolved.is_relative_to(cache.resolve()): raise ValueError('Test directory escaped the cache')
        shutil.rmtree(resolved)


def pe_image(functions, values=None, base=BASE, relocations=(), iat=(0, 0)):
    data = bytearray(0x6000)
    data[:2] = b'MZ'; struct.pack_into('<I', data, 0x3C, 0x80)
    data[0x80:0x84] = b'PE\0\0'
    struct.pack_into('<HH', data, 0x84, 0x8664, 5)
    struct.pack_into('<H', data, 0x94, 0xF0)
    opt = 0x98
    struct.pack_into('<H', data, opt, 0x20B)
    struct.pack_into('<Q', data, opt+24, BASE)
    struct.pack_into('<I', data, opt+56, len(data))
    struct.pack_into('<II', data, opt+112+3*8, 0x4000, len(functions)*12)
    struct.pack_into('<II', data, opt+112+12*8, *iat)
    sections = [('.text', 0x1000, 0x60000020), ('.rdata', 0x2000, 0x40000040),
                ('.data', 0x3000, 0xC0000040), ('.pdata', 0x4000, 0x40000040), ('.reloc', 0x5000, 0x42000040)]
    for i, (name, rva, flags) in enumerate(sections):
        at = opt+0xF0+i*40
        data[at:at+len(name)] = name.encode()
        struct.pack_into('<II', data, at+8, 0x1000, rva)
        struct.pack_into('<I', data, at+36, flags)
    for i, (rva, code) in enumerate(sorted(functions.items())):
        data[rva:rva+len(code)] = code
        struct.pack_into('<III', data, 0x4000+i*12, rva, rva+len(code), 0)
    for rva, value in (values or {}).items(): struct.pack_into('<I', data, rva, value)
    blocks = {}
    for site in relocations: blocks.setdefault(site & ~0xFFF, []).append(0xA000+(site & 0xFFF))
    at = 0x5000
    for page, entries in sorted(blocks.items()):
        if len(entries) % 2: entries.append(0)
        size = 8+2*len(entries)
        struct.pack_into('<II', data, at, page, size)
        struct.pack_into('<'+'H'*len(entries), data, at+8, *entries)
        at += size
    struct.pack_into('<II', data, opt+112+5*8, 0x5000 if relocations else 0, at-0x5000)
    return MemoryImage(bytes(data), base, rule_range=(0x3000, 0x3010))


def call(start, target): return b'\xe8'+struct.pack('<i', target-start-5)+b'\xc3'
def load(start, target): return b'\x8b\x05'+struct.pack('<i', target-start-6)+b'\xc3'
def literal(value): return b'\xb8'+struct.pack('<I', value)+b'\xc3'


class VersionAuditTests(unittest.TestCase):
    def baseline(self, image, limit=10000): return capture(image, {'formula': [0x1000]}, max_functions=limit)

    def test_moved_function_and_rule_reference_are_candidates(self):
        old = pe_image({0x1000: call(0x1000, 0x1100), 0x1100: load(0x1100, 0x3000)}, {0x3000: 7})
        new = pe_image({0x1200: call(0x1200, 0x1300), 0x1300: load(0x1300, 0x3040)}, {0x3040: 7})
        report = compare(self.baseline(old), new)
        self.assertEqual(report['functions']['4096']['new_rva'], 0x1200)
        self.assertEqual(report['functions']['4352']['new_rva'], 0x1300)
        self.assertEqual(report['functions']['4096']['status'], 'static-candidate')
        self.assertEqual(report['affected_functions'], [])
        self.assertTrue(report['modules']['formula']['oracle_replay_required'])

    def test_rule_change_propagates_to_unchanged_caller(self):
        functions = {0x1000: call(0x1000, 0x1100), 0x1100: load(0x1100, 0x3000)}
        report = compare(self.baseline(pe_image(functions, {0x3000: 7})), pe_image(functions, {0x3000: 8}))
        self.assertEqual(report['functions']['4096']['changes'], [])
        self.assertEqual(report['functions']['4352']['changes'], ['rule-bytes'])
        self.assertEqual(report['modules']['formula']['affected_roots'], [0x1000])

    def test_direct_call_target_change_is_detected_despite_identical_shape(self):
        functions = {0x1000: call(0x1000, 0x1100), 0x1100: literal(1), 0x1120: literal(2)}
        new = dict(functions); new[0x1000] = call(0x1000, 0x1120)
        report = compare(self.baseline(pe_image(functions)), pe_image(new))
        self.assertIn('direct-call-target', report['functions']['4096']['changes'])
        self.assertEqual(report['modules']['formula']['affected_roots'], [0x1000])

    def test_duplicates_are_ambiguous_even_at_the_old_rva(self):
        old = pe_image({0x1000: literal(1)})
        new = pe_image({0x1000: literal(1), 0x1200: literal(1)})
        report = compare(self.baseline(old), new)
        self.assertEqual(report['functions']['4096']['status'], 'ambiguous')
        self.assertEqual(report['functions']['4096']['candidates'], [0x1000, 0x1200])

    def test_changed_field_offset_requires_explicit_mapping_and_is_not_normalized(self):
        old = pe_image({0x1000: b'\x8b\x41\x10\xc3'})
        new = pe_image({0x1200: b'\x8b\x41\x14\xc3'})
        baseline = self.baseline(old)
        self.assertEqual(compare(baseline, new)['functions']['4096']['status'], 'unmatched')
        mapped = compare(baseline, new, {'4096': 0x1200})
        self.assertEqual(mapped['functions']['4096']['changes'], ['instruction-or-control-flow'])

    def test_indirect_virtual_call_is_an_explicit_review_boundary(self):
        image = pe_image({0x1000: b'\xff\x50\x10\xc3'})
        report = compare(self.baseline(image), image)
        self.assertIn('indirect-call', report['functions']['4096']['review'])
        self.assertEqual(report['modules']['formula']['review_roots'], [0x1000])

    def test_mutable_globals_are_input_review_not_rule_changes(self):
        functions = {0x1000: load(0x1000, 0x3100)}
        report = compare(self.baseline(pe_image(functions, {0x3100: 1})), pe_image(functions, {0x3100: 2}))
        self.assertEqual(report['functions']['4096']['changes'], [])
        self.assertIn('runtime-input-bytes', report['functions']['4096']['review'])

    def test_aslr_is_normalized_only_at_real_dir64_sites(self):
        make = lambda base: pe_image({0x1000: b'\x48\xb8'+struct.pack('<Q', base+0x2020)+b'\xc3'},
                                     base=base, relocations=[0x1002])
        old, new = make(BASE), make(BASE+0x4000000)
        self.assertEqual(old.identity()['code_sha256'], new.identity()['code_sha256'])
        self.assertNotEqual(old.identity()['memory_image_sha256'], new.identity()['memory_image_sha256'])
        self.assertEqual(compare(self.baseline(old), new)['functions']['4096']['changes'], [])
        constants = pe_image({0x1000: b'\x48\xb8'+struct.pack('<Q', BASE+0x2020)+b'\xc3'})
        changed = pe_image({0x1000: b'\x48\xb8'+struct.pack('<Q', BASE+0x2021)+b'\xc3'})
        self.assertNotEqual(constants.describe(0x1000)['shape_sha256'], changed.describe(0x1000)['shape_sha256'])

    def test_incomplete_graph_is_visible_and_requires_review(self):
        image = pe_image({0x1000: call(0x1000, 0x1100), 0x1100: literal(1)})
        baseline = self.baseline(image, limit=1)
        self.assertEqual(baseline['uncaptured_direct_targets'], [0x1100])
        report = compare(baseline, image)
        self.assertEqual(report['modules']['formula']['review_roots'], [0x1000])
        self.assertTrue(report['modules']['formula']['oracle_replay_required'])

    def test_mapping_cannot_collapse_distinct_old_entries(self):
        image = pe_image({0x1000: call(0x1000, 0x1100), 0x1100: literal(1)})
        with self.assertRaisesRegex(ValueError, 'multiple old'):
            compare(self.baseline(image), image, {'4096': 0x1000, '4352': 0x1000})

    def test_automatic_collision_leaves_no_selected_rva(self):
        old = pe_image({0x1100: literal(1), 0x1120: literal(1)})
        new = pe_image({0x1200: literal(1)})
        baseline = capture(old, {'formula': [0x1100, 0x1120]})
        report = compare(baseline, new)
        for row in report['functions'].values():
            self.assertEqual(row['status'], 'mapping-collision')
            self.assertNotIn('new_rva', row)

    def test_disk_pe_and_unknown_schema_are_rejected(self):
        image = pe_image({0x1000: literal(1)})
        with self.assertRaisesRegex(ValueError, 'complete virtual image'): MemoryImage(image.data[:0x500], BASE)
        with self.assertRaisesRegex(ValueError, 'schema'): compare({'schema': 999}, image)

    def test_decoder_mismatch_requires_regenerating_the_catalog(self):
        image = pe_image({0x1000: literal(1)})
        baseline = self.baseline(image); baseline['decoder']['version'] = 'different'
        with self.assertRaisesRegex(ValueError, 'decoder version differs'): compare(baseline, image)

    def test_import_addresses_are_runtime_inputs_even_in_readonly_sections(self):
        code = b'\xff\x15'+struct.pack('<i', 0x2020-0x1006)+b'\xc3'
        old = pe_image({0x1000: code}, {0x2020: 1}, iat=(0x2020, 8))
        new = pe_image({0x1000: code}, {0x2020: 2}, iat=(0x2020, 8))
        report = compare(self.baseline(old), new)
        row = report['functions']['4096']
        self.assertEqual(row['changes'], [])
        self.assertIn('runtime-input-bytes', row['review'])
        self.assertEqual(row['reference_differences'][0]['old']['bytes'], '0100000000000000')

    def test_call_site_hint_does_not_automatically_bind_changed_function(self):
        old = pe_image({0x1000: call(0x1000, 0x1100), 0x1100: literal(1)})
        new = pe_image({0x1200: call(0x1200, 0x1300), 0x1300: literal(2)})
        report = compare(self.baseline(old), new)
        row = report['functions']['4352']
        self.assertEqual(row['status'], 'unmatched')
        self.assertNotIn('new_rva', row)
        self.assertEqual(row['call_site_hints'][0]['new_target'], 0x1300)

    def test_readonly_difference_includes_the_actual_bytes(self):
        functions = {0x1000: load(0x1000, 0x2000)}
        report = compare(self.baseline(pe_image(functions, {0x2000: 1})), pe_image(functions, {0x2000: 2}))
        row = report['functions']['4096']
        self.assertEqual(row['changes'], ['readonly-bytes'])
        self.assertEqual(row['reference_differences'][0]['new']['bytes'], '02000000')

    def test_bundle_contains_coherent_artifacts_and_refuses_overwrite(self):
        with temporary() as directory:
            root = Path(directory); image = pe_image({0x1000: literal(1)})
            manifest = self.baseline(image); target = root/'bundle'
            bundle(target, manifest, image, root, [])
            self.assertEqual((target/'image.bin').read_bytes(), image.data)
            self.assertEqual(read_json(target/'manifest.json.gz'), manifest)
            with self.assertRaises(FileExistsError): bundle(target, manifest, image, root, [])
            manifest['assets'] = {'Tests/Vectors/fixture.json.gz': digest(b'original')}
            (root/'fixture.json.gz').write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError, 'Asset changed'): bundle(root/'bad-bundle', manifest, image, root, [])
            self.assertFalse((root/'bad-bundle'/'manifest.json.gz').exists())

    def test_registry_resolves_imported_literals_without_executing_modules(self):
        with temporary() as directory:
            root = Path(directory)
            marker = root/'executed'
            (root/'helper.py').write_text('PHASES = [0x1100]\nCOVER = [0x9999]\n', encoding='utf-8')
            (root/'formula.py').write_text("from pathlib import Path\nfrom helper import PHASES\n"
                f"Path({str(marker)!r}).write_text('executed')\n"
                'FN, OTHER = 0x1000, 0x1200\nFUNCTIONS = {1: OTHER}\nCOVER = [FN, *PHASES, *FUNCTIONS.values()]\n', encoding='utf-8')
            (root/'wrapper.py').write_text('from helper import *\n', encoding='utf-8')
            values = CoverRegistry(root).modules()
            self.assertEqual(values['formula'], [0x1000, 0x1100, 0x1200])
            self.assertNotIn('wrapper', values)
            self.assertFalse(marker.exists())

    def test_nonliteral_cover_fails_instead_of_silently_dropping_a_module(self):
        with temporary() as directory:
            (Path(directory)/'bad.py').write_text('COVER = calculate_addresses()\n', encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'Cannot resolve COVER'): CoverRegistry(directory).modules()

    def test_immutable_json_and_snapshot_code_checks(self):
        with temporary() as directory:
            root = Path(directory); path = root/'baseline.json.gz'
            write_json(path, {'schema': 1}); self.assertEqual(read_json(path), {'schema': 1})
            with self.assertRaises(FileExistsError): write_json(path, {'schema': 2})
            old = pe_image({0x1000: literal(7)}, base=BASE)
            snapshot = root/'snapshot.pkl'
            snapshot.write_bytes(pickle.dumps({'exe': BASE, 'exe_size': old.size, 'pages': {BASE+0x1000: old.data[0x1000:0x2000]}}))
            evidence = snapshot_evidence(old, snapshot)
            self.assertEqual(evidence['matching_code_page_intersections'], 1)
            new = pe_image({0x1000: literal(8)})
            with self.assertRaisesRegex(ValueError, 'Snapshot code differs'): snapshot_evidence(new, snapshot)


if __name__ == '__main__': unittest.main()

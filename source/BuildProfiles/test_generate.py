"""Offline checks for definition drift and managed/native guard agreement."""
import copy
import json
import tempfile
import unittest
import shutil
import subprocess
from pathlib import Path

import generate


class GenerationTests(unittest.TestCase):
    def setUp(self):
        self.profile, self.digest = generate.load()

    def reject(self, mutate):
        profile = copy.deepcopy(self.profile)
        mutate(profile)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'profile.json'
            path.write_text(json.dumps(profile), encoding='utf-8')
            with self.assertRaises((ValueError, KeyError)):
                generate.load(path)

    def test_outputs_are_byte_exact_and_repeatable(self):
        first = generate.outputs(self.profile, self.digest)
        self.assertEqual(first, generate.outputs(self.profile, self.digest))
        for path, text in first.items():
            self.assertEqual((generate.SOURCE / path).read_bytes(), text.encode(), path)

    def test_both_bridges_receive_identical_profile_identity_and_facts(self):
        files = generate.outputs(self.profile, self.digest)
        self.assertEqual(files['Seasons/Native/GameBuildFacts.g.h'], files['Environment/Native/GameBuildFacts.g.h'])
        self.assertIn(self.digest, files['Core/BuildProfiles/GameBuildFacts.g.cs'])
        self.assertIn(self.digest, files['Seasons/Native/GameBuildFacts.g.h'])

    def test_changed_guard_bytes_size_hash_and_rva_are_rejected(self):
        for field, value in [('rva', 1), ('size', 1), ('sha256', '0' * 64)]:
            with self.subTest(field=field):
                self.reject(lambda p: p['guards']['Season'][0].update({field: value}))

    def test_changed_signature_is_rejected(self):
        self.reject(lambda p: p['signatures']['Environment/Native/EnvironmentSignatures.h']['arrays'][0].update(bytes='00'))

    def test_invalid_abi_declaration_is_rejected(self):
        self.reject(lambda p: p['abi'].update(Draw='void(*)(); injected'))

    @unittest.skipUnless(shutil.which('powershell'), 'Windows PowerShell is required for the MSBuild validator')
    def test_msbuild_validator_rejects_each_changed_generated_input_without_touching_checkout(self):
        lock = json.loads((generate.HERE / 'generated.lock.json').read_bytes())
        paths = list(lock['files']) + [r['path'] for r in self.profile['resources']] + ['BuildProfiles/current.json']
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'source'
            for name in paths + ['BuildProfiles/generated.lock.json', 'BuildProfiles/Validate.ps1']:
                target = source / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(generate.SOURCE / name, target)
            def validate():
                return subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                                       str(source / 'BuildProfiles/Validate.ps1')], capture_output=True).returncode
            self.assertEqual(0, validate())
            for name in paths:
                with self.subTest(path=name):
                    target = source / name
                    original = target.read_bytes()
                    try:
                        target.write_bytes(bytes([original[0] ^ 1]) + original[1:])
                        self.assertNotEqual(0, validate(), name)
                    finally:
                        target.write_bytes(original)
            self.assertEqual(0, validate())

    def test_changed_resource_and_path_escape_are_rejected(self):
        self.reject(lambda p: p['resources'][0].update(sha256='0' * 64))
        self.reject(lambda p: p['resources'][0].update(path='../outside.json'))
        self.reject(lambda p: p['resources'].append(p['resources'][0]))

    def test_unknown_schema_invalid_hash_and_numeric_overflow_are_rejected(self):
        self.reject(lambda p: p.update(schemaVersion=2))
        self.reject(lambda p: p.update(exeSha256='1.0.1.0'))
        self.reject(lambda p: p['groups']['Memory']['CityStride'].update(value=-1))
        self.reject(lambda p: p['groups']['Memory']['CityStride'].update(value=0x80000000))
        self.reject(lambda p: p['groups']['Memory']['CityStride'].update(type='string'))


if __name__ == '__main__':
    unittest.main()

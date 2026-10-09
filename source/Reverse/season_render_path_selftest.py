"""Test false-success rejection in the saved render-path audit; no game access."""
import copy
import struct
from types import SimpleNamespace
import unittest

from season_render_path import (audit, audit_trace, sdk_methods, SDK_METHODS,
                                binding_tables, BINDING_TABLES, ENGINE_STAGES)


def binding_fixture():
    base = 0x100000
    qwords = [base + 0x200, 0x300, 0x400, *([0x500] * 4), 0x600,
              *([0x700] * 12), *([0x800] * 8), *([0x900] * 8)]
    arguments = (4, 1, 7, 0, 1, 0)
    viewport = [0., 0., 1920., 1080., 0., 1.]
    raw = struct.pack('<36Q3Ii2I6f2I', *qwords, *arguments, *viewport, 1, 0)
    row = dict(sequence=0, caller=hex(qwords[0]), callerRva='0x200',
               ps='0x300', vs='0x400', targets=['0x500'] * 4, depth='0x600',
               views=['0x700'] * 12, psConstants=['0x800'] * 8, vsConstants=['0x900'] * 8,
               **dict(zip(('kind', 'count', 'start', 'base', 'instances', 'firstInstance'), arguments)),
               viewport=viewport)
    return dict(state=dict(exe=hex(base)), indices=[8, 9], records=[row]), raw


def sdk_fixture():
    methods = [f'Method{index}' for index in range(115)]
    methods[:3] = ['QueryInterface', 'AddRef', 'Release']
    for slot, name in SDK_METHODS.items():
        methods[slot] = name
    return 'typedef struct ID3D11DeviceContextVtbl {\n' + '\n'.join(
        f'void ( STDMETHODCALLTYPE *{name} )(void);' for name in methods) + '\n} ID3D11DeviceContextVtbl;'


class AuditTests(unittest.TestCase):
    def adapter_fixture(self):
        methods = [f'Method{index}' for index in range(115)]
        slots = {'ShaderResources': (25, 59, 63, 31, 8, 67),
                 'Samplers': (26, 61, 65, 32, 10, 70),
                 'ConstantBuffers': (7, 62, 66, 22, 16, 71)}
        pointers = {}
        instructions = {}
        for table_index, (kind, table) in enumerate(BINDING_TABLES.items()):
            for stage, slot in enumerate(slots[kind]):
                methods[slot] = ENGINE_STAGES[stage] + 'Set' + kind
                start = 0x200000 + table_index * 0x1000 + stage * 16
                pointers[table + stage * 8] = 0x100000 + start
                instructions[start] = [SimpleNamespace(mnemonic='mov', op_str='rax, qword ptr [rcx]'),
                                       SimpleNamespace(mnemonic='jmp', op_str=f'qword ptr [rax + {slot*8:#x}]')]
        image = SimpleNamespace(base=0x100000, u64=lambda address: pointers[address])
        return image, methods, instructions

    def test_engine_stage_is_resolved_from_sdk_method_not_shader_version_number(self):
        image, methods, instructions = self.adapter_fixture()
        result = binding_tables(image, methods, lambda start: instructions[start])
        self.assertEqual(18, len(result))
        constants = [row for row in result if row['binding'] == 'ConstantBuffers']
        self.assertEqual([(0, 'VS', 7), (1, 'HS', 62), (2, 'DS', 66),
                          (3, 'GS', 22), (4, 'PS', 16), (5, 'CS', 71)],
                         [(row['engineStage'], row['stage'], row['nativeSlot']) for row in constants])
        self.assertTrue(all(row['ownedContextAcquired'] is False for row in result))
        methods[25] = 'PSSetShaderResources'
        with self.assertRaises(ValueError):
            binding_tables(image, methods, lambda start: instructions[start])

    def test_adapter_with_extra_side_effect_is_not_a_pure_tail_call(self):
        image, methods, instructions = self.adapter_fixture()
        instructions[next(iter(instructions))].insert(1, SimpleNamespace(mnemonic='call', op_str='0x1234'))
        with self.assertRaises(ValueError):
            binding_tables(image, methods, lambda start: instructions[start])

    def test_binding_with_shader_rt_depth_is_not_a_draw_or_owned_callback(self):
        document, raw = binding_fixture()
        result = audit_trace(document, raw)
        self.assertEqual({'PSSetShaderResources': 1, 'PSSetShader': 0}, result['eventCounts'])
        self.assertEqual(0, result['drawCallsRecorded'])
        self.assertEqual(0, result['frameBoundariesRecorded'])
        for key in ('completeFrame', 'finalDrawStateVerified', 'gamePassVerified',
                    'renderThreadRecorded', 'resourceOwnershipVerified'):
            self.assertIs(False, result[key])

    def test_binary_json_pointer_argument_and_viewport_mismatch_are_rejected(self):
        document, raw = binding_fixture()
        for key, value in (('ps', '0x301'), ('count', 2), ('viewport', [0.] * 6), ('sequence', True)):
            with self.subTest(key=key):
                changed = copy.deepcopy(document)
                changed['records'][0][key] = value
                with self.assertRaises(ValueError):
                    audit_trace(changed, raw)

    def test_short_binary_and_incomplete_record_are_rejected(self):
        document, raw = binding_fixture()
        incomplete = bytearray(raw)
        struct.pack_into('<I', incomplete, 336, 0)
        for bad in (raw[:-1], raw + bytes(344), bytes(incomplete)):
            with self.assertRaises(ValueError):
                audit_trace(document, bad)

    def test_relabelled_draw_and_wrong_api_indices_are_rejected(self):
        document, raw = binding_fixture()
        changed = copy.deepcopy(document)
        changed['records'][0]['kind'] = 0
        changed_raw = bytearray(raw)
        struct.pack_into('<I', changed_raw, 288, 0)
        with self.assertRaises(ValueError):
            audit_trace(changed, changed_raw)
        document['indices'] = [12, 13]
        with self.assertRaises(ValueError):
            audit_trace(document, raw)

    def test_historical_module_base_must_match_each_record(self):
        document, raw = binding_fixture()
        document['state']['exe'] = '0x200000'
        with self.assertRaises(ValueError):
            audit_trace(document, raw)

    def test_sdk_slot_layout_is_checked_before_names_are_assigned(self):
        source = sdk_fixture()
        self.assertEqual('DrawIndexed', sdk_methods(source)[12])
        for bad in (source.replace('*DrawIndexed ', '*Changed '),
                    source.replace('*Method114 ', '*Method114_REMOVED ', 1).replace(
                        'void ( STDMETHODCALLTYPE *Method114_REMOVED )(void);', ''), ''):
            with self.assertRaises(ValueError):
                sdk_methods(bad)

    def test_changed_image_is_rejected_before_any_engine_disassembly(self):
        document, raw = binding_fixture()
        with self.assertRaisesRegex(ValueError, 'image hash'):
            audit(SimpleNamespace(img=b'changed image'), document, raw, sdk_fixture())


if __name__ == '__main__':
    unittest.main()

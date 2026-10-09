# Synthetic machine code below tests the analyzer, never substitutes a game helper.
import struct
import unittest
import capstone
from ai_attack_access import audit
from ai_attack_boundaries import closure, self_check
from image import Image


class BytesImage:
    def __init__(self, functions):
        self.functions = {rva: bytes.fromhex(code) for rva, code in functions.items()}
        self.img = bytes(0x2000)  # An empty import table for these analyzer-only programs.
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.md.detail = True

    def u32(self, rva): return struct.unpack_from('<I', self.img, rva)[0]
    def u64(self, rva): return struct.unpack_from('<Q', self.img, rva)[0]
    def flow(self, start): return list(self.md.disasm(self.functions.get(start, b''), start))
    def flowtext(self, start): return '\n'.join(f'{i.address:x} {i.mnemonic} {i.op_str}' for i in self.flow(start))


class BoundaryTests(unittest.TestCase):
    def test_instruction_checks(self):
        self.assertEqual(11, self_check()['passed'])

    def test_internal_field_reload_follows_subobject_receiver(self):
        # root stores node+68 at node+78 and reloads it as the next receiver.
        im = BytesImage({0x1000: '488D416848894178488B4978E8EF000000C3', 0x1100: '8B4118C3'})
        result = closure(im, 8, (0x1000,))
        child = next(r for r in result['contexts'] if r['entry'] == 0x1100)
        self.assertEqual({'rcx': ['node', 0x68]}, child['arguments'])
        self.assertEqual('readOrModify', child['accesses'][0]['kind'])
        self.assertEqual(0x80, child['accesses'][0]['offset'])

    def test_only_read_incoming_stack_slot_is_followed(self):
        root = '4883EC2848894C242031C9E8F00000004883C428C3'
        im = BytesImage({0x1000: root, 0x1100: '488B4424288B8080000000C3'})
        result = closure(im, 8, (0x1000,))
        child = next(r for r in result['contexts'] if r['entry'] == 0x1100)
        self.assertEqual({'@40': ['node', 0]}, child['arguments'])
        self.assertEqual(1, len(child['accesses']))
        im = BytesImage({0x1000: root, 0x1100: '48C744242800000000488B4424288B8080000000C3'})
        self.assertEqual(1, len(closure(im, 8, (0x1000,))['contexts']))

    def test_common_pointer_return_is_propagated_and_differing_returns_are_not(self):
        root = 'E8FB0000008B8080000000C3'
        im = BytesImage({0x1000: root, 0x1100: '4889C8C3'})
        result = closure(im, 8, (0x1000,))
        self.assertTrue(result['converged'])
        self.assertEqual(1, len(result['contexts'][0]['accesses']))
        im = BytesImage({0x1000: root, 0x1100: '85D274044889C8C331C0C3'})
        result = closure(im, 8, (0x1000,))
        self.assertEqual([], result['contexts'][0]['accesses'])
        self.assertEqual([], result['pointerReturnSummaries'])

    def test_external_store_and_context_limit_remain_visible(self):
        result = audit(BytesImage({0x1000: '48890AC3'}), 0x1000)
        self.assertEqual('pointerStore', result['pointerEscapes'][0]['kind'])
        self.assertIsNone(result['pointerEscapes'][0]['destination'])
        result = closure(BytesImage({0x1000: 'E8FB000000C3', 0x1100: '4889C8C3'}), 1, (0x1000,))
        self.assertFalse(result['converged'])
        self.assertEqual('context limit', result['unresolvedBoundaries'][0]['reason'])

    def test_actual_saved_code_keeps_unknown_meaning_and_new_field_call_evidence(self):
        result = closure(Image(), 256)
        self.assertTrue(result['converged'])
        self.assertEqual(28, len(result['contexts']))
        self.assertEqual(7, len(result['unresolvedBoundaries']))
        self.assertEqual(83, result['stackProbeCount'])
        self.assertTrue(all(not r['matched'] for r in result['stackBoundaryChecks']))
        call = next(e for e in result['callEdges'] if e.get('callee') == 0x58820)
        self.assertEqual(0x2F3EA, call['rva'])
        self.assertEqual({'rcx': ['node', 0x68]}, call['followedArguments'])
        self.assertFalse(any(e['kind'] == 'readOrModify' for r in result['contexts'] for e in r['accesses']))


if __name__ == '__main__':
    unittest.main()

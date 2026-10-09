# Static graph bookkeeping tests; synthetic bytes are never native evidence.
import unittest
import capstone
from ai_path_list_boundary import closure
from image import Image


class BytesImage:
    def __init__(self):
        self.img = bytes(0x5000)
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        # Entry calls2000;2000 tail-jumps3000;3000 has local jump then indirect call.
        self.code = {0x1000: bytes.fromhex('E8FB0F0000C3'), 0x2000: bytes.fromhex('E9FB0F0000'),
                     0x3000: bytes.fromhex('EB0090FFD0C3')}
    def flow(self, start): return list(self.md.disasm(self.code.get(start, b''), start))


class ListBoundaryTests(unittest.TestCase):
    def test_direct_calls_and_external_tails_follow_but_local_jumps_do_not(self):
        report = closure(BytesImage(), entry=0x1000, imported={})
        self.assertEqual([0x1000, 0x2000, 0x3000], [r['entry'] for r in report['functions']])
        self.assertEqual(2, report['summary']['directEdges'])
        self.assertEqual(1, report['summary']['unresolved'])
        self.assertTrue(report['directWorklistComplete'])
        self.assertFalse(report['transitiveMutationClosureProved'])
        self.assertFalse(report['loopTakenObserved'])

    def test_limit_preserves_unvisited_entries(self):
        report = closure(BytesImage(), entry=0x1000, maximum=1, imported={})
        self.assertFalse(report['directWorklistComplete'])
        self.assertEqual([0x2000], report['pendingEntries'])

    def test_unknown_in_image_target_stays_an_unresolved_boundary(self):
        image = BytesImage(); image.code[0x2000] = b''
        report = closure(image, entry=0x1000, imported={})
        self.assertEqual('no decoded instructions', report['unresolvedBoundaries'][0]['reason'])
        self.assertTrue(report['directWorklistComplete'])
        self.assertFalse(report['transitiveMutationClosureProved'])

    def test_saved_image_exposes_pool_operations_and_indirect_limits(self):
        report = closure(Image())
        self.assertEqual(dict(functions=434, instructions=21242, directEdges=1144,
                              poolReferences=37, unresolved=256, importedBoundaries=118), report['summary'])
        self.assertTrue(report['directWorklistComplete'])
        self.assertEqual({'0x18b60', '0x18df0', '0x18770', '0x18830'}, set(report['operationPaths']))
        self.assertEqual(0x185D4C, report['operationPaths']['0x18df0'][0]['rva'])
        self.assertEqual(0x18DF0, report['operationPaths']['0x18df0'][-1]['target'])
        self.assertFalse(report['loopTakenObserved'])
        self.assertFalse(report['transitiveMutationClosureProved'])


if __name__ == '__main__': unittest.main()

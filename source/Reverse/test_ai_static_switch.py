import unittest
from ai_static_switch import flow, guarded_tables
from ai_path_list_boundary import closure
from image import Image


class StaticSwitchTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.image = Image(); cls.image.md.detail = True

    def test_guarded_leaf_and_unwind_functions_recover_only_exact_table_targets(self):
        expected = {0x1B3C80: (207, {0x1B3CB0, 0x1B3CD4}),
                    0x1B3F10: (62, {0x1B3F3D, 0x1B3F45, 0x1B3F4D}),
                    0x1B3DB0: (52, {0x1B3DD8, 0x1B3DDE})}
        for entry, (count, targets) in expected.items():
            code, switches = flow(self.image, entry)
            self.assertEqual(1, len(switches)); row = switches[0]
            self.assertEqual(count, row['count'])
            self.assertEqual(targets, {t['target'] for t in row['targets']})
            self.assertTrue(targets <= {i.address for i in code})
        self.assertIsNone(self.image.func(0x1B3C80))

    def test_removed_unsigned_guard_cannot_recover_a_dispatch(self):
        image = Image(); raw = bytearray(image.img)
        raw[0x1B3C90] = 0x76  # Altered analyzer input only: jbe, not ja.
        image.img = bytes(raw); image.md.detail = True
        self.assertEqual([], guarded_tables(image, 0x1B3C80, list(image.flow(0x1B3C80))))

    def test_table_target_in_data_is_rejected(self):
        image = Image(); raw = bytearray(image.img)
        raw[0x1B3CD8:0x1B3CDC] = image.sections['.data'][0].to_bytes(4, 'little')
        image.img = bytes(raw); image.md.detail = True
        self.assertEqual([], guarded_tables(image, 0x1B3C80, list(image.flow(0x1B3C80))))

    def test_dispatch_resolution_adds_static_paths_without_claiming_native_coverage(self):
        report = closure(self.image, switches=True)
        self.assertEqual(3, len(report['resolvedSwitches']))
        self.assertFalse(report['loopTakenObserved'])
        self.assertFalse(report['transitiveMutationClosureProved'])
        self.assertTrue(report['directWorklistComplete'])
        self.assertNotIn(0x1B3CAE, {r.get('rva') for r in report['unresolvedBoundaries']})


if __name__ == '__main__': unittest.main()

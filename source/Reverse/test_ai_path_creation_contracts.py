# Original saved-code guard tests; no game process and no synthetic native states.
import unittest
from image import Image
from ai_path_creation_contracts import code_contracts


class CreationContractTests(unittest.TestCase):
    def test_original_commander_assignment_has_one_clamped_direct_store(self):
        facts = code_contracts(Image())['commanderAssignment']
        self.assertEqual([0x2D2AAA], facts['directStores'])
        self.assertEqual((0, 6000), (facts['minimum'], facts['maximum']))
        self.assertTrue(facts['dominates'])
        self.assertFalse(facts['nativeObserved'])

    def test_changed_clamp_is_rejected_instead_of_inheriting_a_contract(self):
        im = Image(); value = bytearray(im.img); value[0x2D2AA1] ^= 1; im.img = bytes(value)
        with self.assertRaisesRegex(ValueError, 'Changed fact anchor'): code_contracts(im)

    def test_original_success_path_requires_the_definition_terrain_gate(self):
        facts = code_contracts(Image())
        self.assertTrue(facts['placementTerrainGate']['dominates'])
        self.assertEqual(0x1A, facts['sharedBuildingId']['tileOffset'])
        self.assertIn('other writers', facts['commanderAssignment']['scope'])


if __name__ == '__main__': unittest.main()

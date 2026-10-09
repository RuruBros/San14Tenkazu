import unittest
from unittest.mock import patch
from ai_attack_receiver import build,r8_reads,r12_constant
from test_ai_attack_boundaries import BytesImage
from image import Image


class AttackReceiverTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):cls.image=Image();cls.image.md.detail=True

    def test_world_array_and_cached_receiver_bind_the_original_troops_slot(self):
        result=build(self.image)
        self.assertEqual((501,0x40,0x221980),(result['arrayCount'],result['arrayStride'],result['constructor']))
        self.assertEqual((0x7F8D8,0x13A6918,0x234540),(result['receiverTable'],result['vtable'],result['target']))
        self.assertEqual([0x49054,0x49A4E],result['directCacheWriters'])
        self.assertEqual(0,result['cacheInitializerR12'])
        self.assertFalse(result['nativeObserved'])

    def test_incoming_node_alias_is_killed_without_claiming_p2_meaning(self):
        result=build(self.image)
        self.assertEqual(0x222F39,result['aliasKill'])
        self.assertEqual([1,1,0],[len(r['callBoundaries']) for r in result['downstreamAliasAudits']])
        self.assertFalse(result['wholeProgramNonuseProved'])
        self.assertFalse(result['attackP2MeaningKnown'])

    def test_changed_receiver_class_is_rejected(self):
        with patch.object(self.image,'rtti_vtables',return_value={0x13A6918:'.?AVDifferentData@@'}):
            with self.assertRaisesRegex(ValueError,'Troops RTTI'):build(self.image)

    def test_an_incoming_r8_read_is_rejected_but_a_dominated_read_after_zero_is_allowed(self):
        image=BytesImage({0x1000:'418B0089C0C3'})
        with self.assertRaisesRegex(ValueError,'Incoming R8'):r8_reads(image,0x1000)
        image=BytesImage({0x1000:'4531C04489C0C3'})
        self.assertEqual(1,len(r8_reads(image,0x1000,0x1000)))

    def test_cache_initializer_constant_tracks_zero_and_widens_differing_paths(self):
        image=BytesImage({0x1000:'4531E44C89E0C3'})
        self.assertEqual(0,r12_constant(image,0x1000,0x1003))
        # Two branch paths set R12D to0/1 before the merge.
        image=BytesImage({0x1000:'85D274054531E4EB0641BC010000004C89E0C3'})
        self.assertIsNone(r12_constant(image,0x1000,0x100F))


if __name__=='__main__':unittest.main()

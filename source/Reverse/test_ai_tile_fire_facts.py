import unittest
from unittest.mock import patch

from ai_tile_fire_facts import build, proposal_binding
from ai_static_facts import anchored
from image import Image


class TileFireFactsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):cls.image=Image()

    def test_original_proposals_bind_the_same_timer_writer(self):
        result=build(self.image)
        self.assertEqual((0x16,2,720),(result['field']['offset'],result['field']['size'],result['field']['maximum']))
        self.assertEqual(48400,result['update']['tileCount'])
        self.assertEqual([0x415AC0,0x416A30],[r['target'] for r in result['proposals']])
        self.assertEqual(0x184084,result['path']['branch'])
        self.assertFalse(result['nativeObserved'])
        self.assertFalse(result['nativePathFireEdgeObserved'])

    def test_different_class_or_slot_is_rejected(self):
        with self.assertRaisesRegex(ValueError,'Proposal RTTI'):
            proposal_binding(self.image,0x13D2088,'.?AVCProposalHandler_ExtinguishFireFriend@@',0x415AC0)
        with self.assertRaisesRegex(ValueError,'Proposal RTTI'):
            proposal_binding(self.image,0x13D2088,'.?AVCProposalHandler_BurnEnemy@@',0x416A30)

    def test_data_pointer_cannot_be_bound_as_a_proposal_body(self):
        with patch.object(self.image,'u64',return_value=self.image.base+0x13D2088):
            with self.assertRaisesRegex(ValueError,'executable slot'):
                proposal_binding(self.image,0x13D2088,'.?AVCProposalHandler_BurnEnemy@@',0x13D2088)

    def test_signed_or_different_field_read_cannot_replace_the_unsigned_fire_filter(self):
        with self.assertRaisesRegex(ValueError,'Changed fact anchor'):
            anchored(self.image,0x183A30,{0x184084:'jg 0x1842df'})
        with self.assertRaisesRegex(ValueError,'Changed fact anchor'):
            anchored(self.image,0x183A30,{0x18407F:'cmp word ptr [rbx + 0x18], 0'})


if __name__=='__main__':unittest.main()

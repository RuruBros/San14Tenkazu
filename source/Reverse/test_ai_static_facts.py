import unittest
from ai_static_facts import anchored, bindings, build, dominates, pool_ownership, read_only_leaf
from ai_path_list_boundary import closure
from image import Image
from unittest.mock import patch
import capstone


class StaticFactsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls): cls.image=Image();cls.image.md.detail=True

    def test_installed_iterator_slot_and_factories_are_checked(self):
        result=bindings(self.image)
        self.assertEqual(0x333110,result['target'])
        self.assertEqual(4,len(result['factoryDominance']))
        self.assertEqual({0x222E2A,0x2230AA,0x185CDA,0x185E6A},set(result['callSites']))
        self.assertFalse(result['nativeObserved'])
        self.assertFalse(dominates(self.image,0x185B30,0x185E02,0x185CDA))

    def test_changed_constructor_anchor_is_rejected(self):
        with self.assertRaisesRegex(ValueError,'Changed fact anchor'):
            anchored(self.image,0x222DA0,{0x222E09:'mov qword ptr [rbx + 8], rax'})

    def test_static_pool_sources_do_not_claim_fresh_index_alias_closure(self):
        result=build(self.image)
        self.assertEqual(0x1B5C9D8,result['codeFacts']['integerIndexAllocation']['pool'])
        self.assertEqual(6,len(result['codeFacts']))
        self.assertFalse(result['nativeObserved'])
        self.assertFalse(result['transitiveMutationClosureProved'])

    def test_known_iterator_paths_are_followed_without_new_native_coverage(self):
        result=closure(self.image,switches=True,known_iterators=True)
        self.assertEqual(4,sum(e['kind']=='scoped_iterator_call' for e in result['directEdges']))
        self.assertIn(0x333110,{r['entry'] for r in result['functions']})
        self.assertFalse(result['loopTakenObserved'])
        self.assertFalse(result['transitiveMutationClosureProved'])

    def test_pool_membership_body_is_read_only_and_owner_sources_are_separate(self):
        result=pool_ownership(self.image)
        self.assertEqual(93,result['membershipReader']['instructions'])
        self.assertEqual(0,result['membershipReader']['nonStackWrites'])
        self.assertEqual(7,result['sevenReceiverLists']['count'])
        self.assertEqual(0x10,result['messageAppend']['indexPointerOffset'])
        self.assertFalse(result['transitiveMutationClosureProved'])
        self.assertFalse(result['loopTakenObserved'])

    def test_reader_rejects_new_heap_write_call_or_indirect_jump(self):
        decoder=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);decoder.detail=True
        for encoded in ('488901c3','e800000000c3','ffe0'):
            with self.subTest(encoded=encoded),patch.object(self.image,'flow',return_value=list(decoder.disasm(bytes.fromhex(encoded),0x1000))):
                with self.assertRaises(ValueError):read_only_leaf(self.image,0x1000)


if __name__=='__main__':unittest.main()

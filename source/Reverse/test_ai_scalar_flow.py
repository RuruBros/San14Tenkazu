# Synthetic instruction packets test the analyzer only, never supply native evidence.
from types import SimpleNamespace
import unittest
import capstone
from ai_scalar_flow import branch, flags_for, specialize
from ai_path_list_boundary import closure
from image import Image


def packet(encoded):
    decoder=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64);decoder.detail=True
    code=list(decoder.disasm(bytes.fromhex(encoded),0x1000))
    return SimpleNamespace(md=decoder,flow=lambda entry:code)


class ScalarFlowTests(unittest.TestCase):
    def test_unsigned_and_signed_conditions_are_distinct(self):
        f=flags_for(0xFFFFFFFF,50,4)
        self.assertTrue(branch('ja',f));self.assertFalse(branch('jg',f))
        self.assertIsNone(branch('je',None))

    def test_unknown_call_result_is_never_a_constant_branch(self):
        result=specialize(packet('b801000000e80000000083f8017401c3c3'),0x1000,{})
        self.assertEqual([],result['decisions'])
        self.assertIn(0x1010,result['reachable'])

    def test_partial_store_does_not_make_an_eight_byte_stack_read_constant(self):
        result=specialize(packet('c744242801000000488b4424284883f8017401c3c3'),0x1000,{})
        self.assertEqual([],result['decisions'])

    def test_actual_previous_force_zero_removes_both_old_force_action_blocks(self):
        result=specialize(Image(),0x185B30,{'r8':44,'r9':0},{0x28:1,0x30:1,0x38:0})
        choices={row['rva']:row['taken'] for row in result['decisions']}
        self.assertTrue(choices[0x185C09]);self.assertTrue(choices[0x185DBA])
        self.assertNotIn(0x185D22,result['reachable'])
        self.assertNotIn(0x185EBA,result['reachable'])
        self.assertFalse(result['nativeObserved'])

    def test_unknown_previous_force_preserves_the_two_action_calls(self):
        result=specialize(Image(),0x185B30,{'r8':44})
        self.assertIn(0x185D22,result['reachable']);self.assertIn(0x185EBA,result['reachable'])

    def test_narrowed_call_graph_keeps_unresolved_boundaries_explicit(self):
        result=closure(Image(),switches=True,known_iterators=True,scalar_context={'registers':{'r8':44,'r9':0},'stack':{0x28:1,0x30:1,0x38:0}})
        self.assertTrue(result['directWorklistComplete'])
        self.assertTrue(result['unresolvedBoundaries'])
        self.assertFalse(result['transitiveMutationClosureProved'])
        self.assertFalse(result['loopTakenObserved'])


if __name__=='__main__':unittest.main()

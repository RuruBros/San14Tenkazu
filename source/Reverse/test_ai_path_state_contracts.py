# Control tests never turn synthetic inputs into native evidence.
import copy
from types import SimpleNamespace
import struct
import unittest
from unittest.mock import patch
from ai_path_state_contracts import build, checked_tiles, code_contracts, facility_trials
from image import Image


class StateContractTests(unittest.TestCase):
    def test_original_anchors_keep_constructor_and_exception_scopes_explicit(self):
        facts=code_contracts(Image())
        self.assertEqual((48400,32),(facts['tileTable']['count'],facts['tileTable']['stride']))
        self.assertEqual([0x183F8A,0x18520C],facts['tileTable']['unobservedDirections'])
        self.assertIn('successful original allocation',facts['tileTable']['scope'])
        self.assertEqual([16,17],facts['facility']['exceptionTerrainCodes'])
        self.assertEqual({'r8':44,'r9':0},facts['globalCaller']['registers'])

    def test_changed_original_instruction_is_rejected(self):
        im=Image(); original=im.flow
        def altered(entry):
            instructions=list(original(entry))
            if entry==0x3262E0:
                ins=next(i for i in instructions if i.address==0x326B5A)
                instructions[instructions.index(ins)]=SimpleNamespace(address=ins.address,mnemonic='mov',op_str='r14d, 1')
            return instructions
        im.flow=altered
        with self.assertRaisesRegex(ValueError,'Changed fact anchor'):code_contracts(im)

    def test_null_or_noncontiguous_table_is_not_a_constructed_table(self):
        pointers=[0x100000+i*32 for i in range(48400)]
        game=SimpleNamespace(base=0x1290,read=lambda address,size:struct.pack('<48400Q',*pointers))
        self.assertTrue(checked_tiles(game)['verified'])
        for index,value in ((11,0),(0,0),(23,pointers[23]+1)):
            previous=pointers[index];pointers[index]=value
            with self.assertRaisesRegex(ValueError,'contiguous'):checked_tiles(game)
            pointers[index]=previous

    def test_invalid_frozen_pool_stops_before_native_harness(self):
        fake=SimpleNamespace(pool_snapshot={'valid':False})
        with patch('ai_path_state_contracts.FrozenImage',return_value=fake),patch('ai_path_state_contracts.Harness') as harness:
            with self.assertRaisesRegex(ValueError,'pool invalid'):build('synthetic',{})
            harness.assert_not_called()

    def test_mismatched_or_unstable_inventory_stops_before_native_harness(self):
        clock={'world':123};game=SimpleNamespace(snapshot_sha256='expected')
        inventory={'snapshot':{'sha256':'expected'},'clockBefore':clock,'clockAfter':clock,'clockStable':True}
        with patch('ai_path_state_contracts.read_clock',return_value=clock),patch('ai_path_state_contracts.Harness') as harness:
            for change in ({'snapshot':{'sha256':'wrong'}},{'clockBefore':{'world':124}},{'clockStable':False}):
                bad=copy.deepcopy(inventory);bad.update(change)
                with self.assertRaises(ValueError):facility_trials(game,bad)
            harness.assert_not_called()


if __name__=='__main__':unittest.main()

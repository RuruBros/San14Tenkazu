"""Own compiler fixtures and text rejection tests; no GPU or game access."""
import json
from pathlib import Path
import unittest

from season_shader_layout import LayoutReflector
from season_shader_layout_selftest import compile_own
from season_shader_instructions import disassemble,instruction_facts


PS=b'''Texture2D<float4> leaf : register(t0); SamplerState leafSampler : register(s0);
cbuffer Own : register(b2) { float cutoff; };
float4 main(float2 uv:TEXCOORD0):SV_Target {
 float4 value=leaf.Sample(leafSampler,uv); clip(value.a-cutoff); return value;
}'''
CS=b'''RWStructuredBuffer<uint> vox : register(u1);
[numthreads(1,1,1)] void main(uint3 id:SV_DispatchThreadID) {
 uint original; InterlockedMax(vox[id.x],1,original); vox[id.x+1]=original;
}'''


class InstructionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):cls.reflector=LayoutReflector();cls.records=[]

    def test_own_pixel_clip_and_constant_buffer(self):
        facts=instruction_facts(disassemble(self.reflector,compile_own(self.reflector,PS,b'ps_5_0')))
        self.assertEqual(0,facts['profile']['stage']);self.assertEqual(1,facts['discardInstructionCount'])
        self.assertEqual(0,facts['resourceWriteInstructionCount'])
        self.assertIn((2,0,'x'),[(r['slot'],r['vector'],r['components']) for r in facts['constantBufferReferences']])
        self.assertTrue(facts['criticalInstructions'][0]['precedingInstructions'])
        self.assertFalse(facts['alphaDataflowVerified']);self.assertFalse(facts['gamePassVerified'])
        self.records.append(facts)

    def test_own_structured_store_and_interlocked_write(self):
        facts=instruction_facts(disassemble(self.reflector,compile_own(self.reflector,CS,b'cs_5_0')))
        self.assertEqual(5,facts['profile']['stage']);self.assertEqual(0,facts['discardInstructionCount'])
        self.assertEqual(2,facts['resourceWriteInstructionCount'])
        self.assertEqual({'store_structured','imm_atomic_umax'},{r['opcode'] for r in facts['criticalInstructions']})
        self.records.append(facts)

    def test_comments_and_declarations_are_not_operations(self):
        facts=instruction_facts('// discard_nz r0.x\nps_5_0\ndcl_globalFlags refactoringAllowed\ndcl_uav_structured u1, 4\nret\n')
        self.assertEqual({'ret':1},facts['opcodeCounts']);self.assertEqual(1,facts['instructionCount'])
        self.assertEqual(2,len(facts['declarations']));self.assertEqual(0,facts['resourceWriteInstructionCount'])

    def test_dynamic_constant_buffer_expression_stays_uninterpreted(self):
        facts=instruction_facts('vs_5_0\nadd r0.x, cb2[r1.x + 3].z, cb0[1].xyxx\nret\n')
        dynamic,direct=facts['constantBufferReferences']
        self.assertFalse(dynamic['directIndex']);self.assertIsNone(dynamic['byteOffsets'])
        self.assertEqual('r1.x + 3',dynamic['expression']);self.assertEqual([16,20],direct['byteOffsets'])

    def test_missing_duplicate_or_malformed_profile_rejected(self):
        for text in ('','ret\n','ps_5_0\nvs_5_0\nret','ps_5_0\n?bad','// ps_5_0\n'):
            with self.subTest(text=text),self.assertRaises(ValueError):instruction_facts(text)

    @classmethod
    def tearDownClass(cls):
        path=Path(__file__).resolve().parent/'.cache/season-safety/20261007-shader-states/instruction-fixtures.json'
        path.parent.mkdir(parents=True,exist_ok=True)
        path.write_text(json.dumps(dict(deviceCreated=False,gameProcessAccess=False,
            compilerSha256=cls.reflector.sha,fixtures=cls.records),indent=2)+'\n',encoding='utf-8')


if __name__=='__main__':unittest.main(verbosity=2)

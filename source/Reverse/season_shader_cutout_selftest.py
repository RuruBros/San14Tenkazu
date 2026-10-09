"""Own compiler and adversarial text fixtures; never creates a GPU or opens SAN14."""
import copy
import json
from pathlib import Path
import unittest

from season_shader_cutout import operands,recognize_cutouts,bind_declarations
from season_shader_layout import LayoutReflector,buffer_binding
from season_shader_layout_selftest import compile_own
from season_shader_instructions import disassemble

DIRECT='''ps_5_0
dcl_temps 3
sample_indexable(texture2d)(float,float,float,float) r0.xyzw, v1.xyxx, t0.xyzw, s0
add r1.x, r0.w, -cb2[0].x
mov o0.xyz, r0.xyzx
lt r0.x, r1.x, l(0.000000)
discard r0.x
ret
'''
GATED='''ps_5_0
lt r0.x, l(0.000000), cb2[1].x
if_nz r0.x
sample_indexable(texture2d)(float,float,float,float) r0.x, v1.zwzz, t0.wxyz, s0
mad_sat r0.x, r0.x, cb2[2].y, cb2[2].z
else
mov r0.x, l(1.000000)
endif
add r0.x, r0.x, -cb2[0].w
lt r0.x, r0.x, l(0.000000)
discard r0.x
ret
'''


def own_layout():
    return dict(metadata=dict(resources=[
        dict(name='leaf',kind=2,slot=0,count=1,dimension=4,result=5),
        dict(name='leafSampler',kind=3,slot=0,count=1)],
        inputs=[dict(name='TEXCOORD',index=0,register=1,stream=0,mask=15)]),constantBuffers=[
            dict(name='Own',size=48,binding=dict(slot=2),variables=[
                dict(name='values',offset=0,size=48)])])


class CutoutTests(unittest.TestCase):
    def test_operand_nesting_and_malformed_expressions(self):
        self.assertEqual(['r0.xy','l(1, 2, 3, 4)','cb0[r1.x + 3].xy'],
            operands('r0.xy, l(1, 2, 3, 4), cb0[r1.x + 3].xy'))
        for text in ('r0.x,','r0.x, l(1,2]','cb0[r0.x','r0.x,,r1.x'):
            with self.subTest(text=text),self.assertRaises(ValueError):operands(text)

    def test_direct_sample_component_and_exact_threshold(self):
        row,=recognize_cutouts(DIRECT)
        self.assertEqual('directSample',row['pattern']);self.assertEqual('w',row['sample']['sampledComponent'])
        self.assertEqual((2,0),(row['threshold']['slot'],row['threshold']['byteOffset']))
        self.assertEqual('xy',row['sample']['coordinateComponents'])
        self.assertFalse(row['gamePassVerified']);self.assertFalse(row['actualAlphaCoverageVerified'])

    def test_gated_sample_retains_enable_scale_bias_and_fallback(self):
        row,=recognize_cutouts(GATED)
        self.assertEqual('gatedAdjustedSample',row['pattern']);self.assertEqual('w',row['sample']['sampledComponent'])
        self.assertEqual('zw',row['sample']['coordinateComponents']);self.assertEqual(12,row['threshold']['byteOffset'])
        self.assertEqual([16,36,40],[row['adjustments'][k]['byteOffset'] for k in ('enable','scale','bias')])

    def test_sample_swizzle_maps_by_destination_component_not_mask_position(self):
        for destination,channel in [('x','w'),('y','x'),('z','y'),('w','z')]:
            text=DIRECT.replace('r0.xyzw','r0.'+destination).replace('r0.w','r0.'+destination)
            text=text.replace('t0.xyzw','t0.wxyz')
            row,=recognize_cutouts(text)
            self.assertEqual(channel,row['sample']['sampledComponent'])

    def test_intervening_component_write_invalidates_dependency(self):
        bad=DIRECT.replace('add r1.x','mov r0.w, l(1.000000)\nadd r1.x')
        with self.assertRaises(ValueError):recognize_cutouts(bad)
        good=DIRECT.replace('add r1.x','mov r0.y, l(1.000000)\nadd r1.x')
        self.assertEqual(1,len(recognize_cutouts(good)))

    def test_multi_destination_or_unknown_intervening_opcode_is_never_skipped(self):
        for instruction in ('udiv r2.x, r0.w, r2.y, r2.z', 'custom_writer r2.x, r0.w'):
            text=DIRECT.replace('add r1.x',instruction+'\nadd r1.x')
            with self.subTest(instruction=instruction),self.assertRaises(ValueError):recognize_cutouts(text)

    def test_dynamic_modifiers_wrong_comparisons_and_unknown_writers_rejected(self):
        for old,new in [('-cb2[0].x','-cb2[r2.x].x'),('-cb2[0].x','cb2[0].x'),
                        ('lt r0.x','ge r0.x'),('r1.x, l(0.000000)','r1.x, l(1.000000)'),
                        ('t0.xyzw','t0.xyz'),('v1.xyxx','r2.xyxx'),('discard r0.x','discard_z r0.x'),
                        ('add r1.x','mad r1.x'),('ps_5_0','vs_5_0')]:
            with self.subTest(new=new),self.assertRaises(ValueError):recognize_cutouts(DIRECT.replace(old,new))

    def test_unverified_conditional_loops_and_unknown_gate_shapes_rejected(self):
        for text in [DIRECT.replace('sample_indexable','if_nz r2.x\nsample_indexable'),
                     DIRECT.replace('dcl_temps 3','loop\nendloop'),
                     GATED.replace('if_nz r0.x','if_z r0.x'),
                     GATED.replace('l(1.000000)','l(0.000000)'),
                     GATED.replace('mad_sat r0.x, r0.x','mad_sat r0.x, r1.x'),
                     GATED.replace('else\nmov','else\nmov r2.x, l(0)\nmov')]:
            with self.subTest(text=text),self.assertRaises(ValueError):recognize_cutouts(text)

    def test_bindings_bounds_ambiguity_and_input_masks_rejected(self):
        pattern=recognize_cutouts(DIRECT)[0];layout=own_layout();receipt=bind_declarations(pattern,layout)
        self.assertEqual('values',receipt['threshold']['variableName'])
        self.assertFalse(receipt['gameResourceIdentityVerified'])
        bad=[]
        item=copy.deepcopy(layout);item['metadata']['resources'].pop();bad.append(item)
        item=copy.deepcopy(layout);item['metadata']['resources'].append(item['metadata']['resources'][0]);bad.append(item)
        item=copy.deepcopy(layout);item['metadata']['inputs'][0]['mask']=1;bad.append(item)
        item=copy.deepcopy(layout);item['constantBuffers'][0]['size']=0;bad.append(item)
        item=copy.deepcopy(layout);item['constantBuffers'][0]['variables']=[];bad.append(item)
        item=copy.deepcopy(layout);item['constantBuffers'][0]['variables']*=2;bad.append(item)
        item=copy.deepcopy(layout);item['metadata']['resources'][0]['dimension']=5;bad.append(item)
        for item in bad:
            with self.subTest(item=item),self.assertRaises(ValueError):bind_declarations(pattern,item)

    def test_no_discard_has_no_receipt_and_comment_is_not_a_cutout(self):
        self.assertEqual([],recognize_cutouts('// discard r0.x\nps_5_0\nret\n'))
        with self.assertRaises(ValueError):recognize_cutouts('ps_5_0\n'+' '*4*1024*1024)

    def test_own_compiler_alpha_and_blue_channel_correspondence(self):
        reflector=LayoutReflector();fixtures=[]
        for component,expected in [('a','w'),('b','z')]:
            source=('Texture2D<float4> leaf : register(t3); SamplerState leafSampler : register(s1);\n'
                    'cbuffer Own : register(b2) { float cutoff; };\n'
                    'float4 main(float2 uv:TEXCOORD0):SV_Target { float4 value=leaf.Sample(leafSampler,uv);\n'
                    'clip(value.'+component+'-cutoff); return value; }').encode()
            code=compile_own(reflector,source,b'ps_5_0');assembly=disassemble(reflector,code)
            pattern,=recognize_cutouts(assembly);layout=reflector.inspect(code)
            layout['constantBuffers']=reflector.layouts(code)
            for buffer in layout['constantBuffers']:buffer['binding']=buffer_binding(buffer,layout['metadata']['resources'])
            receipt=bind_declarations(pattern,layout)
            self.assertEqual(expected,pattern['sample']['sampledComponent'])
            self.assertEqual('cutoff',receipt['threshold']['variableName']);self.assertEqual(3,pattern['sample']['textureSlot'])
            self.assertEqual(1,pattern['sample']['samplerSlot']);fixtures.append(dict(component=component,assembly=assembly,pattern=pattern,receipt=receipt))
        out=Path(__file__).parent/'.cache/season-safety/20261007-cutout/cutout-fixtures.json';out.parent.mkdir(parents=True,exist_ok=True)
        out.write_text(json.dumps(dict(compilerSha256=reflector.sha,gameProcessAccess=False,deviceCreated=False,
            fixtures=fixtures),indent=2)+'\n',encoding='utf-8')


if __name__=='__main__':unittest.main(verbosity=2)

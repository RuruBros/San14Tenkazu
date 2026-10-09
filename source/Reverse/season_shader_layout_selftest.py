"""Compile own HLSL and reflect/disassemble in this process; no GPU/game access."""
import ctypes as c
import hashlib
import json
from pathlib import Path
import re
import unittest

from season_shader_layout import LayoutReflector,BufferDesc,VariableDesc,TypeDesc,validate_variable,buffer_binding
from season_shader_instructions import blob_bytes,disassemble


def compile_own(reflector,source,profile):
    compile_function=reflector.dll.D3DCompile
    compile_function.argtypes=(c.c_void_p,c.c_size_t,c.c_char_p,c.c_void_p,c.c_void_p,c.c_char_p,c.c_char_p,
                               c.c_uint32,c.c_uint32,c.POINTER(c.c_void_p),c.POINTER(c.c_void_p))
    compile_function.restype=c.c_int32
    storage=c.create_string_buffer(source);code=c.c_void_p();errors=c.c_void_p()
    status=compile_function(storage,len(source),b'own-layout-fixture.hlsl',None,None,b'main',profile,0x800,0,c.byref(code),c.byref(errors))
    try:
        if status<0:raise ValueError(blob_bytes(reflector,errors).decode('utf-8',errors='replace') if errors else 'Own compile failed')
        if not code:raise ValueError('Own compiler returned no bytecode')
        return blob_bytes(reflector,code)
    finally:
        if errors:reflector.method(errors,2)(errors)
        if code:reflector.method(code,2)(code)


VS=b'''cbuffer Own : register(b2) {
 float4 tint; row_major float3x4 rowMatrix; column_major float3x4 columnMatrix;
 float values[3]; float unused;
};
float4 main(float4 p:POSITION):SV_Position {
 float3 q=mul(rowMatrix,p)+mul(columnMatrix,p)+float3(values[0],values[1],values[2])+tint.xyz;
 return float4(q,tint.w);
}'''
CS=b'''struct Voxel { uint identifier; float3 direction; };
RWStructuredBuffer<Voxel> vox : register(u1);
cbuffer Own : register(b2) { float4 tint; };
[numthreads(1,1,1)] void main(uint3 id:SV_DispatchThreadID) {
 Voxel v; v.identifier=id.x; v.direction=tint.xyz; vox[id.x]=v;
}'''


class LayoutTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.reflector=LayoutReflector();cls.records=[]

    def test_x64_sdk_layout_and_offsets(self):
        self.assertEqual((24,48,40),(c.sizeof(BufferDesc),c.sizeof(VariableDesc),c.sizeof(TypeDesc)))
        self.assertEqual(24,VariableDesc.DefaultValue.offset)
        self.assertEqual(32,TypeDesc.Name.offset)

    def test_variable_bounds_reject_bad_ranges(self):
        for values in ((65537,0,1),(16,-1,1),(16,17,0),(16,15,2),(16,0,-1)):
            with self.assertRaises(ValueError):validate_variable(*values)
        validate_variable(16,16,0);validate_variable(16,0,16)

    def test_borrowed_children_preserve_matrix_classes_and_binding(self):
        code=compile_own(self.reflector,VS,b'vs_5_0');text=disassemble(self.reflector,code)
        layouts=self.reflector.layouts(code);metadata=self.reflector.inspect(code)['metadata']
        own=next(b for b in layouts if b['name']=='Own')
        self.assertEqual(0,own['index']);self.assertEqual(2,buffer_binding(own,metadata['resources'])['slot'])
        fields={v['name']:v for v in own['variables']}
        self.assertEqual((2,3,4),tuple(fields['rowMatrix']['type'][k] for k in ('variableClass','rows','columns')))
        self.assertEqual((3,3,4),tuple(fields['columnMatrix']['type'][k] for k in ('variableClass','rows','columns')))
        self.assertEqual(3,fields['values']['type']['elements'])
        self.assertFalse(fields['unused']['flags']&2)
        for variable in own['variables']:
            lines=[line for line in text.splitlines() if re.search(r'\b'+re.escape(variable['name'])+r'\b',line) and 'Offset:' in line]
            self.assertEqual(1,len(lines))
            matched=re.search(r'Offset:\s*(\d+)\s+Size:\s*(\d+)',lines[0])
            self.assertIsNotNone(matched)
            self.assertEqual((variable['offset'],variable['size']),tuple(map(int,matched.groups())))
        # A second parent allocation after release has the same serialized values.
        self.assertEqual(layouts,self.reflector.layouts(code))
        self.records.append(dict(profile='vs_5_0',sourceSha256=hashlib.sha256(VS).hexdigest(),
                                 bytecodeSha256=hashlib.sha256(code).hexdigest(),layouts=layouts,assembly=text))

    def test_resource_element_metadata_is_not_a_constant_buffer(self):
        code=compile_own(self.reflector,CS,b'cs_5_0');layouts=self.reflector.layouts(code)
        metadata=self.reflector.inspect(code)['metadata']
        vox=next(b for b in layouts if b['name']=='vox');own=next(b for b in layouts if b['name']=='Own')
        self.assertEqual(3,vox['bufferType']);self.assertEqual(16,vox['size'])
        self.assertEqual('$Element',vox['variables'][0]['name'])
        self.assertEqual(2,vox['variables'][0]['type']['members'])
        self.assertEqual([('identifier',0),('direction',4)],
            [(v['memberName'],v['offset']) for v in vox['variables'][0]['type']['memberLayouts']])
        self.assertEqual((6,1),tuple(buffer_binding(vox,metadata['resources'])[k] for k in ('kind','slot')))
        self.assertEqual(0,own['bufferType']);self.assertEqual(2,buffer_binding(own,metadata['resources'])['slot'])
        self.records.append(dict(profile='cs_5_0',sourceSha256=hashlib.sha256(CS).hexdigest(),
                                 bytecodeSha256=hashlib.sha256(code).hexdigest(),layouts=layouts,assembly=disassemble(self.reflector,code)))

    def test_binding_mismatch_rejects_used_and_duplicate_resources(self):
        used={'name':'Own','bufferType':0,'variables':[{'flags':2}]}
        binding={'name':'Own','kind':0,'slot':2}
        for candidates in ([],[binding,binding],[{'name':'Own','kind':6,'slot':2}]):
            with self.assertRaises(ValueError):buffer_binding(used,candidates)
        self.assertIsNone(buffer_binding({'name':'Own','bufferType':0,'variables':[{'flags':0}]},[]))

    @classmethod
    def tearDownClass(cls):
        out=Path(__file__).resolve().parent/'.cache/season-safety/20261007-shader-states/layout-fixtures.json'
        out.parent.mkdir(parents=True,exist_ok=True)
        out.write_text(json.dumps(dict(gameProcessAccess=False,deviceCreated=False,compilerPath=str(cls.reflector.path),
            compilerSha256=cls.reflector.sha,fixtures=cls.records),indent=2)+'\n',encoding='utf-8')


if __name__=='__main__':unittest.main(verbosity=2)

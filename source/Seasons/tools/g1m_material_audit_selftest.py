"""Synthetic G1M boundary/name/layout fixtures only; no archives/game/GPU."""
import struct
import unittest

from g1m_material_audit import read_material_sections,correlate_attribute


def attribute(name=b'fOwn',value=struct.pack('<f',0.25),raw_kind=1):
    name=name+b'\0';name+=b'\0'*(-len(name)%4)
    return struct.pack('<3I2H',16+len(name)+len(value),len(name),0,raw_kind,1)+name+value


def section(kind,body,records=1):return struct.pack('<3I',kind,12+len(body),records)+body


def fixture(attributes=None,raw_state=b'\x12\x34\x56\x78'*17):
    attrs=[attribute()] if attributes is None else attributes
    state=section(0x10001,raw_state)
    material=section(0x10002,struct.pack('<4I6H',0,1,0,0,0,0,1,1,4,4))
    values=section(0x10003,struct.pack('<I',len(attrs))+b''.join(attrs))
    parts=[state,material,values];body=b''.join(parts)
    geometry=bytearray(48);geometry[:8]=b'GM1G4400'
    struct.pack_into('<I',geometry,8,48+len(body));struct.pack_into('<I',geometry,44,len(parts))
    result=bytearray(24);result[:8]=b'_M1G7300'
    struct.pack_into('<4I',result,8,24+len(geometry)+len(body),24,0,1)
    return result+geometry+body


def variable(name='fOwn',size=4,kind=3,columns=1):
    return dict(name=name,size=size,type=dict(variableClass=0 if columns==1 else 1,variableType=kind,
        rows=1,columns=columns,elements=0,members=0))


class MaterialTests(unittest.TestCase):
    def test_complete_fixture_and_unknown_state_body_preserved(self):
        raw=fixture();value=read_material_sections(raw)
        self.assertEqual(1,len(value['materials']))
        self.assertEqual([[0,0,1,1,4,4]],value['materials'][0]['rawTextureBindings'])
        self.assertEqual(68,value['rawStates']['bytes'])
        self.assertFalse(value['rawStates']['recordLayoutVerified'])
        self.assertFalse(value['alphaThresholdVerified']);self.assertFalse(value['runtimeMaterialUploadVerified'])
        own=value['namedAttributes'][0]['attributes'][0]
        self.assertEqual('fOwn',own['name']);self.assertEqual(struct.pack('<f',0.25).hex(),own['valueHex'])
        self.assertFalse(own['typeSemanticsVerified'])

    def test_truncated_header_chunk_and_attribute_rejected(self):
        raw=fixture()
        for size in (0,23,35,60,len(raw)-1):
            with self.subTest(size=size),self.assertRaises(ValueError):read_material_sections(raw[:size])

    def test_wrong_versions_chunk_count_and_declared_size_rejected(self):
        mutations=[]
        value=fixture();value[4:8]=b'8300';mutations.append(value)
        value=fixture();value[28:32]=b'5400';mutations.append(value)
        for at,word in ((8,1),(12,25),(20,0),(20,65),(32,0xfffffff0),(68,65)):
            value=fixture();struct.pack_into('<I',value,at,word);mutations.append(value)
        for value in mutations:
            with self.subTest(value=value[:48]),self.assertRaises(ValueError):read_material_sections(value)

    def test_different_material_record_counts_rejected(self):
        raw=fixture();parsed=read_material_sections(raw)
        at=next(s['offset'] for s in parsed['sections'] if s['type']=='00010003')
        struct.pack_into('<I',raw,at+8,2)
        with self.assertRaises(ValueError):read_material_sections(raw)

    def test_duplicate_or_unbounded_attribute_names_rejected(self):
        with self.assertRaises(ValueError):read_material_sections(fixture([attribute(),attribute()]))
        for name in (b'',b'own\0hidden',b'bad name',b'\xff'):
            with self.subTest(name=name),self.assertRaises(ValueError):read_material_sections(fixture([attribute(name)]))

    def test_attribute_length_and_payload_range_rejected(self):
        original=attribute()
        for at,word in ((0,16),(0,0xfffffffc),(4,0xfffffffc),(4,12)):
            value=bytearray(original);struct.pack_into('<I',value,at,word)
            with self.subTest(at=at,word=word),self.assertRaises(ValueError):read_material_sections(fixture([value]))
        with self.assertRaises(ValueError):read_material_sections(fixture([attribute(value=b'\x00')]))

    def test_section_padding_or_extra_unclaimed_bytes_rejected(self):
        raw=fixture()+b'\0\0\0\0';struct.pack_into('<I',raw,8,len(raw))
        with self.assertRaises(ValueError):read_material_sections(raw)
        with self.assertRaises(ValueError):read_material_sections(fixture(raw_state=b'\0'))

    def test_exact_name_size_and_reflection_interpretation(self):
        own=read_material_sections(fixture())['namedAttributes'][0]['attributes'][0]
        result=correlate_attribute(own,variable())
        self.assertTrue(result['nameAndByteSizeMatched']);self.assertEqual([0.25],result['interpretationFromReflectedType'])
        self.assertFalse(result['actualUploadVerified']);self.assertFalse(result['actualGamePassVerified'])
        wrong=correlate_attribute(own,variable(size=8,columns=2))
        self.assertFalse(wrong['nameAndByteSizeMatched']);self.assertIsNone(wrong['interpretationFromReflectedType'])
        with self.assertRaises(ValueError):correlate_attribute(own,variable(name='different'))

    def test_vector_signed_int_and_nonfinite_value(self):
        own=read_material_sections(fixture([attribute(value=struct.pack('<2f',0.25,0.75),raw_kind=2)]))['namedAttributes'][0]['attributes'][0]
        self.assertEqual([0.25,0.75],correlate_attribute(own,variable(size=8,columns=2))['interpretationFromReflectedType'])
        own=read_material_sections(fixture([attribute(value=struct.pack('<i',-7),raw_kind=5)]))['namedAttributes'][0]['attributes'][0]
        self.assertEqual([-7],correlate_attribute(own,variable(kind=2))['interpretationFromReflectedType'])
        own=read_material_sections(fixture([attribute(value=struct.pack('<f',float('nan')))]))['namedAttributes'][0]['attributes'][0]
        with self.assertRaises(ValueError):correlate_attribute(own,variable())


if __name__=='__main__':unittest.main(verbosity=2)

"""Synthetic DOK records only; never opens a game archive/process/GPU device."""
import json
import struct
import unittest

from audit_tree_shader_links import objects, public, shader_name_links
from shader_name_hash import name_hash
from audit_system_shader_links import dxbc


def record(props=(),tag=b'IDOK0000',identity=11,kind=12,context=0x10203040,tail=b''):
    header=28 if tag==b'RDOK0000' else 24
    descriptors=b''.join(struct.pack('<3I',t,n,name) for t,n,name,raw in props)
    values=b''.join(raw for t,n,name,raw in props)
    size=header+len(descriptors)+len(values)+len(tail)
    prefix=tag+struct.pack('<3I',size,identity,kind)
    if tag==b'RDOK0000':prefix+=struct.pack('<I',context)
    return prefix+struct.pack('<I',len(props))+descriptors+values+tail


def dok(*records):
    data=b'_DOK0000'+struct.pack('<5I',28,0,0,0,0)
    for item in records:
        data+=bytes((-len(data))%4)+item
    return data


class DokReaderTests(unittest.TestCase):
    def test_external_alignment_and_related_header_keep_next_record(self):
        data=dok(record([(12,1,1,b'X')]),record([(5,1,2,struct.pack('<I',23))],tag=b'RDOK0000'),record(identity=99))
        rows=list(objects(data))
        self.assertEqual(['IDOK0000','RDOK0000','IDOK0000'],[r['tag'] for r in rows])
        self.assertEqual('10203040',rows[1]['rawContext'])
        self.assertEqual(struct.pack('<I',23),rows[1]['props'][0]['raw'])
        self.assertEqual('00000063',rows[2]['id'])

    def test_embedded_record_tag_is_property_data(self):
        raw=b'IDOK0000'+record(identity=998)
        rows=list(objects(dok(record([(12,len(raw),1,raw)]))))
        self.assertEqual(1,len(rows))
        self.assertEqual(raw,rows[0]['props'][0]['raw'])

    def test_supported_element_lengths_preserve_raw_bytes(self):
        for kind in range(13):
            with self.subTest(kind=kind):
                size=1 if kind in (0,1,2,10,12) else 2 if kind==3 else 8 if kind==9 else 4
                raw=bytes(range(size*3))
                row=list(objects(dok(record([(kind,3,1,raw)]))))[0]
                self.assertIsNone(row['propertyParseError'])
                self.assertEqual(raw,row['props'][0]['raw'])

    def test_unknown_property_kind_keeps_bounded_record_and_neighbor(self):
        rows=list(objects(dok(record([(13,1,1,b'abcd')]),record(identity=99))))
        self.assertEqual(2,len(rows))
        self.assertIn('Unsupported property kind',rows[0]['propertyParseError'])
        self.assertEqual([],rows[0]['props'])

    def test_malformed_value_size_never_exposes_partial_properties(self):
        rows=list(objects(dok(record([(5,8,1,b'abcd')]),record(identity=99))))
        self.assertEqual(2,len(rows))
        self.assertIn('property bounds',rows[0]['propertyParseError'])
        self.assertEqual([],rows[0]['props'])

    def test_nonzero_property_tail_is_not_silently_ignored(self):
        row=list(objects(dok(record(tail=b'\x01'))))[0]
        self.assertIn('do not cover record',row['propertyParseError'])

    def test_invalid_header_lengths_and_count_reject_file(self):
        original=bytearray(dok(record()))
        for offset,value in ((8,27),(28+8,23),(28+8,100),(28+20,4097)):
            with self.subTest(offset=offset,value=value):
                changed=original.copy();struct.pack_into('<I',changed,offset,value)
                with self.assertRaises(ValueError):list(objects(changed))

    def test_non_record_and_truncated_tail_reject_file(self):
        for tail in (b'NOPE',b'IDOK0000',b'RDOK0000'):
            with self.subTest(tail=tail):
                with self.assertRaises(ValueError):list(objects(dok(record())+tail))

    def test_name_links_require_exact_encoding_and_preserve_field(self):
        name='CE1ResourceShaderStandard\uff3bSTD\uff3d'
        table={'rows':[['STD',name,'R_G1S\uff3bSTD\uff3d']]}
        for encoding in ('utf-8','utf-16-le','cp932'):
            with self.subTest(encoding=encoding):
                raw=name.encode(encoding)+(b'\0\0' if encoding=='utf-16-le' else b'\0')
                row=list(objects(dok(record([(12,len(raw),0xC69EB271,raw)]))))[0]
                row['source']={'fixture':True}
                links=shader_name_links(table,{'0000000B':row})
                self.assertEqual(1,len(links))
                self.assertEqual(encoding,links[0]['encoding'])
                self.assertEqual('C69EB271',links[0]['matchedProperty'])
                self.assertEqual(raw.hex(),links[0]['object']['props'][0]['rawHex'])
                self.assertEqual([],shader_name_links({'rows':[['STD',name+'x','R_G1S']]},{'id':row}))

    def test_public_representation_preserves_unsigned_bits(self):
        raw=struct.pack('<I',0xBF800000)
        node=list(objects(dok(record([(5,1,4,raw)]))))[0]
        self.assertEqual(['BF800000'],public(node)['props'][0]['rawUint32'])
        json.dumps(public(node))

    def test_hash_rules_are_distinct_signed_and_bounded(self):
        self.assertEqual(65*31,name_hash(b'A'))
        self.assertEqual(65*32,name_hash(b'A',group=True))
        self.assertEqual(65,name_hash(b'A',0,group=True))
        self.assertEqual(0,name_hash(b'A',0))
        self.assertEqual((-31)&0xFFFFFFFF,name_hash(b'\xFF'))
        self.assertEqual((-32)&0xFFFFFFFF,name_hash(b'\xFF',group=True))
        for group in (False,True):
            self.assertEqual(name_hash(b'A',group=group),name_hash(b'A\0BCD',group=group))
            self.assertEqual(name_hash(b'A',group=group),name_hash(b'ABCD',1,group=group))
            self.assertEqual(0,name_hash(b'',group=group))
            self.assertEqual(0,name_hash(b'\0A',group=group))

    def test_hash_invalid_count_is_rejected(self):
        for count in (-1,2):
            with self.assertRaises(ValueError):name_hash(b'A',count)

    def test_dxbc_offsets_are_relative_to_each_bounded_container(self):
        item=b'DXBC'+bytes(16)+struct.pack('<4I',1,48,1,36)+b'SHDR'+struct.pack('<I',4)+b'abcd'
        rows=dxbc(b'G1S-prefix'+item+b'next'+item)
        self.assertEqual([10,62],[r['offset'] for r in rows])
        self.assertEqual([48,48],[r['bytes'] for r in rows])
        self.assertEqual(rows[0]['sha256'],rows[1]['sha256'])

    def test_dxbc_header_and_chunks_reject_truncation_and_overflow(self):
        item=b'DXBC'+bytes(16)+struct.pack('<4I',1,48,1,36)+b'SHDR'+struct.pack('<I',4)+b'abcd'
        for offset,value in ((24,31),(24,49),(28,99),(32,0),(32,41),(40,5)):
            with self.subTest(offset=offset,value=value):
                changed=bytearray(item);struct.pack_into('<I',changed,offset,value)
                with self.assertRaises(ValueError):dxbc(changed)
        with self.assertRaises(ValueError):dxbc(b'DXBC'+bytes(20))


if __name__=='__main__':
    unittest.main(verbosity=2)

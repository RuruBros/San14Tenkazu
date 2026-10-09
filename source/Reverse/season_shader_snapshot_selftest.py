"""Synthetic read-only memory tests for recognized reflection snapshots."""
import hashlib
import struct
import unittest
from season_shader_snapshot import snapshot_reflection


class Memory:
    def __init__(self):
        self.data=bytearray(0x10000);self.reads=0;self.mutate=None
        struct.pack_into('<Q',self.data,0x100,0x1000)
        for slot in (3,6,7,8):
            struct.pack_into('<Q',self.data,0x1000+slot*8,0x2000+slot);self.data[0x2000+slot]=slot
        struct.pack_into('<I',self.data,0x110,0x50);struct.pack_into('<Q',self.data,0x118,0x6000)
        self.data[0x6000:0x6008]=b'Creator\0'
        struct.pack_into('<3I',self.data,0x110+24,1,1,1)
        for pointer,offset,stride in ((0x3000,0xf8,40),(0x4000,0x110,32),(0x5000,0x128,32)):
            struct.pack_into('<Q4xI',self.data,0x100+offset,pointer,1)
            struct.pack_into('<Q',self.data,pointer,0x7000);self.data[0x7000:0x7005]=b'Name\0'
        self.expected={slot:(1,hashlib.sha256(bytes([slot])).hexdigest()) for slot in (3,6,7,8)}
    def read(self,address,size):
        self.reads+=1
        result=bytes(self.data[address:address+size])
        if address==0x100 and self.mutate:
            struct.pack_into('<I',self.data,0x100+self.mutate,99)
        return result


class SnapshotTests(unittest.TestCase):
    def test_verified_layout_returns_binding_and_signature_names(self):
        m=Memory();value=snapshot_reflection(m.read,0x100,m.expected)
        self.assertEqual('Creator',value['creator']);self.assertEqual(32,value['signatureStride'])
        self.assertEqual('Name',value['metadata']['resources'][0]['name'])
        self.assertEqual('Name',value['metadata']['inputs'][0]['name'])
        self.assertEqual('Name',value['metadata']['outputs'][0]['name'])

    def test_other_getter_code_is_rejected_before_table_reads(self):
        m=Memory();m.data[0x2003]=99
        with self.assertRaisesRegex(ValueError,'Unrecognized'):snapshot_reflection(m.read,0x100,m.expected)
        self.assertEqual(3,m.reads)

    def test_table_count_mismatch_or_oversized_count_is_rejected(self):
        for count in (2,129):
            m=Memory();struct.pack_into('<I',m.data,0x100+0x104,count)
            with self.assertRaisesRegex(ValueError,'count'):snapshot_reflection(m.read,0x100,m.expected)

    def test_unreadable_copy_fails_without_executing_game_code(self):
        m=Memory()
        def read(address,size):return None if address==0x3000 else m.read(address,size)
        with self.assertRaisesRegex(ValueError,'unreadable'):snapshot_reflection(read,0x100,m.expected)

    def test_changed_metadata_is_rejected_but_external_reference_count_is_ignored(self):
        m=Memory();m.mutate=0x20
        with self.assertRaises(ValueError):snapshot_reflection(m.read,0x100,m.expected)
        m=Memory();m.mutate=8
        self.assertEqual('Creator',snapshot_reflection(m.read,0x100,m.expected)['creator'])


if __name__=='__main__':unittest.main(verbosity=2)

import struct
import unittest
from pathlib import Path
from unittest.mock import patch

from ai_frozen_preflight import FrozenImage, validate_pool


class Memory:
    def __init__(self):self.base=0x10000;self.data=bytearray(4096);self.missing=False
    def q(self,offset,value):struct.pack_into('<Q',self.data,offset,value)
    def read(self,address,length):
        return None if self.missing or not self.base <= address <= self.base+len(self.data)-length else bytes(self.data[address-self.base:address-self.base+length])


def sample():
    m=Memory();header=0x100
    # Two node slots, one active list/index, one free node. Synthetic validator
    # inputs only; these are never passed to a game function or coverage claim.
    for f,v in {8:m.base+0x300,0x10:m.base+0x500,0x18:m.base+0x508,0x20:m.base+0x318,
                0x28:m.base+0x510,0x30:1,0x38:2,0x40:1,0x50:m.base+0x400,
                0x58:m.base+0x400,0x60:m.base+0x400,0x68:0,0x78:1,0x80:1}.items():m.q(header+f,v)
    m.q(0x500,m.base+0x300);m.q(0x508,m.base+0x300);m.q(0x510,1)
    return m


class FrozenPreflightTests(unittest.TestCase):
    def test_intact_pool_coverage_and_counts(self):
        m=sample();r=validate_pool(m.read,m.base+0x100)
        self.assertTrue(r['valid']);self.assertEqual((1,1,1,0),(r['activeNodes'],r['freeNodes'],r['activeIndices'],r['freeIndices']))

    def test_missing_pages_and_bad_dimensions_are_rejected(self):
        m=sample();original=m.read
        def missing_page(a,n):return None if a==m.base and n==4096 else original(a,n)
        r=validate_pool(missing_page,m.base+0x100)
        self.assertFalse(r['valid']);self.assertEqual([m.base],r['missingPages'])
        m.q(0x100+0x40,0xFFFFFFFFFFFFFFFF)
        self.assertEqual('unexpected pool dimensions',validate_pool(m.read,m.base+0x100)['reason'])

    def test_cycles_shared_nodes_and_bad_counts_are_rejected(self):
        for offset,value in ((0x308,0x10300),(0x100+0x20,0x10300),(0x510,2),(0x310,0x10318),(0x400,1)):
            with self.subTest(offset=offset):
                m=sample();m.q(offset,value)
                self.assertFalse(validate_pool(m.read,m.base+0x100)['valid'])

    def test_text_mismatch_stops_before_any_fill(self):
        class Image:
            base=0x200000;img=bytes(0x5000);sections={'.text':(0x1000,0x2000)}
        im=Image()
        def load(g,path):
            g.exe=im.base;g.exe_size=len(im.img);g.pages={g.exe+0x1000:b'X'+bytes(4095)}
        with patch('ai_frozen_preflight.FrozenGame.__init__',load),patch('ai_frozen_preflight.relocated_text',return_value=(0x1000,bytes(0x2000),[])):
            with self.assertRaisesRegex(ValueError,'captured text differs'):
                with patch.object(Path,'read_bytes',return_value=b'fixed fixture'):
                    FrozenImage(Path('unused'),im,True)

    def test_text_fill_leaves_mutable_holes_unreadable(self):
        class Image:
            base=0x200000;img=bytes(0x5000);sections={'.text':(0x1000,0x2000)}
        im=Image()
        def load(g,path):
            g.exe=im.base;g.exe_size=len(im.img);g.pages={g.exe+0x1000:bytes(4096)}
        with patch('ai_frozen_preflight.FrozenGame.__init__',load),patch('ai_frozen_preflight.relocated_text',return_value=(0x1000,bytes(0x2000),[])):
            with patch.object(Path,'read_bytes',return_value=b'fixed fixture'):
                g=FrozenImage(Path('unused'),im,True)
            self.assertIsNotNone(g.read(g.exe+0x2000,4096))
            self.assertIsNone(g.read(g.exe+0x4000,1))
            self.assertEqual(0,g.code_source['mutablePagesAdded'])
            self.assertFalse(g.pool_snapshot['valid'])

    def test_file_changed_during_load_is_rejected(self):
        with patch('ai_frozen_preflight.FrozenGame.__init__',return_value=None),patch.object(Path,'read_bytes',side_effect=[b'first',b'second']):
            with self.assertRaisesRegex(ValueError,'snapshot file changed'):
                FrozenImage(Path('unused'),None,True)


if __name__=='__main__':unittest.main()

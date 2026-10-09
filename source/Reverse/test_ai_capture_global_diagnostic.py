# Synthetic byte packets test diagnostic bookkeeping, never native game behavior.
from types import SimpleNamespace
import unittest
from unittest.mock import patch
from ai_capture_global_diagnostic import compare


def reader(changes=(),short=False):
    pointer=0x1008;size=0x31330;length=((pointer+size+4095)&-4096)-0x1000
    first=bytes(length);second=bytearray(first)
    for offset in changes:second[pointer-0x1000+offset]=1
    reads=iter((first,bytes(second[:-1] if short else second)))
    return SimpleNamespace(exe=0,q=lambda address:pointer,read=lambda address,count:next(reads))


class GlobalDiagnosticTests(unittest.TestCase):
    def test_stable_pages_have_no_changed_bytes(self):
        with patch('ai_capture_global_diagnostic.read_clock',return_value={'world':1}):result=compare(reader())
        self.assertEqual([],result['changedByteOffsets']);self.assertTrue(result['clockStable'])
        self.assertEqual(0,result['nativeCalls']);self.assertEqual(0,result['gameWrites'])

    def test_object_boundaries_are_distinguished_from_neighboring_bytes(self):
        offsets=[-1,0,0x3132F,0x31330]
        with patch('ai_capture_global_diagnostic.read_clock',return_value={'world':1}):result=compare(reader(offsets))
        self.assertEqual(offsets,result['changedByteOffsets'])
        self.assertEqual([0,0x3132F],result['changedInsideAllocation'])

    def test_incomplete_reads_are_rejected(self):
        with patch('ai_capture_global_diagnostic.read_clock',return_value={'world':1}):
            with self.assertRaisesRegex(ValueError,'incomplete'):compare(reader(short=True))

    def test_changed_clock_is_reported_even_when_object_bytes_match(self):
        with patch('ai_capture_global_diagnostic.read_clock',side_effect=[{'world':1},{'world':2}]):result=compare(reader())
        self.assertFalse(result['clockStable']);self.assertEqual([],result['changedByteOffsets'])


if __name__=='__main__':unittest.main()

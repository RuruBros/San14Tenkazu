# Control/cache safety tests only; these packets are never native evidence.
from pathlib import Path
from types import SimpleNamespace
import tempfile
import json
import unittest
from unittest.mock import patch

from ai_remaining_batch import CapturePages, capture, inventory, main
from oracle import OracleError


class RemainingBatchTests(unittest.TestCase):
    def test_page_copy_preserves_cross_page_bytes_and_caches_original_reads(self):
        calls=[];pages={0x1000:bytes(range(256))*16,0x2000:b'B'*4096}
        def read(address,size):
            calls.append((address,size));return pages.get(address)
        copied=CapturePages(SimpleNamespace(read=read))
        self.assertEqual(b'\xfe\xffBBB',copied.read(0x1FFE,5))
        self.assertEqual(b'\xfe\xffBBB',copied.read(0x1FFE,5))
        self.assertEqual([(0x1000,4096),(0x2000,4096)],calls)
        self.assertEqual(pages,copied.pages)

    def test_missing_page_is_not_filled_with_zero_bytes(self):
        copied=CapturePages(SimpleNamespace(read=lambda address,size:None))
        with self.assertRaisesRegex(OracleError,'unreadable 0x3000'):copied.read(0x3000,4)
        self.assertEqual({},copied.pages)

    def test_existing_destination_stops_before_live_process_open(self):
        with tempfile.TemporaryDirectory(prefix='ai-capture-test-') as folder:
            path=Path(folder)/'existing.pkl';path.write_bytes(b'keep')
            with patch('ai_native_snapshot.ListPoolSnapshot',side_effect=AssertionError('must not open game')):
                with self.assertRaisesRegex(ValueError,'destination already exists'):capture(path)
            self.assertEqual(b'keep',path.read_bytes())

    def test_inventory_rejects_bad_frozen_pool_before_native_calls(self):
        with patch('ai_remaining_batch.Image'),patch('ai_remaining_batch.FrozenImage',return_value=SimpleNamespace(pool_snapshot={'valid':False})),patch('ai_remaining_batch.Harness',side_effect=AssertionError('must not call native code')):
            with self.assertRaisesRegex(ValueError,'frozen pool validation failed'):inventory(Path('unused'))

    def test_system_exit_guard_is_recorded_without_turning_it_into_success(self):
        with tempfile.TemporaryDirectory(prefix='ai-capture-failure-') as folder:
            output=Path(folder)/'failure.json'
            arguments=['ai_remaining_batch.py','capture','--snapshot',str(Path(folder)/'unused.pkl'),'--output',str(output)]
            with patch('sys.argv',arguments),patch('ai_remaining_batch.capture',side_effect=SystemExit('global context capture changed')):
                with self.assertRaises(SystemExit):main()
            result=json.loads(output.read_bytes())
            self.assertFalse(result['completed']);self.assertEqual(0,result['nativeCalls'])
            self.assertEqual('global context capture changed',result['error'])


if __name__=='__main__':unittest.main()

# Recorder/audit workflow tests. Synthetic frames are never native formula vectors.
import contextlib
import gzip
import io
import json
import struct
import shutil
import unittest
import uuid
from pathlib import Path
from unittest.mock import patch

import audit_capture_observation as verifier
import observe_capture as observer
import world


class PointerSource:
    exe = 0x140000000
    raw = 0x100000

    def q(self, address):
        return self.raw

    def read(self, address, length):
        key = address - self.raw
        _, size, count = world.TABLES[key]
        start = 0x2000000 + observer.KEYS.index(key) * 0x1000000
        return struct.pack('<' + 'Q' * count, *(start + i * size for i in range(count)))


class Clock:
    def __init__(self):
        self.now = 0

    def monotonic(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


def frame(status=4, ruler=260):
    result = {key: bytearray(world.TABLES[key][1] * world.TABLES[key][2]) for key in observer.KEYS}
    officer = world.TABLES[0x73A50][1]
    struct.pack_into('<H', result[0x73A50], officer + 0x10, 260)
    struct.pack_into('<H', result[0x73A50], officer + 0xE4, 62)
    result[0x73A50][officer + 0xE6] = status
    struct.pack_into('<H', result[0xDF30], world.TABLES[0xDF30][1] + 0x10, ruler)
    return {key: bytes(data) for key, data in result.items()}


class ObservationWorkflowTests(unittest.TestCase):
    def setUp(self):
        cache = (Path(__file__).resolve().parent / '.cache').resolve()
        cache.mkdir(exist_ok=True)
        # Python's Windows mode-0700 temporary directories exclude restricted test tokens.
        # Inherit the workspace cache ACL and check the exact cleanup target before removal.
        temp = (cache / ('capture-observation-test-' + uuid.uuid4().hex)).resolve()
        self.assertTrue(temp.is_relative_to(cache))
        temp.mkdir()
        self.addCleanup(shutil.rmtree, temp)
        self.root = temp / 'observation'
        self.assertTrue(self.root.is_relative_to(cache))

    def record(self, sequence, seconds=.075):
        clock = Clock()
        with patch.object(observer, 'read_frame', side_effect=sequence), contextlib.redirect_stdout(io.StringIO()):
            return observer.record(PointerSource(), self.root, seconds, clock.monotonic, clock.sleep)

    def test_force_removal_without_officer_status_change_is_retained(self):
        a, b = frame(), frame(ruler=0)
        result = self.record([a, a, b, b, b, b])
        self.assertEqual((result['events'], result['force_removals'], result['prisoner_entries']), (1, 1, 0))
        event = json.loads((self.root / 'transitions.jsonl').read_text())
        self.assertEqual(event['officers'], [])
        self.assertEqual((event['forces'][0]['ruler_before'], event['forces'][0]['ruler_after']), (260, 0))
        checked = verifier.audit(self.root)
        self.assertEqual(checked['counts']['force_removals'], 1)
        self.assertEqual(checked['last_forces'], [])

    def test_unstable_read_is_skipped_before_prisoner_entry(self):
        a, b = frame(), frame(status=6)
        result = self.record([a, a, b, a, b, b, b, b], seconds=.11)
        self.assertEqual((result['unstable_polls'], result['events'], result['prisoner_entries']), (1, 1, 1))
        checked = verifier.audit(self.root)
        self.assertEqual(checked['groups'][0]['before_status'], 4)
        self.assertEqual(checked['groups'][0]['after_status'], 6)

    def test_read_stop_or_interrupt_preserves_last_accepted_frame(self):
        for error in (RuntimeError('Process memory disappeared'), KeyboardInterrupt()):
            with self.subTest(error=type(error).__name__):
                self.root = self.root.parent / type(error).__name__
                a, b = frame(), frame(status=6)
                result = self.record([a, a, b, b, error], seconds=1)
                self.assertFalse(result['completed'])
                self.assertEqual(result['events'], 1)
                last = json.loads(gzip.decompress((self.root / 'last_tables.json.gz').read_bytes()))
                self.assertEqual(bytes.fromhex(last['0x73a50']['data']), b[0x73A50])
                self.assertEqual(verifier.audit(self.root)['counts']['prisoner_entries'], 1)

    def test_audit_rejects_omitted_force_transition(self):
        a, b = frame(), frame(ruler=0)
        self.record([a, a, b, b, b, b])
        path = self.root / 'transitions.jsonl'
        event = json.loads(path.read_text())
        event['forces'] = []
        path.write_text(json.dumps(event) + '\n', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'force change is missing'):
            verifier.audit(self.root)

    def test_audit_rejects_corrupted_status_bytes(self):
        a, b = frame(), frame(status=6)
        self.record([a, a, b, b, b, b])
        path = self.root / 'transitions.jsonl'
        event = json.loads(path.read_text())
        event['officers'][0]['status_after'] = 4
        path.write_text(json.dumps(event) + '\n', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'Status transition differs'):
            verifier.audit(self.root)

    def test_zero_duration_retains_baseline_and_final_frame(self):
        a = frame()
        result = self.record([a, a], seconds=0)
        self.assertTrue(result['completed'])
        self.assertEqual(result['polls'], 0)
        checked = verifier.audit(self.root)
        self.assertEqual(checked['initial_forces'], checked['last_forces'])

    def test_audit_rejects_unlogged_final_transition(self):
        a = frame()
        self.record([a, a], seconds=0)
        path = self.root / 'last_tables.json.gz'
        value = json.loads(gzip.decompress(path.read_bytes()))
        data = bytearray.fromhex(value['0x73a50']['data'])
        data[world.TABLES[0x73A50][1] + 0xE6] = 6
        value['0x73a50']['data'] = data.hex()
        path.write_bytes(gzip.compress(json.dumps(value).encode('utf-8')))
        with self.assertRaisesRegex(ValueError, 'unlogged transition appears'):
            verifier.audit(self.root)

    def test_changed_pointer_array_is_rejected(self):
        tables = {key: (*world.TABLES[key], 0x2000000 + n * 0x1000000)
            for n, key in enumerate(observer.KEYS)}
        game = PointerSource()
        with patch.object(game, 'read', return_value=None):
            with self.assertRaisesRegex(RuntimeError, 'Record table pointers changed'):
                observer.read_frame(game, game.raw, tables)


if __name__ == '__main__':
    unittest.main()

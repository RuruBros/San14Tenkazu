import unittest
from types import SimpleNamespace
from frozen_code import add_verified_text


class FrozenCodeTests(unittest.TestCase):
    def setUp(self):
        self.image = SimpleNamespace(sections={'.text': (0x1000, 0x3100)},
            img=b'X' * 0x1000 + b'A' * 0x1000 + b'B' * 0x1000 + b'C' * 0x1000 + b'D' * 0x100)
        self.code = self.image.img[0x1000:]
        self.dump = dict(exe=0x100000, pages={0x101000: b'A' * 4096,
            0x104000: b'partial-page-must-not-change', 0x200000: b'old-heap-state'}, imports={'test': 1})

    def test_rebases_code_only_and_preserves_original_pages_and_metadata(self):
        prepared = add_verified_text(self.dump, self.image, self.code)
        self.assertEqual(prepared['pages'][0x102000], b'B' * 4096)
        self.assertEqual(prepared['pages'][0x103000], b'C' * 4096)
        for page, value in self.dump['pages'].items(): self.assertEqual(prepared['pages'][page], value)
        self.assertNotIn(0x102000, self.dump['pages'])
        self.assertEqual(prepared['imports'], self.dump['imports'])
        self.assertEqual(prepared['immutable_code_evidence']['mutable_pages_added'], 0)
        self.assertEqual(prepared['immutable_code_evidence']['added_text_rvas'], ['0x2000', '0x3000'])

    def test_rejects_changed_live_code(self):
        with self.assertRaisesRegex(RuntimeError, 'live executable'):
            add_verified_text(self.dump, self.image, b'!' + self.code[1:])

    def test_rejects_changed_original_code(self):
        bad = dict(self.dump, pages=dict(self.dump['pages']))
        bad['pages'][0x101000] = b'!' * 4096
        with self.assertRaisesRegex(RuntimeError, 'Original executable'):
            add_verified_text(bad, self.image, self.code)

    def test_requires_captured_executable_evidence(self):
        with self.assertRaisesRegex(RuntimeError, 'No original executable'):
            add_verified_text(dict(self.dump, pages={0x200000: b'old-heap-state'}), self.image, self.code)


if __name__ == '__main__': unittest.main()

# Analyzer bookkeeping tests use altered log copies, never fabricated game evidence.
import copy
import gzip
import json
import unittest
from ai_path_cost_contract import ANCHORS, build, check_code, check_run
from ai_path_triage import HERE, FIXTURES


class CostContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = build([HERE.parent / 'Tests' / (n + '.json.gz') for n in FIXTURES],
                           HERE.parent / 'Tests' / 'AiPathHelperVectors.json')
        fixture = json.loads(gzip.decompress((HERE.parent / 'Tests' / 'AiPathReinsertVectors.json.gz').read_bytes()))
        cls.sample_run = next(r for r in fixture['cases'] if r['options'] == 0 and len(r['relaxations']) > 10)

    def test_actual_saved_evidence_and_missing_edges_remain_separate(self):
        self.assertEqual(dict(searches=1098, decisions=146182, penalizedSteps=4, acceptedNewNodes=32616), self.report['observations'])
        self.assertEqual(967980000, self.report['contract']['maximumSimplePathCost'])
        self.assertTrue(all(not r['observed'] for r in self.report['scopedDirections']))
        self.assertEqual({0x184207, 0x185454}, {r['rva'] for r in self.report['scopedDirections']})

    def test_code_anchor_change_rejects_an_old_argument(self):
        # Minimal instruction text tests signature checking, not game execution.
        flow = {entry: '\n'.join(f'{rva:x} {text}' for rva, text in expected.items()) for entry, expected in ANCHORS.items()}
        flow[0x1864F0] = flow[0x1864F0].replace('test dl, 1', 'test dl, 2')
        with self.assertRaisesRegex(ValueError, 'Code anchor changed'):
            check_code(flow)

    def test_changed_destination_cost_is_rejected(self):
        run = copy.deepcopy(self.sample_run)
        row = copy.deepcopy(run['relaxations'][0])
        row['stepCost'] = 20000
        run['relaxations'].insert(1, row)
        with self.assertRaises(ValueError):
            check_run(run)

    def test_existing_node_improvement_is_rejected(self):
        run = copy.deepcopy(self.sample_run)
        run['relaxations'][0].update(flags=1, accepted=True)
        with self.assertRaisesRegex(ValueError, 'existing-node improvement'):
            check_run(run)

    def test_heuristic_input_is_outside_this_contract(self):
        for options in (1, 4, 5, 65):
            run = copy.deepcopy(self.sample_run)
            run['options'] = options
            self.assertIsNone(check_run(run))


if __name__ == '__main__':
    unittest.main()

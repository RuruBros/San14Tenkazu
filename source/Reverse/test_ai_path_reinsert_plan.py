# Planner validation does not turn a modeled candidate into native backend evidence.
import copy
import gzip
import json
import unittest
from ai_path_reinsert_plan import HERE, Neighbors, build, inputs, model
from image import Image


class PlanTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = HERE.parent / 'Tests' / 'AiPathReinsertVectors.json.gz'
        cls.im = Image()
        cls.report = build(cls.path, cls.im)
        cls.runs = json.loads(gzip.decompress(cls.path.read_bytes()))['cases']

    def test_all_original_heuristic_search_decisions_are_reproduced(self):
        self.assertEqual(120, len(self.report['validatedSearches']))
        self.assertEqual(111139, self.report['validatedDecisions'])
        self.assertGreater(self.report['originalNeighborCalls'], 0)

    def test_two_small_recipes_preserve_pending_native_status(self):
        a, b = self.report['trials']
        self.assertEqual([0x184230, 0x18547D], [a['target'], b['target']])
        self.assertEqual(5, len(a['allowedHexes']))
        self.assertEqual(16, len(a['excludedHexes']))
        self.assertEqual(139, b['limit'])
        self.assertNotIn('excludedHexes', b)
        self.assertTrue(all(not r['nativeObserved'] for r in self.report['trials']))
        self.assertFalse(self.report['globalListTrial']['nativeObserved'])
        self.assertEqual(48, a['predictions'][0]['oldCost'])
        self.assertEqual(45, a['predictions'][0]['newCost'])
        self.assertEqual(139, b['predictions'][0]['oldCost'])
        self.assertEqual(136, b['predictions'][0]['newCost'])

    def test_bounded_backend_cannot_be_planned_with_an_ignored_exclusion_list(self):
        run = self.runs[self.report['trials'][1]['case']]
        with self.assertRaisesRegex(ValueError, 'does not consult'):
            model(run, Neighbors(self.im), allowed=set())

    def test_changed_cost_and_changed_world_inputs_are_rejected(self):
        run = copy.deepcopy(self.runs[1])
        row = copy.deepcopy(run['relaxations'][0]); row['stepCost'] += 1
        run['relaxations'].append(row)
        with self.assertRaisesRegex(ValueError, 'cost'):
            inputs(run)
        run = copy.deepcopy(self.runs[1]); run['changes'] = [dict(description='controller test packet')]
        with self.assertRaisesRegex(ValueError, 'unchanged world'):
            inputs(run)


if __name__ == '__main__': unittest.main()

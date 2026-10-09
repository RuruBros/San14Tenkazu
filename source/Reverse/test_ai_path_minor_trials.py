# Controller tests only: synthetic packets never execute or supply native evidence.
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
from ai_path_minor_trials import GateObservers, build, select_army


class MinorTrialTests(unittest.TestCase):
    def test_selector_requires_actual_minor_owner_special_predicate_and_valid_source(self):
        baseline=dict(slot=2,owner=46,special=1,source=100)
        self.assertEqual(baseline,select_army([dict(baseline,slot=5),baseline]))
        for change in ({'owner':45},{'owner':51},{'special':0},{'source':48400}):
            with self.assertRaisesRegex(ValueError,'no actual'):select_army([{**baseline,**change}])

    def test_invalid_frozen_pool_stops_before_any_native_harness(self):
        with patch('ai_path_minor_trials.FrozenImage',return_value=SimpleNamespace(pool_snapshot={'valid':False})),patch('ai_path_minor_trials.Harness') as harness:
            with self.assertRaisesRegex(ValueError,'pool invalid'):build(Path('unused'))
            harness.assert_not_called()

    def test_observer_passes_each_backend_as_unicorn_user_data(self):
        calls=[]
        o=SimpleNamespace(exe=0x100000,mu=SimpleNamespace(hook_add=lambda *args:calls.append(args)))
        GateObservers(SimpleNamespace(o=o),{0x183A30:[SimpleNamespace(address=0x1843AE)],0x184EB0:[SimpleNamespace(address=0x1855FC)]})
        self.assertEqual([0x183A30,0x184EB0],[args[2] for args in calls])
        self.assertTrue(all(len(args)==5 for args in calls))


if __name__=='__main__':unittest.main()

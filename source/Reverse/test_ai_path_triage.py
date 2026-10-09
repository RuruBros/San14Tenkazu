# Tests for offline scheduling bookkeeping; synthetic packets never execute game code.
# python test_ai_path_triage.py --inventory <saved-inventory.json> --admission <saved-admission.json> -v
import argparse
import copy
from pathlib import Path
import tempfile
import unittest
import json
from ai_path_coverage import ledger
from ai_path_triage import HERE,FIXTURES,build,classify,read_evidence


class TriageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.paths=[HERE.parent/'Tests'/(name+'.json.gz') for name in FIXTURES]
        cls.inventory,_=read_evidence(INVENTORY);cls.admission,_=read_evidence(ADMISSION)
        cls.contracts,_=read_evidence(HERE.parent/'Tests/AiPathStateContracts.json.gz')
        cls.report=ledger(cls.paths);cls.summary=build(cls.paths,INVENTORY,ADMISSION,HERE.parent/'Tests/AiPathStateContracts.json.gz')

    def test_actual_evidence_preserves_missing_directions_and_counts_small_trial_gain(self):
        r=self.summary
        self.assertEqual(2662,r['completedNativeSearches'])
        self.assertEqual(dict(calls=2,newEdges=3),r['lastBatch'])
        self.assertEqual(20,r['missingDirectionCount'])
        self.assertEqual(dict(code_contract_scoped=8,outside_caller_contract=6,changed_state=5,code_analysis=1),r['categories'])
        self.assertEqual([0x183ECB],[r['rva'] for r in r['nextStaticTargets']])
        self.assertEqual([],r['nextNativeTargets'])
        self.assertTrue(all(not row['observed'] for row in r['targets']))
        missing={(b,row['rva'],side) for b,backend in self.report['backends'].items() for row in backend['conditions'] for side in row['missingEdges']}
        classified={(row['backend'],row['rva'],row['missingEdge']) for row in r['targets']}
        self.assertEqual(missing,classified)

    def test_changed_instruction_does_not_inherit_an_old_code_contract(self):
        report=copy.deepcopy(self.report)
        branch=next(b for b in report['backends']['0x183a30']['conditions'] if b['rva']==0x184073)
        branch['text']='je 0x1842df'
        row=next(r for r in classify(report,self.inventory,self.admission) if r['rva']==0x184073)
        self.assertEqual('unclassified',row['category'])
        self.assertEqual('inspect_code',row['nextAction'])
        self.assertFalse(row['observed'])

    def test_null_directions_remain_state_targets_without_the_constructor_evidence(self):
        rows=classify(self.report,self.inventory,self.admission)
        nulls=[row for row in rows if row['rva'] in (0x183F8A,0x18520C)]
        self.assertEqual(2,len(nulls))
        self.assertTrue(all(row['category']=='changed_state' and not row['observed'] for row in nulls))

    def test_contradictory_or_different_world_constructor_evidence_is_rejected(self):
        for change in ({'verified':False},{'nullCount':1}):
            contracts=copy.deepcopy(self.contracts);contracts['tileTable'].update(change)
            with self.assertRaisesRegex(ValueError,'invalid or contradictory'):classify(self.report,self.inventory,self.admission,contracts)
        contracts=copy.deepcopy(self.contracts);contracts['clockBefore']['world']+=1
        with self.assertRaisesRegex(ValueError,'different frozen'):classify(self.report,self.inventory,self.admission,contracts)

    def test_mismatched_world_evidence_is_rejected(self):
        admission=copy.deepcopy(self.admission);admission['clockBefore']['world']+=1
        with self.assertRaisesRegex(ValueError,'different worlds'):classify(self.report,self.inventory,admission)

    def test_unstable_log_is_rejected(self):
        value=copy.deepcopy(self.inventory);value['clockStable']=False
        with tempfile.TemporaryDirectory(prefix='ai-triage-test-') as folder:
            path=Path(folder)/'unstable.json';path.write_text(json.dumps(value),encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'Unstable'):read_evidence(path)

    def test_synthetic_scheduling_packet_opens_only_its_exact_facility_category(self):
        # A controller test packet, not evidence of a native return or a game state.
        admission=copy.deepcopy(self.admission)
        site=next(s for s in admission['sites'] if s['category']==3)
        admission['admittedCategory2Or3']=[dict(site=site['buildingId'],native=1)]
        rows=classify(self.report,self.inventory,admission)
        eligible=[r['rva'] for r in rows if r['nextAction']=='derive_small_native_trial']
        self.assertEqual([0x1840E8],eligible)
        self.assertTrue(all(not r['observed'] for r in rows))


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--inventory',type=Path,required=True);parser.add_argument('--admission',type=Path,required=True)
    args,other=parser.parse_known_args();INVENTORY=args.inventory;ADMISSION=args.admission
    unittest.main(argv=[__file__,*other])

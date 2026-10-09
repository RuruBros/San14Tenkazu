from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from ai_path_fire_snapshot import candidate_tiles, replay


class PathFireSnapshotTests(unittest.TestCase):
    def test_bad_pool_stops_before_native_verify(self):
        game=SimpleNamespace(snapshot_sha256='fixture',code_source={},pool_snapshot={'valid':False})
        with patch('ai_path_fire_snapshot.Image',return_value=SimpleNamespace(img=b'fixture')),patch('ai_path_fire_snapshot.FrozenImage',return_value=game),patch('ai_path_fire_snapshot.verify',side_effect=AssertionError('native call must not start')):
            result=replay(Path('unused'))
        self.assertFalse(result['completed'])
        self.assertFalse(result['gameAccess'])
        self.assertEqual([],result['helperReplacements'])
        self.assertEqual([],result['timerPokes'])

    def test_different_world_pointer_stops_candidate_selection(self):
        game=SimpleNamespace(exe=0x1000,base=0x2290,q=lambda address:0x3000)
        with self.assertRaisesRegex(ValueError,'world identity'):
            candidate_tiles(game)

    def test_existing_fire_uses_its_actual_timer_without_ignite_or_timer_write(self):
        game=SimpleNamespace(snapshot_sha256='fixture',code_source={},pool_snapshot={'valid':True})
        prefix=bytearray(32);prefix[0x16:0x18]=(40).to_bytes(2,'little')
        o=SimpleNamespace(exe=0x1000,read=lambda address,size:bytes(prefix),call32=lambda *args:1,
                          call=lambda *args,**kwargs:self.fail('existing fire must not invoke Ignite'),
                          mu=SimpleNamespace(hook_add=lambda *args:None))
        def verify(game,smoke,rows,prepare):
            preparation=prepare(SimpleNamespace(o=o))
            return dict(preparation=preparation,cases=rows,failures=[],clockStable=True,
                        branchCoverage={'0x183a30':[dict(rva=0x184084,taken=True)]})
        candidate=dict(hex=10,tile=0x5000,timer=40,origin='saved frozen tile')
        with patch('ai_path_fire_snapshot.Image',return_value=SimpleNamespace(img=b'fixture')),patch('ai_path_fire_snapshot.FrozenImage',return_value=game),patch('ai_path_fire_snapshot.candidate_tiles',return_value=([1]*20,[candidate],[])),patch('ai_path_fire_snapshot.separated_neighbors',return_value=(8,12)),patch('ai_path_fire_snapshot.verify',side_effect=verify):
            result=replay(Path('unused'))
        self.assertTrue(result['completed'])
        self.assertEqual(40,result['preparation']['site']['timer'])
        self.assertEqual(2,len(result['cases']))
        self.assertEqual([0,1],[row['fireFilter'] for row in result['cases']])
        attempt=result['preparation']['attempts'][0]
        self.assertFalse(attempt['igniteStarted'])
        self.assertEqual(attempt['before'],attempt['after'])


if __name__=='__main__':unittest.main()

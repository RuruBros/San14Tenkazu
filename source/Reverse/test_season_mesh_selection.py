"""Selection contract only; never opens the game or dispatches a diagnostic."""
import copy
import unittest
from season_draw_observe import mesh_read_requests


class MeshSelectionTests(unittest.TestCase):
    def rows(self):
        return [dict(count=n,start=0,thread=7,views=[dict(object=hex(n),resource=hex(n+1),format=97,
            width=512,height=256 if n==918 else 512,mipLevels=7 if n==918 else 8,arraySize=1)])
            for n in (918,1104,1179)]

    def test_exact_three_resources_share_one_thread(self):
        result=mesh_read_requests(self.rows(),[918,1104,1179],True)
        self.assertEqual([r.indexCount for r in result],[918,1104,1179])
        self.assertTrue(all(r.requireColor and r.capacity==4194304 for r in result))

    def test_ambiguous_resource_and_foreign_thread_rejected(self):
        rows=self.rows();duplicate=copy.deepcopy(rows[1]);duplicate['views'][0]['object']='0xabcd'
        with self.assertRaises(ValueError):mesh_read_requests(rows+[duplicate],[1104],True)
        rows[1]['thread']=8
        with self.assertRaises(ValueError):mesh_read_requests(rows,[918,1104],True)

    def test_count_bounds_and_unrecorded_or_wrong_descriptors_rejected(self):
        for counts in ([],[918]*2,[3],[918,1104,1179,90]):
            with self.assertRaises(ValueError):mesh_read_requests(self.rows(),counts,True)
        for field,value in [('width',1024),('height',256),('mipLevels',7),('format',99),('arraySize',2)]:
            rows=self.rows();rows[1]['views'][0][field]=value
            with self.assertRaises(ValueError):mesh_read_requests(rows,[1104],True)
        rows=self.rows();rows[1]['start']=1
        with self.assertRaises(ValueError):mesh_read_requests(rows,[1104],True)


if __name__=='__main__':unittest.main()

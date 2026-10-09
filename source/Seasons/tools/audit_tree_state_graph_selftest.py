"""Bounded synthetic graph fixtures; never reads archives, game, or GPU."""
import unittest

from audit_tree_state_graph import reachable_shader_objects,shader_fields


def node(resource='ABCDEF01',states=(),digest='fixture'):
    return dict(propertyParseError=None,rawSha256=digest,props=[
        dict(name='193025DF',kind=5,count=1,rawUint32=[resource]),
        dict(name='DB59AAFC',kind=5,count=len(states),rawUint32=list(states))])


class GraphTests(unittest.TestCase):
    def test_record_versions_and_array_positions_preserved(self):
        versions=[node(states=['00000000','00000002'],digest='first'),node(states=['00000003'],digest='second')]
        nodes={'00000001':versions,'00000002':[node()],'00000003':[node()]}
        reachable,edges=reachable_shader_objects(nodes,{'00000001'})
        self.assertEqual(3,len(reachable));self.assertEqual(2,len(reachable['00000001']))
        self.assertEqual([('first',0,'00000000',True),('first',1,'00000002',False),('second',0,'00000003',False)],
            [(e['sourceRecordSha256'],e['rawArrayIndex'],e['targetObject'],e['nullReference']) for e in edges])

    def test_cycle_and_duplicate_references_terminate_without_losing_edges(self):
        nodes={'00000001':[node(states=['00000002','00000002'])],
               '00000002':[node(states=['00000001'])]}
        reachable,edges=reachable_shader_objects(nodes,{'00000001'})
        self.assertEqual(2,len(reachable));self.assertEqual(3,len(edges))

    def test_missing_child_and_excess_graph_fail_closed(self):
        nodes={'00000001':[node(states=['00000002'])]}
        with self.assertRaises(ValueError):reachable_shader_objects(nodes,{'00000001'})
        nodes['00000002']=[node()]
        with self.assertRaises(ValueError):reachable_shader_objects(nodes,{'00000001'},limit=1)

    def test_changed_schema_counts_and_invalid_keys_rejected(self):
        bad=[]
        value=node();value['props'][0]['name']='FFFFFFFF';bad.append(value)
        value=node();value['props'][0]['kind']=6;bad.append(value)
        value=node();value['props'][0]['rawUint32']=[];bad.append(value)
        value=node(states=['00000002']);value['props'][1]['count']=2;bad.append(value)
        bad.extend([node(states=['00000002']*257),node(resource='00000000'),node(resource='not-a-key')])
        value=node();value['propertyParseError']='incomplete';bad.append(value)
        for value in bad:
            with self.subTest(value=value),self.assertRaises(ValueError):shader_fields(value)

    def test_invalid_roots_and_limit_rejected(self):
        nodes={'00000001':[node()]}
        for roots,limit in ((set(),4096),({'00000000'},4096),({'bad'},4096),({'00000001'},0),({'00000001'},4097)):
            with self.subTest(roots=roots,limit=limit),self.assertRaises(ValueError):reachable_shader_objects(nodes,roots,limit)


if __name__=='__main__':unittest.main(verbosity=2)

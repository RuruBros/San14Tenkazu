"""Known analytic results for the independent DDS sampler/material oracle; no GPU or game."""
import math
import struct
import unittest

from validate_terrain_shader import climate_influence, reference, sampling


class TerrainOracleTests(unittest.TestCase):
    @staticmethod
    def diffuse_fixture():
        words=[0]*37;words[3]=1;words[4]=2;words[7]=2
        # Opaque black/white base and an authored sRGB gray second mip.
        return struct.pack('<37I',*words)+bytes([0,0,0,255,255,255,255,255,128,128,128,255])

    def test_srgb_decode_occurs_before_bilinear_filter(self):
        value=sampling(self.diffuse_fixture(),.5,.5,0,'diff')
        self.assertEqual([.5,.5,.5],value)
        self.assertGreater(value[0]-.21586050011389926,.28)

    def test_wrap_coordinates_repeat_texels(self):
        fixture=self.diffuse_fixture()
        self.assertEqual([1,1,1],sampling(fixture,-.25,.5,0,'diff'))
        self.assertEqual([0,0,0],sampling(fixture,1.25,.5,0,'diff'))

    def test_fractional_mip_filters_linear_light_samples(self):
        result=sampling(self.diffuse_fixture(),.5,.5,.5,'diff')
        for value in result:self.assertAlmostEqual(value,(.5+.21586050011389926)/2,places=14)

    def test_convex_weights_and_snow_overlay_have_known_material_results(self):
        layers=[([1,0,0],[0,0,1],.1),([0,1,0],[0,0,1],.2),([0,0,1],[0,0,1],.3),
                ([.5,.5,0],[0,0,1],.4),([1,1,1],[0,0,1],.9)]
        inputs=[0,0,0,1,.25,.25,.25,.25,1,1,0,0,1,1,.5,.9,.2,.3,.4,.7,0,0,1,0]
        actual=reference(inputs,layers)
        self.assertEqual(actual[:3],[.375,.375,.25]);self.assertAlmostEqual(actual[3],.25)
        self.assertEqual(actual[4:8],[0,0,1,0]);self.assertEqual(actual[8:],[.25]*4)
        inputs[10]=1;actual=reference(inputs,layers)
        self.assertEqual(actual[:3],[1,1,1]);self.assertAlmostEqual(actual[3],.9)
        self.assertEqual(actual[4:8],[0,0,1,1])

    def test_cold_calendar_boundary_and_december_wrap_are_explicit(self):
        station={'temperatureC':[0]*12,'precipitationMm':[40]*12}
        ground,snow=climate_influence(station,2,30)
        self.assertEqual(ground[:3],[0,0,0]);self.assertAlmostEqual(ground[3],1/30);self.assertAlmostEqual(snow,1/30)
        self.assertEqual(climate_influence(station,12,30),([0,0,0,1],1))
        station['temperatureC']=[20]*12
        self.assertEqual(climate_influence(station,3,1),([1,0,0,0],0))


if __name__=='__main__':unittest.main()

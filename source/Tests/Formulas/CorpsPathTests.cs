using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class CorpsPathTests
{
    [Fact]
    public void BasePathsMatchNativeSearchAndQueueOrder()
    {
        var root = Vectors.Read("corps_path.json.gz");
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        Assert.Equal(4, root.GetProperty("contract").GetProperty("options").GetInt32());
        Assert.Equal(0, root.GetProperty("contract").GetProperty("workspace").GetInt32());
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            var f = c.GetProperty("fixture");
            Vectors.Poke(w, f.GetProperty("pokes"));
            var result = CorpsPath.Find(w, w.Table(Tables.Cities)[f.GetProperty("source").GetInt32()],
                w.Table(Tables.Cities)[f.GetProperty("target").GetInt32()]);
            Assert.Equal(c.GetProperty("result").GetInt32() != 0, result.Success);
            Assert.Equal(c.GetProperty("cost").GetInt32(), result.Cost);
            Assert.True(c.GetProperty("hexes").EnumerateArray().Select(p => p.GetInt32()).SequenceEqual(result.Hexes),
                f.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void TerrainSliderAndFireStepCostsMatchNativeLeaf()
    {
        var root = Vectors.Read("corps_path.json.gz");
        foreach (var c in root.GetProperty("steps").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            var pokes = c.GetProperty("pokes");
            Vectors.Poke(w, pokes);
            Assert.Equal(c.GetProperty("result").GetInt32(), CorpsPath.StepCost(w, w.Table(Tables.Tiles)[pokes[0][1].GetInt32()]));
        }
    }
}

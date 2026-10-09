using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ReturnPathTests
{
    [Fact]
    public void RoutesAndNativeNeighborBoundariesMatchCopiedEngine()
    {
        var root = Vectors.Read("return_path.json.gz");
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        foreach (var row in root.GetProperty("neighbors").EnumerateArray())
            Assert.Equal(row[2].GetInt32(), Map.Neighbor(row[0].GetInt32(), row[1].GetInt32()));
        int failed = 0;
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            var f = c.GetProperty("fixture");
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            Vectors.Poke(w, f.GetProperty("pokes"));
            var result = ReturnPath.Find(w, f.GetProperty("source").GetInt32(), f.GetProperty("target").GetInt32());
            Assert.True(c.GetProperty("hexes").EnumerateArray().Select(h => h.GetInt32()).SequenceEqual(result.Hexes),
                f.GetProperty("name").GetString());
            Assert.Equal(c.GetProperty("cost").GetInt32(), result.Cost);
            if (!result.Success) failed++;
        }
        Assert.Equal(1, failed);
    }
}

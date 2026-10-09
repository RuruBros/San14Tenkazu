using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ForceFallTests
{
    [Fact]
    public void ScenarioFlagsMatchTheCompleteNativeHelper()
    {
        JsonElement vectors = Vectors.Read("force_fall_flags.json.gz");
        Assert.True(vectors.GetProperty("no_stubs").GetBoolean());
        World w = Vectors.LoadWorld(vectors.GetProperty("world").GetString()!);
        foreach (JsonElement c in vectors.GetProperty("cases").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c.GetProperty("pokes"));
            byte[][] forces = w.Table(Tables.Forces).Select(f => (byte[])f.Bytes.Clone()).ToArray();
            try
            {
                Rec? Ref(string key) => c.GetProperty(key) is var r && r.ValueKind != JsonValueKind.Null
                    ? w.Table(Convert.ToInt32(r[0].GetString()!, 16))[r[1].GetInt32()] : null;
                ForceFall.UpdateScenarioFlags(w, Ref("defeated"), Ref("victor"));
                Assert.Equal(Convert.FromHexString(c.GetProperty("worldstate").GetString()!), w.WorldState.Bytes);
                Assert.True(c.GetProperty("forces_unchanged").GetBoolean());
                Assert.Equal(forces.Select(Convert.ToHexString), w.Table(Tables.Forces).Select(f => Convert.ToHexString(f.Bytes)));
            }
            finally { undo(); }
        }
    }
}

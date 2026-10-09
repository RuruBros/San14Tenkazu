using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class RulerPredicateTests
{
    [Fact]
    public void AllEightSuccessionPredicatesMatchCompleteNativeCalls()
    {
        JsonElement root = Vectors.Read("ruler_predicates.json.gz");
        Assert.True(root.GetProperty("no_stubs").GetBoolean());
        Assert.False(root.GetProperty("succession_caller").GetBoolean());
        Assert.Equal(RulerSuccession.SeniorMerit, root.GetProperty("senior_merit").GetInt32());
        Assert.Equal(RulerSuccession.SeniorOfficeGrade, root.GetProperty("senior_office_grade").GetInt32());
        Assert.Equal(112, root.GetProperty("cases").GetArrayLength());
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        int[] tables = Vectors.Read(root.GetProperty("world").GetString()!).GetProperty("tables")
            .EnumerateObject().Select(t => Convert.ToInt32(t.Name, 16)).ToArray();
        var original = tables.ToDictionary(t => t, t => w.Table(t).Select(r => Convert.ToHexString(r.Bytes)).ToArray());
        byte[] state = w.WorldState.Bytes.ToArray();
        var predicates = new HashSet<int>();
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture");
            Action undo = Vectors.Poke(w, f.GetProperty("pokes"));
            try
            {
                Rec? Ref(string key) => f.GetProperty(key) is var r && r.ValueKind != JsonValueKind.Null
                    ? w.Table(Tables.Officers)[r.GetInt32()] : null;
                int predicate = f.GetProperty("predicate").GetInt32();
                bool actual = RulerSuccession.Matches(w, Ref("candidate"), Ref("dying"), predicate);
                Assert.True(actual == c.GetProperty("result").GetBoolean(), f.GetProperty("name").GetString());
                predicates.Add(predicate);
            }
            finally { undo(); }
        }
        Assert.Equal(Enumerable.Range(0, 8), predicates.Order());
        foreach (int table in tables)
            Assert.Equal(original[table], w.Table(table).Select(r => Convert.ToHexString(r.Bytes)));
        Assert.Equal(state, w.WorldState.Bytes);
    }
}

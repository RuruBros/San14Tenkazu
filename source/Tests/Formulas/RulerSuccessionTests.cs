using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class RulerSuccessionTests
{
    [Fact]
    public void ParentAndGrandparentHelpersMatchNativeIdLookup()
    {
        JsonElement root = Vectors.Read("ruler_candidates.json.gz");
        Assert.True(root.GetProperty("no_stubs").GetBoolean());
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        int count = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray()
            .Where(c => c.GetProperty("fixture").GetProperty("kind").GetString() == "kinship"))
        {
            JsonElement f = c.GetProperty("fixture");
            Action undo = Vectors.Poke(w, f.GetProperty("pokes"));
            try
            {
                Rec? Ref(string key) => f.GetProperty(key) is var r && r.ValueKind != JsonValueKind.Null
                    ? w.Table(Tables.Officers)[r.GetInt32()] : null;
                bool actual = f.GetProperty("entry").GetString() == "0x2c8a70"
                    ? RulerSuccession.HasParent(w, Ref("person"), Ref("relative"))
                    : RulerSuccession.HasGrandparent(w, Ref("person"), Ref("relative"));
                Assert.True(actual == c.GetProperty("result").GetBoolean(), f.GetProperty("name").GetString());
                count++;
            }
            finally { undo(); }
        }
        Assert.Equal(25, count);
    }

    [Fact]
    public void CandidateInsertionOrderMatchesTheCompleteNativeSorter()
    {
        JsonElement root = Vectors.Read("ruler_candidates.json.gz");
        Assert.True(root.GetProperty("no_stubs").GetBoolean());
        Assert.True(root.GetProperty("native_sort").GetBoolean());
        Assert.False(root.GetProperty("succession_caller").GetBoolean());
        Assert.Equal(root.GetProperty("priority_ids").EnumerateArray().Select(x => x.GetInt32()), RulerSuccession.PriorityIds.ToArray());
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        int count = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray()
            .Where(c => c.GetProperty("fixture").GetProperty("kind").GetString() == "sort"))
        {
            JsonElement f = c.GetProperty("fixture");
            Action undo = Vectors.Poke(w, f.GetProperty("pokes"));
            try
            {
                Rec dying = w.Table(Tables.Officers)[f.GetProperty("dying").GetInt32()];
                var candidates = f.GetProperty("members").EnumerateArray().Select(i => w.Table(Tables.Officers)[i.GetInt32()]).ToList();
                RulerSuccession.SortCandidates(w, dying, candidates);
                Assert.True(candidates.Select(p => p.Index).SequenceEqual(c.GetProperty("result").EnumerateArray().Select(i => i.GetInt32())),
                    f.GetProperty("name").GetString());
                count++;
            }
            finally { undo(); }
        }
        Assert.Equal(23, count);
    }
}

using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class OfficerRelationsTests
{
    [Fact]
    public void RelationshipRemovalAndSwornSortMatchNativeBodies()
    {
        var root = Vectors.Read("officer_relations.json.gz");
        var snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        Assert.True(root.GetProperty("native_pool").GetBoolean());
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            var f = c.GetProperty("fixture");
            Vectors.Poke(w, f.GetProperty("pokes"));
            var expected = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(t => t.Name,
                t => w.Table(Convert.ToInt32(t.Name[2..], 16)).Select(r => r.Bytes.ToArray()).ToArray());
            int result = 0;
            if (f.GetProperty("kind").GetString() == "sort")
            {
                var members = f.GetProperty("members").EnumerateArray().Select(i => w.Table(Tables.Officers)[i.GetInt32()]).ToList();
                OfficerRelations.SortSworn(members);
                Assert.Equal(c.GetProperty("sorted").EnumerateArray().Select(i => i.GetInt32()), members.Select(p => p.Index));
            }
            else
            {
                Rec p = w.Table(Tables.Officers)[f.GetProperty("person").GetInt32()];
                Rec? target = f.GetProperty("target").ValueKind == System.Text.Json.JsonValueKind.Null ? null :
                    w.Table(Tables.Officers)[f.GetProperty("target").GetInt32()];
                switch (f.GetProperty("entry").GetString())
                {
                    case "0x232ee0": result = OfficerRelations.Likes(w, p, target) ? 1 : 0; break;
                    case "0x232a00": result = OfficerRelations.Dislikes(w, p, target) ? 1 : 0; break;
                    case "0x2283f0": OfficerRelations.RemoveLiked(w, p, target); break;
                    case "0x2280d0": OfficerRelations.RemoveDisliked(w, p, target); break;
                    default: throw new InvalidDataException("Unknown relationship helper.");
                }
            }
            Assert.Equal(c.GetProperty("result").GetInt32(), result);
            foreach (var r in c.GetProperty("after").GetProperty("records").EnumerateArray())
                expected[r[0].GetString()!][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
            foreach (var t in expected)
                for (int i = 0; i < t.Value.Length; i++)
                    Assert.True(t.Value[i].SequenceEqual(w.Table(Convert.ToInt32(t.Key[2..], 16))[i].Bytes),
                        $"{f.GetProperty("name").GetString()}: {t.Key}[{i}] differs");
            Assert.All(c.GetProperty("after").EnumerateObject(), p => Assert.Equal("records", p.Name));
        }
    }
}

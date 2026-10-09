using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class RulerSelectionTests
{
    [Fact]
    public void SelectionPrefixAndRequestsMatchNativeCallerBoundaries()
    {
        JsonElement root = Vectors.Read("ruler_selection.json.gz");
        Assert.True(root.GetProperty("prefix_only").GetBoolean());
        Assert.False(root.GetProperty("complete_succession").GetBoolean());
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.Equal(61, root.GetProperty("cases").GetArrayLength());
        Assert.Equal(Attack.AgeEighty, root.GetProperty("special_age_ids").EnumerateArray().Select(v => v.GetInt32()));
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        int[] tables = Vectors.Read(root.GetProperty("world").GetString()!).GetProperty("tables")
            .EnumerateObject().Select(t => Convert.ToInt32(t.Name, 16)).Order().ToArray();
        byte[] Digest()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (int table in tables)
                foreach (Rec r in w.Table(table)) hash.AppendData(r.Bytes);
            hash.AppendData(w.WorldState.Bytes);
            hash.AppendData(w.Settings.Bytes);
            return hash.GetHashAndReset();
        }
        byte[] original = Digest();
        List<Rec?[]> list = w.List(Lists.OfficerList);
        Rec?[][] originalList = list.ToArray();
        var kinds = new HashSet<RulerSuccession.SelectionKind>();
        var predicates = new HashSet<int>();
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement fixture = c.GetProperty("fixture"), expected = c.GetProperty("result");
            Action undo = Vectors.Poke(w, fixture.GetProperty("pokes"));
            list.Clear();
            list.AddRange(fixture.GetProperty("members").EnumerateArray()
                .Select(i => new Rec?[] { i.ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Officers)[i.GetInt32()] }));
            Rec?[][] beforeList = list.Select(n => n.ToArray()).ToArray();
            byte[] before = Digest();
            try
            {
                RulerSuccession.Selection actual = RulerSuccession.SelectBeforeSuccession(w,
                    w.Table(Tables.Officers)[fixture.GetProperty("dying").GetInt32()]);
                int? selected = expected.GetProperty("selected") is var value && value.ValueKind != JsonValueKind.Null
                    ? value.GetInt32() : null;
                Assert.True(actual.Officer?.Index == selected && actual.Kind.ToString() == expected.GetProperty("kind").GetString() &&
                    actual.Predicate == expected.GetProperty("predicate").GetInt32(), fixture.GetProperty("name").GetString());
                Assert.Equal(before, Digest());
                Assert.Equal(beforeList, list);
                kinds.Add(actual.Kind);
                if (actual.Kind == RulerSuccession.SelectionKind.Predicate) predicates.Add(actual.Predicate);
            }
            finally
            {
                undo();
                list.Clear();
                list.AddRange(originalList);
            }
        }
        Assert.Equal(Enum.GetValues<RulerSuccession.SelectionKind>().Order(), kinds.Order());
        // Related (predicate 2) already includes same-force sworn/spouse matches (predicates 3/4).
        Assert.Equal(new[] { 0, 1, 2, 5, 6, 7 }, predicates.Order());
        Assert.Equal(original, Digest());
        Assert.Equal(originalList, list);
    }
}

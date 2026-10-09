using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class OfficerDepartureTests
{
    [Theory]
    [InlineData("predicate", 43)]
    [InlineData("basis", 10)]
    public void CompleteNativeDepartureHelpersMatchWithoutChangingRecordsOrSharedRng(string group, int count)
    {
        JsonElement root = Vectors.Read("officer_departure.json.gz");
        Assert.True(root.GetProperty("complete").GetBoolean());
        foreach (string key in new[] { "gameplay_stubs", "allocator_stubs", "presentation_stubs" }) Assert.False(root.GetProperty(key).GetBoolean());
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        Assert.Equal(OfficerDeparture.LoyaltyLimit, snapshot.GetProperty("flags").GetProperty("departure_loyalty_limit").GetInt32());
        Assert.Equal(OfficerDeparture.CompatibilityDivisor, snapshot.GetProperty("flags").GetProperty("departure_compatibility_divisor").GetInt32());
        Assert.Equal(OfficerDeparture.ChanceCap, snapshot.GetProperty("flags").GetProperty("departure_chance_cap").GetInt32());
        int cases = 0;
        var outcomes = new HashSet<int>();
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture");
            if (f.GetProperty("kind").GetString() != group) continue;
            Assert.True(c.GetProperty("function_complete").GetBoolean());
            Assert.True(c.GetProperty("state_unchanged").GetBoolean());
            World w = Vectors.LoadWorld(world);
            Vectors.Poke(w, f.GetProperty("pokes"));
            var records = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
            Assert.Equal(32, records.Count);
            var vectors = records.Keys.SelectMany(w.Table).ToDictionary(r => r,
                r => new[] { r.Vector40, r.Vector50, r.Vector70, r.Vector98, r.VectorA0, r.VectorA8, r.VectorC8, r.Vector1D0 }.Select(v => v?.ToArray()).ToArray());
            var lists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16), p => w.List(Convert.ToInt32(p.Name, 16)).Select(n => n.ToArray()).ToArray());
            byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
            Rec person = w.Table(Tables.Officers)[f.GetProperty("person").GetInt32()];
            int result = group == "basis" ? OfficerDeparture.Chance(w, person) : OfficerDeparture.Test(w, person) ? 1 : 0;
            Assert.True(c.GetProperty("result").GetInt32() == result, $"{f.GetProperty("name").GetString()}: expected {c.GetProperty("result").GetInt32()}, actual {result}");
            foreach (var (table, rows) in records)
                for (int i = 0; i < rows.Length; i++) Assert.Equal(rows[i], w.Table(table)[i].Bytes);
            foreach (var (record, data) in vectors)
                Assert.Equal(data, new[] { record.Vector40, record.Vector50, record.Vector70, record.Vector98, record.VectorA0, record.VectorA8, record.VectorC8, record.Vector1D0 });
            foreach (var (head, list) in lists) Assert.Equal(list, w.List(head));
            Assert.Equal(state, w.WorldState.Bytes); Assert.Equal(settings, w.Settings.Bytes);
            foreach (JsonProperty p in snapshot.GetProperty("flags").EnumerateObject()) Assert.Equal(p.Value.GetInt32(), w.Value(p.Name));
            outcomes.Add(result); cases++;
        }
        Assert.Equal(count, cases);
        if (group == "predicate") Assert.Equal(new[] { 0, 1 }, outcomes.Order());
    }
}

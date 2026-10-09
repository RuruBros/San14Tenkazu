using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class RulerRebellionTests
{
    [Theory]
    [InlineData("compare", 17)]
    [InlineData("check", 40)]
    public void CompleteNativeNoRebellionCallsMatchCandidatesChanceAndDrawWithoutChangingState(string group, int expectedCount)
    {
        JsonElement root = Vectors.Read("ruler_rebellion.json.gz");
        Assert.True(root.GetProperty("complete").GetBoolean());
        foreach (string key in new[] { "gameplay_stubs", "allocator_stubs", "presentation_stubs" }) Assert.False(root.GetProperty(key).GetBoolean());
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        var rules = new Dictionary<string, int>
        {
            ["rebellion_loyalty_gate"] = RulerRebellion.LoyaltyGate,
            ["rebellion_service_divisor"] = RulerRebellion.ServiceDivisor,
            ["rebellion_merit_gate"] = RulerRebellion.MeritGate,
            ["rebellion_compatibility_gate"] = RulerRebellion.CompatibilityGate,
            ["rebellion_chance_base"] = RulerRebellion.ChanceBase,
            ["rebellion_dying_bonus"] = RulerRebellion.DyingBonus,
            ["rebellion_merit_divisor"] = RulerRebellion.MeritDivisor,
            ["rebellion_chance_service_divisor"] = RulerRebellion.ChanceServiceDivisor,
            ["rebellion_imperial_penalty"] = RulerRebellion.ImperialPenalty,
        };
        foreach (var (name, value) in rules) Assert.Equal(value, snapshot.GetProperty("flags").GetProperty(name).GetInt32());
        int count = 0, selected = 0;
        var chances = new HashSet<int>();
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture");
            if (f.GetProperty("kind").GetString() != group) continue;
            Assert.True(c.GetProperty("function_complete").GetBoolean()); Assert.True(c.GetProperty("state_unchanged").GetBoolean());
            World w = Vectors.LoadWorld(world); Vectors.Poke(w, f.GetProperty("pokes"));
            var records = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
            Assert.Equal(32, records.Count);
            var vectors = records.Keys.SelectMany(w.Table).ToDictionary(r => r,
                r => new[] { r.Vector40, r.Vector50, r.Vector70, r.Vector98, r.VectorA0, r.VectorA8, r.VectorC8, r.Vector1D0 }.Select(v => v?.ToArray()).ToArray());
            var lists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.List(Convert.ToInt32(p.Name, 16)).Select(n => n.ToArray()).ToArray());
            byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
            if (group == "compare")
            {
                int result = RulerRebellion.Before(w, w.Table(Tables.Officers)[f.GetProperty("ruler").GetInt32()],
                    w.Table(Tables.Officers)[f.GetProperty("person").GetInt32()], w.Table(Tables.Officers)[f.GetProperty("other").GetInt32()]) ? 1 : 0;
                Assert.True(c.GetProperty("result").GetInt32() == result, f.GetProperty("name").GetString());
            }
            else
            {
                Rec? dying = f.GetProperty("dying").ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Officers)[f.GetProperty("dying").GetInt32()];
                var plan = RulerRebellion.Select(w, w.Table(Tables.Forces)[f.GetProperty("force").GetInt32()], dying);
                JsonElement selection = c.GetProperty("selection");
                if (selection.TryGetProperty("candidates", out JsonElement candidates))
                {
                    Assert.Equal(candidates.EnumerateArray().Select(v => v.GetInt32()), plan.Candidates.Select(p => p.Index));
                    Assert.True(selection.GetProperty("chance").GetInt32() == plan.Chance, $"{f.GetProperty("name").GetString()}: chance {plan.Chance}");
                    Assert.Equal(selection.GetProperty("draw").GetInt32(), plan.Draw);
                    Assert.False(plan.Execute); chances.Add(plan.Chance); selected++;
                }
                else Assert.Empty(plan.Candidates);
                Assert.Equal(0, c.GetProperty("result").GetInt32());
            }
            foreach (var (table, rows) in records)
                for (int i = 0; i < rows.Length; i++) Assert.Equal(rows[i], w.Table(table)[i].Bytes);
            foreach (var (record, data) in vectors)
                Assert.Equal(data, new[] { record.Vector40, record.Vector50, record.Vector70, record.Vector98, record.VectorA0, record.VectorA8, record.VectorC8, record.Vector1D0 });
            foreach (var (head, nodes) in lists) Assert.Equal(nodes, w.List(head));
            Assert.Equal(state, w.WorldState.Bytes); Assert.Equal(settings, w.Settings.Bytes);
            foreach (JsonProperty p in snapshot.GetProperty("flags").EnumerateObject()) Assert.Equal(p.Value.GetInt32(), w.Value(p.Name));
            count++;
        }
        Assert.Equal(expectedCount, count);
        if (group == "check") { Assert.True(selected > 0); Assert.Contains(0, chances); Assert.Contains(70, chances); }
    }

    [Fact]
    public void ActualSampledCandidateSeedMatchesWithoutAdvancingSharedRng()
    {
        JsonElement root = Vectors.Read("ruler_rebellion.json.gz");
        string world = root.GetProperty("world").GetString()!;
        Assert.NotEmpty(root.GetProperty("seeds").EnumerateArray());
        foreach (JsonElement c in root.GetProperty("seeds").EnumerateArray())
        {
            Assert.True(c.GetProperty("function_complete").GetBoolean()); Assert.True(c.GetProperty("state_unchanged").GetBoolean());
            World w = Vectors.LoadWorld(world); JsonElement f = c.GetProperty("fixture"); Vectors.Poke(w, f.GetProperty("pokes"));
            byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
            int draw = Wounded.Seeded(w, 100, 1, w.WorldState.I16(0x34) + w.WorldState.I8(0x36), w.WorldState.I8(0x37), f.GetProperty("person").GetInt32(), 0);
            Assert.Equal(c.GetProperty("result").GetInt32(), draw);
            Assert.Equal(state, w.WorldState.Bytes); Assert.Equal(settings, w.Settings.Bytes);
        }
    }
}

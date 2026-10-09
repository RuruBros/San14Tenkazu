using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TacticBoostTests
{
    private static Tactic.Side Side(World w, JsonElement source) => new(
        source[0].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[source[0].GetInt32()],
        source[1].ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(source[1][0].GetString()!, 16))[source[1][1].GetInt32()]);

    [Fact]
    public void BoostMergeMatchesTheGamesEntireRecord()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("tactic_boost.json.gz").GetProperty("merge").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[5]);
            Rec unit = w.Table(Tables.Units)[c[0].GetInt32()];
            byte[] before = unit.Bytes.ToArray();
            try
            {
                UnitBoost.Set(unit, c[1].GetInt32(), c[2].GetInt32(), c[3].GetInt32());
                if (!unit.Bytes.SequenceEqual(Convert.FromHexString(c[4].GetString()!)) && wrong.Count < 20)
                    wrong.Add($"case {n}, unit {c[0]}, boost {c[1]}, level {c[2]}, duration {c[3]}; pokes {c[5]}");
                n++;
            }
            finally { before.CopyTo(unit.Bytes, 0); undo(); }
        }
        Assert.True(n >= 5_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void AttackFactorsAndAbilitiesMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("tactic_boost.json.gz").GetProperty("factors").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[4]);
            try
            {
                Rec tactic = w.Table(Tables.Tactics)[c[0].GetInt32()];
                Tactic.Side source = Side(w, c[1]), target = Side(w, c[2]);
                float got = TacticEffects.AttackFactor(w, tactic, source, target);
                if ((!BitConverter.GetBytes(got).SequenceEqual(Convert.FromHexString(c[3][0].GetString()!))
                    || TacticEffects.SourceAbility(w, tactic, source) != c[3][1].GetInt32()
                    || TacticEffects.TargetAbility(w, tactic, target) != c[3][2].GetInt32()) && wrong.Count < 20)
                    wrong.Add($"case {n}, tactic {c[0]}, source {c[1]}, target {c[2]}: factor {got:R}, game {c[3]}; pokes {c[4]}");
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 1_500, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void BoostEffectsMatchTheGamesRecordsAndResults()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        var changedKinds = new HashSet<int>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("tactic_boost.json.gz").GetProperty("apply").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[8]);
            var records = c[6].EnumerateArray().Select(e => (Record: w.Table(Convert.ToInt32(e[0].GetString()!, 16))[e[1].GetInt32()], Expected: Convert.FromHexString(e[2].GetString()!))).ToArray();
            var before = records.Select(r => r.Record.Bytes.ToArray()).ToArray();
            try
            {
                int kind = c[4].GetInt32();
                var targets = c[5].EnumerateArray().Select(h => w.Table(Tables.Tiles)[h.GetInt32()]).ToArray();
                var got = TacticEffects.ApplyBoost(w, w.Table(Tables.Tactics)[c[0].GetInt32()], Side(w, c[1]), c[2].GetInt32(), c[3].GetInt32(), kind, targets);
                var expected = c[7].EnumerateArray().Select(e => new TacticEffects.Applied(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32()));
                if (!got.SequenceEqual(expected) && wrong.Count < 20) wrong.Add($"case {n}, kind {kind}, source {c[1]}: results [{string.Join(',', got)}], game {c[7]}; pokes {c[8]}");
                for (int i = 0; i < records.Length; i++)
                {
                    var r = records[i];
                    if (!before[i].SequenceEqual(r.Expected)) changedKinds.Add(kind);
                    if (!r.Record.Bytes.SequenceEqual(r.Expected) && wrong.Count < 20)
                    {
                        int off = Enumerable.Range(0, r.Expected.Length).First(at => r.Record.Bytes[at] != r.Expected[at]);
                        wrong.Add($"case {n}, kind {kind}, source {c[1]}, target {r.Record.Table:X}/{r.Record.Index}, offset {off:X}: {r.Record.Bytes[off]:X2}, game {r.Expected[off]:X2}; pokes {c[8]}");
                    }
                }
                n++;
            }
            finally
            {
                for (int i = 0; i < records.Length; i++) before[i].CopyTo(records[i].Record.Bytes, 0);
                undo();
            }
        }
        Assert.True(n >= 4_800, $"only {n} cases");
        Assert.True(Enumerable.Range(5, 10).Concat(new[] {21, 22}).All(changedKinds.Contains), $"effect kinds with actual mutations: {string.Join(',', changedKinds)}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }
}

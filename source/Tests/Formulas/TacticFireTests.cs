using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TacticFireTests
{
    private static Tactic.Side Side(World w, JsonElement source) => new(
        source[0].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[source[0].GetInt32()],
        source[1].ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(source[1][0].GetString()!, 16))[source[1][1].GetInt32()]);

    [Fact]
    public void ImmunityTerrainAndTimerMatchTheGamesEntireTile()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0, changed = 0;
        foreach (JsonElement c in Vectors.Read("tactic_fire.json.gz").GetProperty("ignite").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[3]);
            Rec tile = w.Table(Tables.Tiles)[c[0].GetInt32()];
            byte[] before = tile.Bytes.ToArray();
            try
            {
                TacticEffects.Ignite(w, tile, c[1].GetInt32());
                byte[] expected = Convert.FromHexString(c[2].GetString()!);
                if (!before.SequenceEqual(expected)) changed++;
                if (!tile.Bytes.SequenceEqual(expected) && wrong.Count < 20)
                    wrong.Add($"case {n}, hex {c[0]}, duration {c[1]}: {Convert.ToHexString(tile.Bytes)}, game {c[2]}; pokes {c[3]}");
                n++;
            }
            finally { before.CopyTo(tile.Bytes, 0); undo(); }
        }
        Assert.True(n >= 3_000, $"only {n} cases");
        Assert.True(changed > 0, "No native fire timer changed.");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void SourcePolicyMatchesTheGamesFloatBits()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("tactic_fire.json.gz").GetProperty("policy").EnumerateArray())
        {
            string got = Convert.ToHexString(BitConverter.GetBytes(TacticEffects.SourcePolicy(w, Side(w, c[0]), c[1].GetInt32()))).ToLowerInvariant();
            if (got != c[2].GetString() && wrong.Count < 20) wrong.Add($"case {n}, source {c[0]}, policy {c[1]}: {got}, game {c[2]}");
            n++;
        }
        Assert.True(n >= 1_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void EffectsMatchTheGamesRecordsAndCenterResults()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0, changed = 0, emitted = 0;
        foreach (JsonElement c in Vectors.Read("tactic_fire.json.gz").GetProperty("apply").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[8]);
            var records = c[6].EnumerateArray().Select(e => (Record: w.Table(Convert.ToInt32(e[0].GetString()!, 16))[e[1].GetInt32()], Expected: Convert.FromHexString(e[2].GetString()!))).ToArray();
            var before = records.Select(r => r.Record.Bytes.ToArray()).ToArray();
            try
            {
                var targets = c[5].EnumerateArray().Select(h => w.Table(Tables.Tiles)[h.GetInt32()]).ToArray();
                var got = TacticEffects.ApplyFire(w, w.Table(Tables.Tactics)[c[0].GetInt32()], Side(w, c[1]), c[2].GetInt32(), c[3].GetInt32(), c[4].GetInt32(), targets);
                var expected = c[7].EnumerateArray().Select(e => new TacticEffects.Applied(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32())).ToArray();
                emitted += expected.Length;
                if (!got.SequenceEqual(expected) && wrong.Count < 20) wrong.Add($"case {n}, source {c[1]}, center {c[4]}: results [{string.Join(',', got)}], game {c[7]}; pokes {c[8]}");
                for (int i = 0; i < records.Length; i++)
                {
                    var r = records[i];
                    if (!before[i].SequenceEqual(r.Expected)) changed++;
                    if (!r.Record.Bytes.SequenceEqual(r.Expected) && wrong.Count < 20)
                    {
                        int off = Enumerable.Range(0, r.Expected.Length).First(at => r.Record.Bytes[at] != r.Expected[at]);
                        wrong.Add($"case {n}, source {c[1]}, target {r.Record.Table:X}/{r.Record.Index}, offset {off:X}: {r.Record.Bytes[off]:X2}, game {r.Expected[off]:X2}; pokes {c[8]}");
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
        Assert.True(n >= 3_000, $"only {n} cases");
        Assert.True(changed > 0, "No native fire effect changed a record.");
        Assert.True(emitted > 0, "No native center result was emitted.");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }
}

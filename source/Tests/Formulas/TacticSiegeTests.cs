using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TacticSiegeTests
{
    [Fact]
    public void DurabilityCapacityAndDamageLevelsMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("tactic_siege.json.gz").GetProperty("capacity").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[4]);
            try
            {
                Rec b = w.Table(Convert.ToInt32(c[0].GetString()!, 16))[c[1].GetInt32()];
                int maximum = Engagement.MaxDurability(w, b), level = Engagement.DamageLevel(w, b);
                if ((maximum != c[2].GetInt32() || level != c[3].GetInt32()) && wrong.Count < 20)
                    wrong.Add($"case {n}, base {c[0]}/{c[1]}: max {maximum}, level {level}, game {c[2]}/{c[3]}; pokes {c[4]}");
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 2_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void SiegeRecordsExperienceResultsAndPendingBasesMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0, changed = 0, routes = 0, experience = 0;
        foreach (JsonElement c in Vectors.Read("tactic_siege.json.gz").GetProperty("apply").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[8]);
            var records = c[5].EnumerateArray().Select(e => (Record: w.Table(Convert.ToInt32(e[0].GetString()!, 16))[e[1].GetInt32()], Expected: Convert.FromHexString(e[2].GetString()!))).ToArray();
            var before = records.Select(r => r.Record.Bytes.ToArray()).ToArray();
            try
            {
                var source = new Tactic.Side(c[1][0].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[c[1][0].GetInt32()],
                    c[1][1].ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(c[1][1][0].GetString()!, 16))[c[1][1][1].GetInt32()]);
                var targets = c[4].EnumerateArray().Select(h => w.Table(Tables.Tiles)[h.GetInt32()]).ToArray();
                var got = TacticEffects.ApplySiege(w, w.Table(Tables.Tactics)[c[0].GetInt32()], source, c[2].GetInt32(), c[3].GetInt32(), targets);
                var expected = c[6].EnumerateArray().Select(e => new TacticEffects.Applied(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32())).ToArray();
                var queued = c[7].EnumerateArray().Select(e => w.Table(Convert.ToInt32(e[1][0].GetString()!, 16))[e[1][1].GetInt32()]).ToArray();
                routes += queued.Length;
                if ((!got.Results.SequenceEqual(expected) || !got.PendingBases.SequenceEqual(queued) || got.DestroyedBuildings.Count != 0) && wrong.Count < 20)
                    wrong.Add($"case {n}: results [{string.Join(',', got.Results)}], bases [{string.Join(',', got.PendingBases.Select(b => b.U16(0x4E)))}], game {c[6]}/{c[7]}; pokes {c[8]}");
                foreach (JsonElement entry in c[7].EnumerateArray())
                    if (entry[0].ValueKind != JsonValueKind.Null || entry[2][0].GetString() != "0x7e838" || entry[2][1].GetInt32() != source.Unit!.Index)
                        wrong.Add($"case {n}: native fall queue contains unexpected source fields {entry}");
                for (int i = 0; i < records.Length; i++)
                {
                    var r = records[i];
                    if (!before[i].SequenceEqual(r.Expected)) { changed++; if (r.Record.Table == Tables.Officers) experience++; }
                    if (!r.Record.Bytes.SequenceEqual(r.Expected) && wrong.Count < 20)
                    {
                        int off = Enumerable.Range(0, r.Expected.Length).First(at => r.Record.Bytes[at] != r.Expected[at]);
                        wrong.Add($"case {n}, record {r.Record.Table:X}/{r.Record.Index}, offset {off:X}: {r.Record.Bytes[off]:X2}, game {r.Expected[off]:X2}; pokes {c[8]}");
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
        Assert.True(n >= 4_000, $"only {n} cases");
        Assert.True(changed > 0 && experience > 0 && routes > 0, $"record changes {changed}, experience {experience}, routes {routes}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }
}

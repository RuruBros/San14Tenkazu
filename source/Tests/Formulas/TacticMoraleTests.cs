using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TacticMoraleTests
{
    [Fact]
    public void MoraleResultsAndRouteTargetsMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0, changed = 0, routes = 0;
        foreach (JsonElement c in Vectors.Read("tactic_morale.json.gz").GetProperty("apply").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[8]);
            var records = c[5].EnumerateArray().Select(e => (Record: w.Table(Convert.ToInt32(e[0].GetString()!, 16))[e[1].GetInt32()], Expected: Convert.FromHexString(e[2].GetString()!))).ToArray();
            var before = records.Select(r => r.Record.Bytes.ToArray()).ToArray();
            try
            {
                var source = new Tactic.Side(c[1][0].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[c[1][0].GetInt32()],
                    c[1][1].ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(c[1][1][0].GetString()!, 16))[c[1][1][1].GetInt32()]);
                var targets = c[4].EnumerateArray().Select(h => w.Table(Tables.Tiles)[h.GetInt32()]).ToArray();
                var got = TacticEffects.ApplyMorale(w, w.Table(Tables.Tactics)[c[0].GetInt32()], source, c[2].GetInt32(), c[3].GetInt32(), targets);
                var expected = c[6].EnumerateArray().Select(e => new TacticEffects.Applied(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32())).ToArray();
                var routeTargets = c[7][0].EnumerateArray().Select(e => e[0][1].GetInt32()).ToArray();
                routes += routeTargets.Length;
                if ((!got.Results.SequenceEqual(expected) || !got.Routed.Select(u => u.Index).SequenceEqual(routeTargets)
                    || (got.Routed.Count != 0) != (c[7][1].GetInt32() != 0)) && wrong.Count < 20)
                    wrong.Add($"case {n}, source {c[1]}: results [{string.Join(',', got.Results)}], routes [{string.Join(',', got.Routed.Select(u => u.Index))}], game {c[6]}/{c[7]}; pokes {c[8]}");
                for (int i = 0; i < records.Length; i++)
                {
                    var r = records[i];
                    if (!before[i].SequenceEqual(r.Expected)) changed++;
                    if (!r.Record.Bytes.SequenceEqual(r.Expected) && wrong.Count < 20)
                    {
                        int off = Enumerable.Range(0, r.Expected.Length).First(at => r.Record.Bytes[at] != r.Expected[at]);
                        wrong.Add($"case {n}, target {r.Record.Table:X}/{r.Record.Index}, offset {off:X}: {r.Record.Bytes[off]:X2}, game {r.Expected[off]:X2}; pokes {c[8]}");
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
        Assert.True(changed > 0, "No native morale record changed.");
        Assert.True(routes > 0, "No native route request was queued.");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }
}

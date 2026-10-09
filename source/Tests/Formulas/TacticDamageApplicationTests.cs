using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TacticDamageApplicationTests
{
    [Fact]
    public void ImmediateDamageRecordsMapsResultsAndQueuePayloadsMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0, changed = 0, queued = 0, destroyed = 0;
        Rec? Reference(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null
            : w.Table(Convert.ToInt32(e[0].GetString()!, 16))[e[1].GetInt32()];
        foreach (JsonElement c in Vectors.Read("tactic_apply_damage.json.gz").GetProperty("apply").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[11]);
            var records = c[7].EnumerateArray().Select(e => (Record: w.Table(Convert.ToInt32(e[0].GetString()!, 16))[e[1].GetInt32()], Expected: Convert.FromHexString(e[2].GetString()!))).ToArray();
            var before = records.Select(r => r.Record.Bytes.ToArray()).ToArray();
            // The mutating leaf can transfer transport resources to a source city outside its target list.
            var cities = w.Table(Tables.Cities);
            var cityBefore = cities.Select(r => r.Bytes.ToArray()).ToArray();
            try
            {
                var source = new Tactic.Side(c[1][0].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[c[1][0].GetInt32()], Reference(c[1][1]));
                var targets = c[6].EnumerateArray().Select(h => w.Table(Tables.Tiles)[h.GetInt32()]).ToArray();
                var got = TacticEffects.ApplyDamage(w, w.Table(Tables.Tactics)[c[0].GetInt32()], source, c[2].GetInt32(), c[3].GetInt32(), targets, c[4].GetInt32(), c[5].GetInt32() != 0);
                var expected = c[8].EnumerateArray().Select(e => new TacticEffects.Applied(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32())).ToArray();
                var requests = c[10][0].EnumerateArray().Select(e => new TacticEffects.PendingDestruction(Reference(e[0]), Reference(e[1]), Reference(e[2]), Reference(e[3]), e[5].GetInt32(), e[6].GetInt32())).ToArray();
                queued += requests.Length;
                destroyed += c[10][1].GetInt32() != 0 ? 1 : 0;
                bool mapsMatch = true;
                for (int k = 0; k < 2; k++)
                {
                    var map = k == 0 ? got.UnitDamage : got.BaseDamage;
                    var native = c[9][k].EnumerateArray().Select(e => (Record: w.Table(Convert.ToInt32(e[0].GetString()!, 16))[e[1].GetInt32()], Amount: e[2].GetInt32())).ToArray();
                    mapsMatch &= map.Count == native.Length && native.All(e => map.TryGetValue(e.Record, out int amount) && amount == e.Amount);
                }
                if ((!got.Results.SequenceEqual(expected) || !got.Pending.SequenceEqual(requests) || !mapsMatch || got.Destroyed != (c[10][1].GetInt32() != 0)) && wrong.Count < 20)
                    wrong.Add($"case {n}: results [{string.Join(',', got.Results)}], requests [{string.Join(',', got.Pending)}], flag {got.Destroyed}, maps {mapsMatch}; native {c[8]}/{c[9]}/{c[10]}; pokes {c[11]}");
                foreach (JsonElement entry in c[10][0].EnumerateArray())
                    if (entry[4].GetInt32() != 0 && wrong.Count < 20) wrong.Add($"case {n}: unexpected native queue field 32 {entry}");
                for (int i = 0; i < records.Length; i++)
                {
                    var r = records[i];
                    if (!before[i].SequenceEqual(r.Expected)) changed++;
                    if (!r.Record.Bytes.SequenceEqual(r.Expected) && wrong.Count < 20)
                    {
                        int off = Enumerable.Range(0, r.Expected.Length).First(at => r.Record.Bytes[at] != r.Expected[at]);
                        wrong.Add($"case {n}, record {r.Record.Table:X}/{r.Record.Index}, offset {off:X}: {r.Record.Bytes[off]:X2}, game {r.Expected[off]:X2}; pokes {c[11]}");
                    }
                }
                n++;
            }
            finally
            {
                for (int i = 0; i < records.Length; i++) before[i].CopyTo(records[i].Record.Bytes, 0);
                for (int i = 0; i < cities.Length; i++) cityBefore[i].CopyTo(cities[i].Bytes, 0);
                undo();
            }
        }
        Assert.True(n >= 3_000, $"only {n} cases");
        Assert.True(changed > 0 && queued > 0 && destroyed > 0, $"changes {changed}, requests {queued}, destructions {destroyed}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }
}

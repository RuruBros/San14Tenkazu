using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TacticDispatchTests
{
    [Fact]
    public void SequentialSlotsRecordsMeritMapsAndRequestsMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0, changed = 0, queued = 0, linked = 0;
        Rec? Reference(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null
            : w.Table(Convert.ToInt32(e[0].GetString()!, 16))[e[1].GetInt32()];
        foreach (JsonElement c in Vectors.Read("tactic_dispatch.json.gz").GetProperty("apply").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[10]);
            var records = c[8].EnumerateArray().Select(e => (Record: Reference(e)!, Expected: Convert.FromHexString(e[2].GetString()!))).ToArray();
            var before = records.Select(r => r.Record.Bytes.ToArray()).ToArray();
            var cities = w.Table(Tables.Cities);
            var cityBefore = cities.Select(r => r.Bytes.ToArray()).ToArray();
            try
            {
                var targets = c[4].EnumerateArray().Select(slot => (IReadOnlyList<Rec>)slot.EnumerateArray().Select(h => w.Table(Tables.Tiles)[h.GetInt32()]).ToArray()).ToArray();
                var previous = c[3].EnumerateArray().Select(e => (Rec?)w.Table(Tables.Tactics)[e.GetInt32()]).ToArray();
                Rec? main = c[2].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[c[2].GetInt32()];
                var got = TacticEffects.Dispatch(w, w.Table(Tables.Tactics)[c[0].GetInt32()], c[1][0].GetInt32(), c[1][1].GetInt32(),
                    main, previous, targets, c[5].GetInt32(), c[6].GetInt32(), c[7].GetInt32() != 0);
                var native = c[9];
                int expectedIndex = native[0].GetArrayLength() == 0 ? -1 : native[0][0][0].GetInt32();
                bool resultsMatch = got.ResultIndex == expectedIndex;
                if (expectedIndex >= 0)
                    for (int slot = 0; slot < 2; slot++)
                    {
                        var expected = native[0][0][3][slot].EnumerateArray().Select(e => new TacticEffects.Applied(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32()));
                        resultsMatch &= got.Slots[slot].SequenceEqual(expected);
                    }
                bool mapsMatch = true;
                for (int k = 0; k < 2; k++)
                {
                    var map = k == 0 ? got.UnitDamage : got.BaseDamage;
                    var expected = native[1][k].EnumerateArray().Select(e => (Record: Reference(e)!, Amount: e[2].GetInt32())).ToArray();
                    mapsMatch &= map.Count == expected.Length && expected.All(e => map.TryGetValue(e.Record, out int amount) && amount == e.Amount);
                }
                var requests = native[2].EnumerateArray().Select(e => new TacticEffects.PendingDestruction(Reference(e[0]), Reference(e[1]), Reference(e[2]), Reference(e[3]), e[5].GetInt32(), e[6].GetInt32())).ToArray();
                queued += requests.Length;
                linked += expectedIndex > 0 ? 1 : 0;
                if ((!resultsMatch || !mapsMatch || !got.Pending.SequenceEqual(requests) || got.Destroyed != (native[3].GetInt32() != 0)) && wrong.Count < 20)
                    wrong.Add($"case {n}: row {got.ResultIndex}, slots [{string.Join(',', got.Slots[0])}]/[{string.Join(',', got.Slots[1])}], maps {mapsMatch}, requests [{string.Join(',', got.Pending)}], flag {got.Destroyed}; native {native}; pokes {c[10]}");
                foreach (var r in records)
                {
                    if (!r.Record.Bytes.SequenceEqual(r.Expected))
                    {
                        int off = Enumerable.Range(0, r.Expected.Length).First(at => r.Record.Bytes[at] != r.Expected[at]);
                        if (wrong.Count < 20) wrong.Add($"case {n}, record {r.Record.Table:X}/{r.Record.Index}, offset {off:X}: {r.Record.Bytes[off]:X2}, game {r.Expected[off]:X2}; pokes {c[10]}");
                    }
                }
                changed += records.Where((r, i) => !before[i].SequenceEqual(r.Expected)).Count();
                n++;
            }
            finally
            {
                for (int i = 0; i < records.Length; i++) before[i].CopyTo(records[i].Record.Bytes, 0);
                for (int i = 0; i < cities.Length; i++) cityBefore[i].CopyTo(cities[i].Bytes, 0);
                undo();
            }
        }
        Assert.True(n >= 3_000 && changed > 0 && queued > 0 && linked > 0, $"cases {n}, changed records {changed}, requests {queued}, linked entries {linked}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }
}

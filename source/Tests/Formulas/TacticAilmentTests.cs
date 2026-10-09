using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TacticAilmentTests
{
    private static Tactic.Side Side(World w, JsonElement source) => new(
        source[0].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[source[0].GetInt32()],
        source[1].ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(source[1][0].GetString()!, 16))[source[1][1].GetInt32()]);

    [Fact]
    public void SuccessMatchesTheGamesSeededRoll()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        var outcomes = new HashSet<bool>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("tactic_ailment.json.gz").GetProperty("success").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[4]);
            try
            {
                bool got = TacticEffects.AilmentSucceeds(w, w.Table(Tables.Tactics)[c[0].GetInt32()], Side(w, c[1]), Side(w, c[2]));
                outcomes.Add(c[3].GetInt32() != 0);
                if (got != (c[3].GetInt32() != 0) && wrong.Count < 20)
                    wrong.Add($"case {n}, tactic {c[0]}, source {c[1]}, target {c[2]}: {got}, game {c[3]}; pokes {c[4]}");
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 2_400, $"only {n} cases");
        Assert.Equal(2, outcomes.Count);
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void ResistanceAndDurationWritesMatchTheGamesEntireRecord()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("tactic_ailment.json.gz").GetProperty("add").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[6]);
            Tactic.Side target = Side(w, c[0]);
            Rec record = target.Unit ?? target.Base!;
            byte[] before = record.Bytes.ToArray();
            try
            {
                Rec? source = c[1].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[c[1].GetInt32()];
                Rec? tile = c[4].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Tiles)[c[4].GetInt32()];
                if (target.Unit is not null) TacticEffects.AddUnitAilment(w, record, source, c[2].GetInt32(), c[3].GetInt32(), tile);
                else TacticEffects.AddBaseAilment(w, record, source, c[2].GetInt32(), c[3].GetInt32(), tile);
                byte[] expected = Convert.FromHexString(c[5].GetString()!);
                if (!record.Bytes.SequenceEqual(expected) && wrong.Count < 20)
                {
                    int off = Enumerable.Range(0, expected.Length).First(at => record.Bytes[at] != expected[at]);
                    wrong.Add($"case {n}, target {c[0]}, source {c[1]}, kind {c[2]}, duration {c[3]}, offset {off:X}: {record.Bytes[off]:X2}, game {expected[off]:X2}; pokes {c[6]}");
                }
                n++;
            }
            finally { before.CopyTo(record.Bytes, 0); undo(); }
        }
        Assert.True(n >= 2_400, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void EffectsMatchTheGamesRecordsAndResults()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        var changedKinds = new HashSet<int>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("tactic_ailment.json.gz").GetProperty("apply").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[8]);
            var records = c[6].EnumerateArray().Select(e => (Record: w.Table(Convert.ToInt32(e[0].GetString()!, 16))[e[1].GetInt32()], Expected: Convert.FromHexString(e[2].GetString()!))).ToArray();
            var before = records.Select(r => r.Record.Bytes.ToArray()).ToArray();
            try
            {
                int kind = c[4].GetInt32();
                var targets = c[5].EnumerateArray().Select(h => w.Table(Tables.Tiles)[h.GetInt32()]).ToArray();
                var got = TacticEffects.ApplyAilment(w, w.Table(Tables.Tactics)[c[0].GetInt32()], Side(w, c[1]), c[2].GetInt32(), c[3].GetInt32(), kind, targets);
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
        Assert.True(n >= 4_000, $"only {n} cases");
        Assert.True(new[] {2, 3, 4, 23}.All(changedKinds.Contains), $"effect kinds with actual mutations: {string.Join(',', changedKinds)}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }
}

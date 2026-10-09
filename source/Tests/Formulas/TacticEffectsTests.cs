using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TacticEffectsTests
{
    private static Tactic.Side Side(World w, JsonElement source) => new(
        source[0].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[source[0].GetInt32()],
        source[1].ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(source[1][0].GetString()!, 16))[source[1][1].GetInt32()]);

    [Fact]
    public void SupportFactorsMatchTheGamesCodeBitForBit()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("tactic_support.json.gz").GetProperty("factors").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[3]);
            try
            {
                Tactic.Side source = Side(w, c[1]);
                float[] got = [TacticEffects.SupportFactor(w, w.Table(Tables.Tactics)[c[0].GetInt32()], source), TacticEffects.SupplyFactor(w, source)];
                for (int i = 0; i < 2; i++)
                    if (!BitConverter.GetBytes(got[i]).SequenceEqual(Convert.FromHexString(c[2][i].GetString()!)) && wrong.Count < 20)
                        wrong.Add($"tactic {c[0]}, source {c[1]}, factor {i}: {got[i]:R}, game bits {c[2][i]}; pokes {c[3]}");
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 1_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void SupportMutationsAndResultsMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        var changedKinds = new HashSet<int>();
        foreach (JsonElement c in Vectors.Read("tactic_support.json.gz").GetProperty("apply").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[8]);
            var records = c[6].EnumerateArray().Select(e => (Record: w.Table(Convert.ToInt32(e[0].GetString()!, 16))[e[1].GetInt32()], Expected: Convert.FromHexString(e[2].GetString()!))).ToArray();
            var before = records.Select(r => r.Record.Bytes.ToArray()).ToArray();
            try
            {
                int kind = c[4].GetInt32();
                var targets = c[5].EnumerateArray().Select(h => w.Table(Tables.Tiles)[h.GetInt32()]).ToArray();
                var got = TacticEffects.ApplySupport(w, w.Table(Tables.Tactics)[c[0].GetInt32()], Side(w, c[1]), c[2].GetInt32(), c[3].GetInt32(), kind, targets);
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
        Assert.True(new[] { 15, 19, 20 }.All(changedKinds.Contains), $"effect kinds with actual mutations: {string.Join(',', changedKinds)}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void GettersMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("tactic_targets.json.gz").GetProperty("getters").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[4]);
            try
            {
                Rec t = w.Table(Tables.Tactics)[c[0].GetInt32()];
                int slot = c[1].GetInt32();
                int[] got = [TacticEffects.Kind(t, slot), TacticEffects.Range(t, slot), TacticEffects.Amount(t, slot),
                    TacticEffects.Level(t, slot), TacticEffects.AbilityKind(t), TacticEffects.Flag(t, c[2].GetInt32()) ? 1 : 0];
                int[] expected = c[3].EnumerateArray().Select(v => v.GetInt32()).ToArray();
                if (!got.SequenceEqual(expected) && wrong.Count < 20) wrong.Add($"tactic {c[0]}, slot {slot}: {string.Join(',', got)}, game {c[3]}; pokes {c[4]}");
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 2_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void TargetsMatchTheGamesCodeInOrder()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        var kinds = new HashSet<int>();
        foreach (JsonElement c in Vectors.Read("tactic_targets.json.gz").GetProperty("targets").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[5]);
            try
            {
                Rec? t = c[0].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Tactics)[c[0].GetInt32()];
                Rec? source = c[1].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Tiles)[c[1].GetInt32()];
                Rec? target = c[2].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Tiles)[c[2].GetInt32()];
                List<Rec>[] got = TacticEffects.Targets(w, t, source, target, c[3].GetInt32());
                for (int slot = 0; slot < 2; slot++)
                {
                    if (t is not null && Objects.Valid(w, t)) kinds.Add(TacticEffects.Kind(t, slot));
                    int[] expected = c[4][slot].EnumerateArray().Select(v => v.GetInt32()).ToArray();
                    if (!got[slot].Select(v => v.Index).SequenceEqual(expected) && wrong.Count < 20)
                        wrong.Add($"case {n}, tactic {c[0]}, hex {c[1]}->{c[2]}, force {c[3]}, slot {slot}: [{string.Join(',', got[slot].Select(v => v.Index))}], game {c[4][slot]}; pokes {c[5]}");
                }
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 6_000, $"only {n} cases");
        Assert.True(Enumerable.Range(1, 23).All(kinds.Contains), $"effect kinds covered: {string.Join(',', kinds.Order())}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void TroopCapacityMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("tactic_targets.json.gz").GetProperty("capacity").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[2]);
            int got;
            try { got = TroopCapacity.Get(w, w.Table(Tables.Officers)[c[0].GetInt32()]); }
            finally { undo(); }
            if (got != c[1].GetInt32() && wrong.Count < 20) wrong.Add($"officer {c[0]}: {got}, game {c[1]}; pokes {c[2]}");
            n++;
        }
        Assert.True(n >= 3_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void MissingEncodedTacticVectorIsReported()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        Rec original = w.Table(Tables.Tactics).First(t => BitConverter.ToInt64(t.Bytes, 0x78) - BitConverter.ToInt64(t.Bytes, 0x70) == 40);
        Rec missing = new(original.Index, original.Bytes.ToArray(), Tables.Tactics);
        Assert.Throws<InvalidOperationException>(() => TacticEffects.Kind(missing, 0));
    }
}

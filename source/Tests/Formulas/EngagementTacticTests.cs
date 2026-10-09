using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class EngagementTacticTests
{
    [Fact]
    public void EligibilityDeferredDamageAndWholeTableChangesMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        JsonElement data = Vectors.Read("engagement_tactic.json.gz");
        int[] tables = data.GetProperty("tables").EnumerateArray().Select(e => Convert.ToInt32(e.GetString()![2..], 16)).ToArray();
        var wrong = new List<string>();
        int n = 0, allowed = 0, denied = 0, fired = 0, linked = 0, missing = 0, transferred = 0;
        object? Ref(Rec? rec) => rec is null ? null : new object[] { $"0x{rec.Table:x}", rec.Index };
        object Maps(Dictionary<Rec, int>? map) => map is null ? Array.Empty<object>() : map.OrderBy(e => e.Key.Table).ThenBy(e => e.Key.Index)
            .Select(e => new object[] { $"0x{e.Key.Table:x}", e.Key.Index, e.Value }).ToArray();
        foreach (JsonElement c in data.GetProperty("cases").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[3]);
            var before = tables.ToDictionary(t => t, t => w.Table(t).Select(r => (byte[])r.Bytes.Clone()).ToArray());
            try
            {
                Engagement.Side a = Side(c[0]), b = Side(c[1]);
                JsonElement expected = c[2], inputs = c[4];
                bool ready = EngagementTactic.Allowed(w, a, b);
                var frame = new EngagementTactic.Frame((ushort)inputs[1].GetInt32(), (ushort)inputs[2].GetInt32(), inputs[3].GetInt32());
                var got = EngagementTactic.Apply(w, inputs[4].GetBoolean() ? null : a, b, inputs[0].GetInt32(), frame);
                TacticFlow.Resolution? flow = got.Flow;
                bool match = ready == (expected[10].GetInt32() != 0) && got.NextStep == expected[9].GetInt32();
                int[] fields = [got.Frame.BasicDamage, got.Frame.ExtraDamage, got.Frame.TacticFired];
                match &= fields.SequenceEqual(expected[8].EnumerateArray().Select(e => e.GetInt32()));
                match &= (flow?.Main is null ? 0 : 1) == expected[0].GetInt32();
                match &= (flow?.Candidates.Select(t => t.Index) ?? []).SequenceEqual(expected[1].EnumerateArray().Select(e => e.GetInt32()));
                object queue = flow?.Commands.Select(e => new object[] { e.Kind, e.Number, e.Tactic.Index,
                    e.Targets.Select(list => list.Select(t => t.Index).ToArray()).ToArray() }).ToArray() ?? [];
                match &= JsonSerializer.Serialize(queue) == Compact(expected[2]);
                var rows = new SortedDictionary<int, object>();
                if (flow is { } result)
                    for (int i = 0; i < result.Applications.Count; i++)
                    {
                        var application = result.Applications[i]; var command = result.Commands[i];
                        rows[application.ResultIndex] = new object[] { application.ResultIndex, command.Tactic.Index, command.Kind, command.Number,
                            application.Slots.Select(slot => slot.Select(r => new[] { r.Kind, r.Number, r.Hex, r.Amount }).ToArray()).ToArray() };
                    }
                match &= JsonSerializer.Serialize(rows.Values.ToArray()) == Compact(expected[3]);
                JsonElement maps = JsonSerializer.SerializeToElement(new[] { Maps(flow?.UnitDamage), Maps(flow?.BaseDamage) });
                for (int kind = 0; kind < 2; kind++)
                    match &= maps[kind].EnumerateArray().Select(Compact).Order().SequenceEqual(expected[4][kind].EnumerateArray().Select(Compact).Order());
                object pending = flow?.Pending.Select(e => new object?[] { Ref(e.Unit), Ref(e.Base), Ref(e.AttackerUnit), Ref(e.AttackerBase), 0, e.Mode, e.Reason }).ToArray() ?? [];
                match &= JsonSerializer.Serialize(pending) == Compact(expected[5]) && (flow?.Destroyed ?? false) == (expected[6].GetInt32() != 0);
                var changes = expected[7].EnumerateArray().ToDictionary(e => (Table: Convert.ToInt32(e[0].GetString()![2..], 16), Index: e[1].GetInt32()),
                    e => Convert.FromHexString(e[2].GetString()!));
                string? differingRecord = null;
                foreach (int table in tables)
                    for (int i = 0; i < before[table].Length; i++)
                    {
                        byte[] bytes = changes.GetValueOrDefault((table, i), before[table][i]);
                        if (w.Table(table)[i].Bytes.AsSpan().SequenceEqual(bytes)) continue;
                        match = false; differingRecord ??= $"record {table:x}/{i}";
                    }
                if (!match && wrong.Count < 20) wrong.Add($"case {n}: allowed {ready}, step {got.NextStep}, frame [{string.Join(',', fields)}], {differingRecord}; native {expected}; inputs {inputs}; pokes {c[3]}");
                if (ready) allowed++; else denied++;
                fired += expected[0].GetInt32(); linked += expected[2].GetArrayLength() > 1 ? 1 : 0;
                missing += inputs[4].GetBoolean() ? 1 : 0;
                transferred += expected[8][1].GetInt32() != inputs[2].GetInt32() ? 1 : 0;
                n++;
            }
            finally
            {
                foreach (int table in tables)
                    for (int i = 0; i < before[table].Length; i++) Array.Copy(before[table][i], w.Table(table)[i].Bytes, before[table][i].Length);
                undo();
            }
        }
        Assert.True(n >= 1200 && allowed > 0 && denied > 0 && fired > 0 && linked > 0 && missing > 0 && transferred > 0,
            $"cases {n}, allowed {allowed}, denied {denied}, fired {fired}, linked {linked}, missing {missing}, transferred {transferred}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void DeferredMapsUseArrayNumbersNativePointerOrderAndLowWords()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        int n = 0, negative = 0, aboveWord = 0, collisions = 0, preserved = 0;
        foreach (JsonElement c in Vectors.Read("engagement_tactic.json.gz").GetProperty("transfer").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[6]);
            try
            {
                Dictionary<Rec, int> Map(JsonElement e) => e.EnumerateArray().ToDictionary(
                    row => w.Table(Convert.ToInt32(row[0].GetString()![2..], 16))[row[1].GetInt32()], row => row[2].GetInt32());
                Engagement.Side target = Side(c[0]);
                var before = new EngagementTactic.Frame((ushort)c[1][0].GetInt32(), (ushort)c[1][1].GetInt32(), c[1][2].GetInt32());
                var units = Map(c[2]); var bases = Map(c[3]);
                var got = EngagementTactic.Transfer(w, target, before, units, bases);
                Assert.Equal(c[4][0].GetInt32(), got.BasicDamage);
                Assert.Equal(c[4][1].GetInt32(), got.ExtraDamage);
                Assert.Equal(c[4][2].GetInt32(), got.TacticFired);
                Assert.Equal(7, c[5].GetInt32());
                negative += units.Values.Concat(bases.Values).Any(v => v < 0) ? 1 : 0;
                aboveWord += units.Values.Concat(bases.Values).Any(v => v > ushort.MaxValue) ? 1 : 0;
                collisions += target.Kind is 5 or 6 && bases.Keys.Count(r => Objects.Valid(w, r)
                    && (r.U16(0x4E) <= 51 ? r.U16(0x4E) : (uint)(r.U16(0x4E) - 52) < 10 ? r.U16(0x4E) - 51 : 0) == target.Index) > 1 ? 1 : 0;
                preserved += got.ExtraDamage == before.ExtraDamage ? 1 : 0;
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 1200 && negative > 100 && aboveWord > 100 && collisions > 100 && preserved > 100,
            $"cases {n}, negative {negative}, above word {aboveWord}, collisions {collisions}, preserved {preserved}");
    }

    [Fact]
    public void BasicAndExtraWordsAreZeroExtendedBeforeTheMinimumDamageRule()
    {
        int n = 0, aboveWord = 0;
        foreach (JsonElement c in Vectors.Read("engagement_tactic.json.gz").GetProperty("combine").EnumerateArray())
        {
            var before = new EngagementTactic.Frame((ushort)c[0][0].GetInt32(), (ushort)c[0][1].GetInt32(), c[0][2].GetInt32());
            var got = EngagementTactic.Combine(before);
            Assert.Equal(c[1][0].GetInt32(), got.Frame.BasicDamage);
            Assert.Equal(c[1][1].GetInt32(), got.Frame.ExtraDamage);
            Assert.Equal(c[1][2].GetInt32(), got.Frame.TacticFired);
            Assert.Equal(c[2].GetInt32(), got.TotalDamage);
            Assert.Equal(8, c[3].GetInt32());
            aboveWord += got.TotalDamage > ushort.MaxValue ? 1 : 0;
            n++;
        }
        Assert.True(n >= 1200 && aboveWord > 100, $"cases {n}, sums above 65535 {aboveWord}");
    }

    private static Engagement.Side Side(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32(), e[4].GetInt32());
    private static string Compact(JsonElement element) => JsonSerializer.Serialize(element);
}

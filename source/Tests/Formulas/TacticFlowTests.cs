using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TacticFlowTests
{
    [Fact]
    public void MainSelectionsQueuesDamageMapsAndWholeTableChangesMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        JsonElement data = Vectors.Read("tactic_flow.json.gz");
        int[] tables = data.GetProperty("tables").EnumerateArray().Select(e => Convert.ToInt32(e.GetString()![2..], 16)).ToArray();
        var wrong = new List<string>();
        int n = 0, fired = 0, linked = 0, sorted = 0, noFire = 0;
        Engagement.Side Side(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32(), e[4].GetInt32());
        object? Ref(Rec? rec) => rec is null ? null : new object[] { $"0x{rec.Table:x}", rec.Index };
        object Maps(Dictionary<Rec, int> map) => map.OrderBy(e => e.Key.Table).ThenBy(e => e.Key.Index)
            .Select(e => new object[] { $"0x{e.Key.Table:x}", e.Key.Index, e.Value }).ToArray();
        foreach (JsonElement c in data.GetProperty("cases").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[3]);
            var before = tables.ToDictionary(t => t, t => w.Table(t).Select(r => (byte[])r.Bytes.Clone()).ToArray());
            try
            {
                var got = TacticFlow.Apply(w, Side(c[0]), Side(c[1]));
                JsonElement expected = c[2];
                bool match = (got.Main is null ? 0 : 1) == expected[0].GetInt32();
                match &= got.Candidates.Select(t => t.Index).SequenceEqual(expected[1].EnumerateArray().Select(e => e.GetInt32()));
                object queue = got.Commands.Select(e => new object[] { e.Kind, e.Number, e.Tactic.Index,
                    e.Targets.Select(list => list.Select(t => t.Index).ToArray()).ToArray() }).ToArray();
                match &= JsonSerializer.Serialize(queue) == expected[2].GetRawText().Replace(" ", "");
                var rows = new SortedDictionary<int, object>();
                for (int i = 0; i < got.Applications.Count; i++)
                {
                    var application = got.Applications[i]; var command = got.Commands[i];
                    rows[application.ResultIndex] = new object[] { application.ResultIndex, command.Tactic.Index, command.Kind, command.Number,
                        application.Slots.Select(slot => slot.Select(r => new[] { r.Kind, r.Number, r.Hex, r.Amount }).ToArray()).ToArray() };
                }
                match &= JsonSerializer.Serialize(rows.Values.ToArray()) == expected[3].GetRawText().Replace(" ", "");
                JsonElement actualMaps = JsonSerializer.SerializeToElement(new[] { Maps(got.UnitDamage), Maps(got.BaseDamage) });
                for (int kind = 0; kind < 2; kind++)
                {
                    string[] actual = actualMaps[kind].EnumerateArray().Select(e => e.GetRawText()).Order().ToArray();
                    string[] native = expected[4][kind].EnumerateArray().Select(e => e.GetRawText().Replace(" ", "")).Order().ToArray();
                    match &= actual.SequenceEqual(native);
                }
                object pending = got.Pending.Select(e => new object?[] { Ref(e.Unit), Ref(e.Base), Ref(e.AttackerUnit), Ref(e.AttackerBase), 0, e.Mode, e.Reason }).ToArray();
                match &= JsonSerializer.Serialize(pending) == expected[5].GetRawText().Replace(" ", "");
                match &= got.Destroyed == (expected[6].GetInt32() != 0);
                var changes = new Dictionary<(int Table, int Index), byte[]>();
                foreach (JsonElement change in expected[7].EnumerateArray())
                    changes[(Convert.ToInt32(change[0].GetString()![2..], 16), change[1].GetInt32())] = Convert.FromHexString(change[2].GetString()!);
                string? differingRecord = null;
                foreach (int table in tables)
                    for (int i = 0; i < before[table].Length; i++)
                    {
                        byte[] bytes = changes.GetValueOrDefault((table, i), before[table][i]);
                        if (w.Table(table)[i].Bytes.AsSpan().SequenceEqual(bytes)) continue;
                        match = false;
                        differingRecord ??= $"record {table:x}/{i}: offsets [{string.Join(',', Enumerable.Range(0, bytes.Length).Where(o => bytes[o] != w.Table(table)[i].Bytes[o]).Select(o => o.ToString("x")))}]";
                    }
                if (!match && wrong.Count < 20) wrong.Add($"case {n}: {differingRecord}; candidates [{string.Join(',', got.Candidates.Select(t => t.Index))}], queue {JsonSerializer.Serialize(queue)}; native {expected}; pokes {c[3]}");
                fired += expected[0].GetInt32(); noFire += expected[0].GetInt32() == 0 ? 1 : 0;
                linked += expected[2].GetArrayLength() > 1 ? 1 : 0;
                sorted += expected[1].GetArrayLength() > 1 ? 1 : 0;
                n++;
            }
            finally
            {
                foreach (int table in tables)
                    for (int i = 0; i < before[table].Length; i++) Array.Copy(before[table][i], w.Table(table)[i].Bytes, before[table][i].Length);
                undo();
            }
        }
        Assert.True(n >= 1_200 && fired > 0 && noFire > 0 && sorted > 0 && linked > 0, $"cases {n}, fired {fired}, no fire {noFire}, multiple candidates {sorted}, linked {linked}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }
}

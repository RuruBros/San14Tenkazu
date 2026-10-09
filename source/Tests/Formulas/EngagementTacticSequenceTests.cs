using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class EngagementTacticSequenceTests
{
    private static Engagement.Side Side(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32(), e[4].GetInt32());
    private static string Compact(JsonElement e) => e.GetRawText().Replace(" ", "");
    private static object Maps(Dictionary<Rec, int>? map) => map is null ? Array.Empty<object>() : map.OrderBy(e => e.Key.Table).ThenBy(e => e.Key.Index)
        .Select(e => new object[] { $"0x{e.Key.Table:x}", e.Key.Index, e.Value }).ToArray();

    [Fact]
    public void ActiveTacticsAndLinkedDamageMatchConsecutiveNativeEngagements()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        JsonElement data = Vectors.Read("engagement_tactic_sequence.json.gz");
        int[] tables = data.GetProperty("tables").EnumerateArray().Select(t => Convert.ToInt32(t.GetString()!, 16)).ToArray();
        int[] phases = data.GetProperty("phases").EnumerateArray().Select(p => p.GetInt32()).ToArray();
        var wrong = new List<string>();
        int n = 0, fired = 0, linked = 0, transferred = 0, records = 0, flowCalled = 0;
        foreach (JsonElement c in data.GetProperty("cases").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[4]);
            var before = tables.ToDictionary(t => t, t => w.Table(t).Select(r => (byte[])r.Bytes.Clone()).ToArray());
            try
            {
                int index = 0;
                foreach (JsonElement pair in c[0].EnumerateArray())
                {
                    Engagement.Side a = Side(pair[0]), b = Side(pair[1]);
                    JsonElement trace = c[2][index++]; records++;
                    if (!Engagement.CanResolve(w, a, b)) throw new InvalidOperationException($"case {n}/{index}: Core rejected an admitted native record");
                    if (!trace.GetProperty("stages").EnumerateArray().Select(s => s.GetInt32()).SequenceEqual(phases)) throw new InvalidOperationException("Unexpected native phase order");
                    Engagement.StartResult start = Engagement.Start(w, a, b);
                    if (start.NextStep != 2 || start.Siege is not null) throw new InvalidOperationException("Fixture must remain on the unit path");
                    Engagement.Powers power = Engagement.Compute(w, a, b);
                    uint[] calculation = [BitConverter.SingleToUInt32Bits(power.PowerA), BitConverter.SingleToUInt32Bits(power.AttackA), BitConverter.SingleToUInt32Bits(power.DefenseA),
                        BitConverter.SingleToUInt32Bits(power.PowerB), BitConverter.SingleToUInt32Bits(power.AttackB), BitConverter.SingleToUInt32Bits(power.DefenseB), unchecked((ushort)power.Damage)];
                    if (!calculation.SequenceEqual(trace.GetProperty("calculation").EnumerateArray().Select(v => v.GetUInt32())) && wrong.Count < 20)
                        wrong.Add($"case {n}/{index}: calculation {string.Join(',', calculation)}, native {trace.GetProperty("calculation")}");
                    // The native records retain their original cached troop word throughout all three engagements.
                    var tactic = EngagementTactic.Apply(w, a, b, 20000, new(unchecked((ushort)power.Damage), 0, 0));
                    TacticFlow.Resolution? flow = tactic.Flow;
                    if ((flow is not null) != trace.GetProperty("flow_called").GetBoolean() && wrong.Count < 20)
                        wrong.Add($"case {n}/{index}: flow call Core {flow is not null}, native {trace.GetProperty("flow_called")}");
                    if (flow is { } result)
                    {
                        flowCalled++;
                        if (result.Pending.Count != 0 || result.Destroyed) throw new InvalidOperationException("Fixture must not enqueue destruction");
                        if (!FlowMatches(result, trace.GetProperty("flow")) && wrong.Count < 20)
                            wrong.Add($"case {n}/{index}: tactic candidates/queue/rows/maps differ; Core {JsonSerializer.Serialize(FlowEvidence(result))}; native {trace.GetProperty("flow")}");
                        if (result.Commands.Count > 1) linked++;
                    }
                    int[] tacticFrame = [tactic.Frame.BasicDamage, tactic.Frame.ExtraDamage, tactic.Frame.TacticFired];
                    if (!tacticFrame.SequenceEqual(trace.GetProperty("tactic_frame").EnumerateArray().Select(v => v.GetInt32())) && wrong.Count < 20)
                        wrong.Add($"case {n}/{index}: tactic frame {string.Join(',', tacticFrame)}, native {trace.GetProperty("tactic_frame")}");
                    if (tactic.Frame.TacticFired != 0) fired++;
                    if (tactic.Frame.ExtraDamage != 0) transferred++;
                    var combined = EngagementTactic.Combine(tactic.Frame);
                    int[] frame = [combined.Frame.BasicDamage, combined.Frame.ExtraDamage, combined.Frame.TacticFired];
                    if ((tactic.NextStep != 7 || !frame.SequenceEqual(trace.GetProperty("frame").EnumerateArray().Select(v => v.GetInt32()))) && wrong.Count < 20)
                        wrong.Add($"case {n}/{index}: final frame {string.Join(',', frame)}, native {trace.GetProperty("frame")}");
                    EngagementSequenceTests.ApplyUnit(w, a, b, combined.TotalDamage);
                    EngagementSequenceTests.ApplyEnd(w, a, b);
                }
                var expected = c[3].EnumerateArray().ToDictionary(e => (Table: Convert.ToInt32(e[0].GetString()!, 16), Index: e[1].GetInt32()), e => Convert.FromHexString(e[2].GetString()!));
                foreach (int t in tables)
                    foreach (Rec rec in w.Table(t))
                    {
                        byte[] wanted = expected.GetValueOrDefault((t, rec.Index), before[t][rec.Index]);
                        if (!rec.Bytes.SequenceEqual(wanted) && wrong.Count < 20)
                        {
                            int offset = Enumerable.Range(0, rec.Bytes.Length).First(i => rec.Bytes[i] != wanted[i]);
                            wrong.Add($"case {n}: table {t:x}[{rec.Index}] +{offset:x}: Core {rec.Bytes[offset]:x2}, native {wanted[offset]:x2}");
                        }
                    }
                if (c[1].GetInt32() != 1) throw new InvalidOperationException("Native caller did not finish normally");
            }
            finally
            {
                foreach (int t in tables)
                    foreach (Rec rec in w.Table(t)) before[t][rec.Index].CopyTo(rec.Bytes, 0);
                undo();
            }
            n++;
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.Equal(600, n);
        Assert.Equal(1800, records);
        JsonElement stats = data.GetProperty("stats");
        Assert.Equal(stats.GetProperty("flow_called").GetInt32(), flowCalled);
        Assert.Equal(stats.GetProperty("fired").GetInt32(), fired);
        Assert.Equal(stats.GetProperty("linked").GetInt32(), linked);
        Assert.Equal(stats.GetProperty("transferred").GetInt32(), transferred);
        Assert.True(fired > 500 && fired < records && linked > 300 && transferred > 300);
    }

    private static object FlowEvidence(TacticFlow.Resolution flow)
    {
        var rows = new SortedDictionary<int, object>();
        for (int i = 0; i < flow.Applications.Count; i++)
        {
            var application = flow.Applications[i]; var command = flow.Commands[i];
            rows[application.ResultIndex] = new object[] { application.ResultIndex, command.Tactic.Index, command.Kind, command.Number,
                application.Slots.Select(slot => slot.Select(r => new[] { r.Kind, r.Number, r.Hex, r.Amount }).ToArray()).ToArray() };
        }
        return new { candidates = flow.Candidates.Select(t => t.Index).ToArray(),
            queue = flow.Commands.Select(e => new object[] { e.Kind, e.Number, e.Tactic.Index, e.Targets.Select(list => list.Select(t => t.Index).ToArray()).ToArray() }).ToArray(),
            rows = rows.Values.ToArray(), maps = new[] { Maps(flow.UnitDamage), Maps(flow.BaseDamage) } };
    }

    internal static bool FlowMatches(TacticFlow.Resolution flow, JsonElement expected)
    {
        JsonElement actual = JsonSerializer.SerializeToElement(FlowEvidence(flow));
        foreach (string field in new[] { "candidates", "queue", "rows" })
            if (Compact(actual.GetProperty(field)) != Compact(expected.GetProperty(field))) return false;
        for (int k = 0; k < 2; k++)
            if (!actual.GetProperty("maps")[k].EnumerateArray().Select(Compact).Order().SequenceEqual(expected.GetProperty("maps")[k].EnumerateArray().Select(Compact).Order())) return false;
        return true;
    }
}

using System.Buffers.Binary;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class EngagementSiegeSequenceTests
{
    private static Engagement.Side Side(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32(), e[4].GetInt32());
    private static void U16(Rec r, int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(r.Bytes.AsSpan(at), unchecked((ushort)value));
    private static void I32(Rec r, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(r.Bytes.AsSpan(at), value);

    [Fact]
    public void ConsecutiveSiegeRetaliationAndTacticsMatchTheWholeNativeCaller()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        JsonElement data = Vectors.Read("engagement_siege_sequence.json.gz");
        int[] tables = data.GetProperty("tables").EnumerateArray().Select(t => Convert.ToInt32(t.GetString()!, 16)).ToArray();
        int[] phases = data.GetProperty("phases").EnumerateArray().Select(p => p.GetInt32()).ToArray();
        var wrong = new List<string>();
        int n = 0, records = 0, admitted = 0, siegeCalls = 0, flowCalled = 0, fired = 0, linked = 0, transferred = 0;
        foreach (JsonElement c in data.GetProperty("cases").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[4]);
            var before = tables.ToDictionary(t => t, t => w.Table(t).Select(r => (byte[])r.Bytes.Clone()).ToArray());
            try
            {
                Engagement.Side fixtureTarget = Side(c[0][0][1]);
                Rec fixtureBuilding = Building(w, fixtureTarget);
                Rec? fixtureBase = fixtureTarget.Kind is 5 or 6 ? Base(w, fixtureTarget) : null;
                int index = 0;
                foreach (JsonElement pair in c[0].EnumerateArray())
                {
                    Engagement.Side a = Side(pair[0]), b = Side(pair[1]);
                    JsonElement trace = c[2][index++]; records++;
                    bool allowed = Engagement.CanResolve(w, a, b);
                    int[] stages = trace.GetProperty("stages").EnumerateArray().Select(s => s.GetInt32()).ToArray();
                    if (!allowed)
                    {
                        if (stages.Length != 0 && wrong.Count < 20) wrong.Add($"case {n}/{index}: Core rejected an admitted native record");
                        continue;
                    }
                    admitted++;
                    Engagement.StartResult start = Engagement.Start(w, a, b);
                    if (start.Siege is { } siege)
                    {
                        siegeCalls++;
                        Rec unit = w.Table(Tables.Units)[a.Index], building = start.Building!;
                        int rawDamage = (int)Attack.SiegeDamage(w, unit, Objects.FormationOf(w, unit), true);
                        int[] call = [building.Index, rawDamage];
                        if (!trace.TryGetProperty("siege_call", out JsonElement nativeCall) || !call.SequenceEqual(nativeCall.EnumerateArray().Select(v => v.GetInt32())))
                            Add($"siege call {string.Join(',', call)} differs");
                        if (siege.Fell) throw new InvalidOperationException("Fixture must not fall");
                        if (Bombard.Allowed(w, building) && !w.List(Lists.SiegeExempt).Any(row => ReferenceEquals(row[0], building)))
                        {
                            int adjusted = Bombard.AdjustedDamage(w, building, rawDamage, unit);
                            if (adjusted > 0) Gain(w, unit, 1, adjusted);
                            U16(building, 0x14, siege.Durability);
                            if (b.Kind is 5 or 6 && Base(w, b).U8(0x66) != 3) Base(w, b).Bytes[0x66] = (byte)Engagement.DamageLevel(w, Base(w, b));
                        }
                    }
                    else if (trace.TryGetProperty("siege_call", out _)) Add("Core omitted a native siege call");
                    JsonElement startState = trace.GetProperty("start_state");
                    if (start.NextStep != trace.GetProperty("start_step").GetInt32()
                        || fixtureBuilding.U16(0x14) != startState[0].GetInt32()
                        || fixtureBase is not null && fixtureBase.U8(0x66) != startState[1].GetInt32()) Add("state after step 1 differs");
                    int[] expectedStages = start.NextStep == 2 ? phases : [phases[0], phases[^1]];
                    if (!stages.SequenceEqual(expectedStages)) Add("native phase order differs");
                    if (start.NextStep == 2)
                    {
                        Engagement.Powers power = Engagement.Compute(w, a, b);
                        uint[] calculation = [BitConverter.SingleToUInt32Bits(power.PowerA), BitConverter.SingleToUInt32Bits(power.AttackA), BitConverter.SingleToUInt32Bits(power.DefenseA),
                            BitConverter.SingleToUInt32Bits(power.PowerB), BitConverter.SingleToUInt32Bits(power.AttackB), BitConverter.SingleToUInt32Bits(power.DefenseB), unchecked((ushort)power.Damage)];
                        if (!calculation.SequenceEqual(trace.GetProperty("calculation").EnumerateArray().Select(v => v.GetUInt32()))) Add("calculation bits differ");
                        // Original cached troop words remain in the records after earlier encounters mutate the tables.
                        var tactic = EngagementTactic.Apply(w, a, b, b.Kind == 27 ? 20000 : 30000, new(unchecked((ushort)power.Damage), 0, 0));
                        if ((tactic.Flow is not null) != trace.GetProperty("flow_called").GetBoolean()) Add("tactic caller presence differs");
                        if (tactic.Flow is { } flow)
                        {
                            flowCalled++;
                            if (flow.Pending.Count != 0 || flow.Destroyed) throw new InvalidOperationException("Fixture must not enqueue destruction");
                            if (!EngagementTacticSequenceTests.FlowMatches(flow, trace.GetProperty("flow"))) Add("tactic candidates/queue/results/maps differ");
                            if (flow.Commands.Count > 1) linked++;
                        }
                        int[] tacticFrame = [tactic.Frame.BasicDamage, tactic.Frame.ExtraDamage, tactic.Frame.TacticFired];
                        if (!tacticFrame.SequenceEqual(trace.GetProperty("tactic_frame").EnumerateArray().Select(v => v.GetInt32()))) Add("tactic frame differs");
                        if (tactic.Frame.TacticFired != 0) fired++;
                        if (tactic.Frame.ExtraDamage != 0) transferred++;
                        var combined = EngagementTactic.Combine(tactic.Frame);
                        int[] frame = [combined.Frame.BasicDamage, combined.Frame.ExtraDamage, combined.Frame.TacticFired];
                        if (tactic.NextStep != 7 || !frame.SequenceEqual(trace.GetProperty("frame").EnumerateArray().Select(v => v.GetInt32()))) Add("final frame differs");
                        if (b.Kind == 27) EngagementSequenceTests.ApplyUnit(w, a, b, combined.TotalDamage);
                        else
                        {
                            Rec baseRec = Base(w, b);
                            if (a.Kind == 27) Gain(w, w.Table(Tables.Units)[a.Index], 0, combined.TotalDamage);
                            Engagement.BaseAfter after = Engagement.ApplyBase(w, baseRec, combined.TotalDamage, a, minimumDamage: false);
                            if (after.Fell) throw new InvalidOperationException("Fixture must retain base troops");
                            I32(baseRec, 0x3C, after.Troops); I32(baseRec, 0x40, after.Wounded); baseRec.Bytes[0x44] = (byte)after.Morale;
                        }
                    }
                    EngagementSequenceTests.ApplyEnd(w, a, b);
                    void Add(string detail) { if (wrong.Count < 20) wrong.Add($"case {n}/{index}: {detail}"); }
                }
                var expected = c[3].EnumerateArray().ToDictionary(e => (Table: Convert.ToInt32(e[0].GetString()!, 16), Index: e[1].GetInt32()), e => Convert.FromHexString(e[2].GetString()!));
                foreach (int t in tables)
                    foreach (Rec rec in w.Table(t))
                    {
                        byte[] wanted = expected.GetValueOrDefault((t, rec.Index), before[t][rec.Index]);
                        if (!rec.Bytes.SequenceEqual(wanted) && wrong.Count < 20)
                        {
                            int at = Enumerable.Range(0, rec.Bytes.Length).First(i => rec.Bytes[i] != wanted[i]);
                            wrong.Add($"case {n}: table {t:x}[{rec.Index}] +{at:x}: Core {rec.Bytes[at]:x2}, native {wanted[at]:x2}");
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
        Assert.Equal(600, n); Assert.Equal(1800, records); Assert.Equal(1635, admitted);
        JsonElement stats = data.GetProperty("stats");
        foreach (var (name, count) in new[] { ("admitted", admitted), ("siege_calls", siegeCalls), ("flow_called", flowCalled), ("fired", fired), ("linked", linked), ("transferred", transferred) })
            Assert.Equal(stats.GetProperty(name).GetInt32(), count);
        Assert.True(siegeCalls > 1000 && fired > 100 && transferred > 100 && linked > 50);
    }

    private static Rec Base(World w, Engagement.Side s) => w.Table(s.Kind == 5 ? Tables.Cities : Tables.Gates)[s.Index];
    private static Rec Building(World w, Engagement.Side s) => w.Table(Tables.Buildings)[s.Kind is 5 or 6 ? Base(w, s).U16(0x4E) : s.Index];
    private static void Gain(World w, Rec unit, int kind, int amount)
    {
        Rec commander = w.OfficerById(unit.U16(0x12))!;
        var xp = Experience.Gain(w, commander, kind, amount);
        U16(commander, 0x1C0, xp.Merit); I32(commander, 0x104, xp.Exp); commander.Bytes[0xEC] = (byte)xp.Level;
    }
}

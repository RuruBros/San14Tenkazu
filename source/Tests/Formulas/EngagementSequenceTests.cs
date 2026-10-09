using System.Buffers.Binary;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

// Compose real ordinary engagement phases without resetting World between records.
public class EngagementSequenceTests
{
    private static Engagement.Side Side(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32(), e[4].GetInt32());
    private static void U16(Rec r, int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(r.Bytes.AsSpan(at), unchecked((ushort)value));
    private static void I32(Rec r, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(r.Bytes.AsSpan(at), value);

    [Fact]
    public void ConsecutiveOrdinaryEngagementsMatchTheWholeNativeCaller()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        JsonElement data = Vectors.Read("engagement_sequence.json.gz");
        int[] tables = data.GetProperty("tables").EnumerateArray().Select(t => Convert.ToInt32(t.GetString()!, 16)).ToArray();
        int[] phases = data.GetProperty("phases").EnumerateArray().Select(p => p.GetInt32()).ToArray();
        var wrong = new List<string>();
        int n = 0, admitted = 0, rejected = 0;
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
                    JsonElement trace = c[2][index++];
                    bool allowed = Engagement.CanResolve(w, a, b);
                    int[] stages = trace.GetProperty("stages").EnumerateArray().Select(s => s.GetInt32()).ToArray();
                    if (!allowed)
                    {
                        rejected++;
                        if (stages.Length != 0 && wrong.Count < 20) wrong.Add($"case {n}/{index}: Core rejected, native entered {stages.Length} phases");
                        continue;
                    }
                    admitted++;
                    if (!stages.SequenceEqual(phases)) throw new InvalidOperationException($"case {n}/{index}: unexpected native phase order");
                    Engagement.StartResult start = Engagement.Start(w, a, b);
                    if (start.NextStep != 2 || start.Siege is not null) throw new InvalidOperationException("Fixture must remain on the ordinary unit path");
                    Engagement.Powers power = Engagement.Compute(w, a, b);
                    uint[] calculation = [BitConverter.SingleToUInt32Bits(power.PowerA), BitConverter.SingleToUInt32Bits(power.AttackA), BitConverter.SingleToUInt32Bits(power.DefenseA),
                        BitConverter.SingleToUInt32Bits(power.PowerB), BitConverter.SingleToUInt32Bits(power.AttackB), BitConverter.SingleToUInt32Bits(power.DefenseB), unchecked((ushort)power.Damage)];
                    if (!calculation.SequenceEqual(trace.GetProperty("calculation").EnumerateArray().Select(v => v.GetUInt32())) && wrong.Count < 20)
                        wrong.Add($"case {n}/{index}: calculation {string.Join(',', calculation)}, native {trace.GetProperty("calculation")}");
                    var tactic = EngagementTactic.Apply(w, a, b,
                        c[4].EnumerateArray()
                            .Where(p => p[0].GetString() == "0x7e838" && p[1].GetInt32() == b.Index && p[2].GetInt32() == 0x16)
                            .Select(p => BinaryPrimitives.ReadUInt16LittleEndian(Convert.FromHexString(p[3].GetString()!))).Single(),
                        new(unchecked((ushort)power.Damage), 0, 0));
                    if (tactic.NextStep != 7 || tactic.Flow is not null) throw new InvalidOperationException("Native cooldown fixture must not execute tactics");
                    var combined = EngagementTactic.Combine(tactic.Frame);
                    int[] frame = [combined.Frame.BasicDamage, combined.Frame.ExtraDamage, combined.Frame.TacticFired];
                    if (!frame.SequenceEqual(trace.GetProperty("frame").EnumerateArray().Select(v => v.GetInt32())) && wrong.Count < 20)
                        wrong.Add($"case {n}/{index}: frame {string.Join(',', frame)}, native {trace.GetProperty("frame")}");
                    ApplyUnit(w, a, b, combined.TotalDamage);
                    ApplyEnd(w, a, b);
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
        Assert.Equal(600, n);
        Assert.Equal(1620, admitted);
        Assert.Equal(180, rejected);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    internal static void ApplyUnit(World w, Engagement.Side a, Engagement.Side b, int damage)
    {
        Rec defender = w.Table(Tables.Units)[b.Index];
        if (a.Kind == 27)
        {
            Rec attacker = w.Table(Tables.Units)[a.Index];
            Rec commander = w.OfficerById(attacker.U16(0x12))!;
            var xp = Experience.Gain(w, commander, 0, damage);
            U16(commander, 0x1C0, xp.Merit); I32(commander, 0x104, xp.Exp); commander.Bytes[0xEC] = (byte)xp.Level;
        }
        Rec person = w.OfficerById(defender.U16(0x12))!;
        int oldLit = person.U16(0x184);
        Engagement.UnitAfter after = Engagement.ApplyUnit(w, defender, damage, a, minimumDamage: false, routeDefersDestruction: true);
        if (after.Destroyed || after.Routed) throw new InvalidOperationException("Fixture must remain nonlethal and preserve morale");
        U16(defender, 0x16, after.Troops); U16(defender, 0x18, after.Wounded); defender.Bytes[0x1A] = (byte)after.Morale;
        I32(defender, 0x5C, after.Gold); I32(defender, 0x60, after.Food); U16(person, 0x184, after.Lit);
        for (int k = 0; k < 9; k++)
            if ((after.Lit & (1 << k)) != 0 && (oldLit & (1 << k)) == 0)
            {
                Rec trait = w.Entry(Tables.Personalities, Personality.Slot(w, person, k), 355);
                U16(person, 0x186 + 2 * k, Math.Min((int)trait.U16(0xB4), 720));
            }
    }

    internal static void ApplyEnd(World w, Engagement.Side a, Engagement.Side b)
    {
        StepEnd.Result end = StepEnd.Apply(w, new(a.Index, a.Kind, a.Hex, a.Force), new(b.Index, b.Kind, b.Hex, b.Force));
        Rec county = w.Entry(Tables.Counties, w.Entry(Tables.Tiles, b.Hex, 48399).U16(0x12), 500);
        U16(county, 0x3C, end.Agriculture); U16(county, 0x3E, end.Commerce); U16(county, 0x40, end.Barracks);
        U16(county, 0x52, end.Population); county.Bytes[0x54] = (byte)end.Order;
        U16(StepEnd.CountyOwner(w, county), 0x1BE, end.OwnerPanel);
        w.Table(Tables.Forces)[a.Force].Bytes[0x13 + b.Force] = (byte)end.RelationAB;
        w.Table(Tables.Forces)[b.Force].Bytes[0x13 + a.Force] = (byte)end.RelationBA;
    }
}

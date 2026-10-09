using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// Localization buffers, variant draws from the shared RNG, and presentation belong to the host.
// History record writes remain in HistoryLog.
internal sealed record ForceEventPresentation(Action Reset, Action<int, int> BindForce, Action<int, int> BindPair,
    Func<int, string> Message, Action<int> Sound, Action<string, int?> Show);

// 0x277140/0x274980/0x256DF0 with 0x292C00(param1=0). Tribal grade transitions require their own gameplay body.
internal sealed class ForceEvents(World w, ForceCoalition coalition, HistoryLog history,
    ForceEventPresentation presentation, int relationshipGain)
{
    private Rec Central => w.Entry(Tables.Forces, w.WorldState.U8(0x1656), 51);
    private Rec Leader => w.Entry(Tables.Forces, w.WorldState.U8(0x1657), 51);
    private void Record(string primary, string secondary) =>
        history.Add(new(1, 0, 0, 1, w.WorldState.I32(0x450), primary, secondary, 5012));
    private void Single(int force)
    {
        presentation.Reset();
        presentation.BindForce(force, 0);
    }
    private void Pair(int defeated, int central)
    {
        presentation.Reset();
        presentation.BindPair(defeated, central);
    }

    internal void LeaderDefeated(Rec defeated)
    {
        if (defeated.Index != w.WorldState.U8(0x1657)) return;
        Rec central = Central;
        if (Objects.ForceValid(central))
        {
            Single(central.Index);
            string primary = presentation.Message(0x3202);
            Pair(defeated.Index, central.Index);
            string secondary = presentation.Message(0x3203);
            Record(primary, secondary);
            Pair(defeated.Index, central.Index);
            presentation.Sound(8);
            presentation.Show(presentation.Message(0x3204), null);
        }
        coalition.ClearWithoutUnitReset();
    }

    internal void TargetDefeated(Rec defeated, Rec? victor)
    {
        if (defeated.Index != w.WorldState.U8(0x1656)) return;
        if (Objects.Valid(w, victor) && (victor!.U8(0x12) & 2) != 0)
        {
            Victory(victor);
            return;
        }
        Rec central = Central;
        if (Objects.ForceValid(central))
        {
            Single(central.Index);
            string primary = presentation.Message(0x3205);
            Single(central.Index);
            string secondary = presentation.Message(0x3206);
            Record(primary, secondary);
            Single(central.Index);
            presentation.Sound(8);
            presentation.Show(presentation.Message(0x3207), null);
        }
        coalition.ClearWithoutUnitReset();
    }

    internal int Victory(Rec? victor)
    {
        if (!Objects.Valid(w, victor) || !Objects.ForceValid(Central) || !Objects.ForceValid(Leader)) return 0;
        Rec central = Central, leader = Leader;
        presentation.BindForce(central.Index, 0);
        string primary = presentation.Message(0x3213), secondary = presentation.Message(0x3214);
        Record(primary, secondary);
        if ((leader.U8(0x12) & 4) != 0)
        {
            presentation.BindForce(central.Index, 0);
            int message = Objects.IsPlayerForce(w, leader) ? 0x3215 : 0x321A;
            presentation.Show(presentation.Message(message), leader.U16(0x10));
        }
        UpdateVictoryScenario(w);
        Rec[] forces = w.List(Lists.ForceList).Select(n => n[0] ??
            throw new InvalidDataException("The native coalition iterator requires a force payload.")).ToArray();
        for (int i = 0; i < forces.Length; i++)
        {
            Rec first = forces[i];
            if ((first.U8(0x12) & 2) == 0) continue;
            for (int j = i + 1; j < forces.Length; j++)
            {
                Rec second = forces[j];
                if ((second.U8(0x12) & 2) == 0) continue;
                int before = first.U8(0x13 + second.Index);
                var a = Advantage.EffectWithActivation(w, first, 8, 100);
                var b = Advantage.EffectWithActivation(w, second, 8, 100);
                int gain = Math.Clamp(unchecked((a.Active ? a.Value : b.Value) * relationshipGain) / 100 + before, 20, 200);
                ForceRelations.ChangeSilent(w, first, second, gain - before);
            }
        }
        coalition.ClearWithoutUnitReset();
        return 1;
    }

    // 0x274EE0: packed event dates truncate the year to signed16 and replace zero month/day with one.
    internal static void UpdateVictoryScenario(World w)
    {
        if ((w.WorldState.U32(0x169C) & 0x10) != 0) return;
        Rec central = w.Entry(Tables.Forces, w.WorldState.U8(0x1656), 51);
        if (!Objects.ForceValid(central) || central.U16(0x10) != 736) return;
        Rec leader = w.Entry(Tables.Forces, w.WorldState.U8(0x1657), 51);
        if (!Objects.ForceValid(leader) || leader.U16(0x10) != 35) return;
        Rec? person = w.OfficerById(521); // raw+0x11A0 is the ID521 pointer in the raw+0x158 lookup table.
        if (!Objects.Valid(w, person) || person!.U8(0xE6) != 1) return;
        Rec force = Objects.ForceOf(w, person);
        if (!Objects.ForceValid(force) || (force.U8(0x12) & 2) == 0) return;
        uint events = w.WorldState.U32(0x460);
        if ((events & 8) == 0) return;
        uint packed = w.WorldState.U32(0x56C);
        int year = unchecked((short)(packed >> 9)), month = (int)(packed >> 5 & 15), day = (int)(packed & 31);
        int previous = (day == 0 ? 1 : day) - 31 + ((month == 0 ? 1 : month) + year * 12) * 30;
        int current = w.WorldState.I8(0x37) - 31 + (w.WorldState.I8(0x36) + w.WorldState.I16(0x34) * 12) * 30;
        if (current - previous >= 720 || (events & 32) != 0 || !Objects.Valid(w, w.OfficerById(w.WorldState.U16(0x1654)))) return;
        BinaryPrimitives.WriteUInt32LittleEndian(w.WorldState.Bytes.AsSpan(0x169C), w.WorldState.U32(0x169C) | 0x10);
    }
}

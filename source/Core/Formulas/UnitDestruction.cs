using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// 0x258EE0, reason4/mode0 without either attacker: compose the gameplay body before the pending finalizer.
internal sealed class UnitDestruction(World w, UnitPersonalityLifecycle personalities,
    PrisonerRelease prisoners, OfficerLifecycle officers, bool refreshAvailable, Action<Rec> refresh,
    Action<Rec, int> moraleVisual, Action<UnitDestruction.DifficultyLog> difficultyLog,
    Action<DestructionQueue.EngineCall> immediateRoute)
{
    internal readonly record struct DifficultyLog(int Slider, int DescriptionRva, int OldValue, int NewValue);
    [Rva(0x1A4A4A0)] internal const int HomeWoundedPercent = 50;
    [Rva(0x1A4A5D4)] internal const int LoyaltyDivisor = 7;
    [Rva(0x1A4A5E0)] internal const int ReputationDivisor = 5;
    [Rva(0x1A4A5EC)] internal const int LinkOffset = 3;
    [Rva(0x1A4A5F8)] internal const int OfficeNoticeLimit = 8;
    [Rva(0x1A4A61C)] internal const int AllyLossMaximum = 10;
    [Rva(0x1A4A628)] internal const int AllyLossPercent = 100;
    private static void Word(Rec r, int at, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(r.Bytes.AsSpan(at), unchecked((ushort)value));
    private static void Dword(Rec r, int at, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(r.Bytes.AsSpan(at), value);
    private static int Clamp(int value, int maximum) => value < 0 ? 0 : Math.Min(value, maximum);
    private int MoraleMaximum(Rec unit) => BattlePower.MaxMorale(w, w.Entry(Tables.Forces, Objects.UnitForce(w, unit), 51));
    private int Morale(Rec unit) => Math.Min(unit.U8(0x1A), MoraleMaximum(unit));
    private void SetMorale(Rec unit, int value) => unit.Bytes[0x1A] = unchecked((byte)Clamp(value, MoraleMaximum(unit)));

    // A null supplied hex and fifth argument0 discard the searched attacker before immediate route dispatch.
    private void AddMorale(Rec unit, int delta)
    {
        if (!Objects.UnitValid(unit)) return;
        if (delta < 0)
        {
            int sum = Personality.UnitSums(w, unit, false)[13];
            if (sum != 0)
            {
                int absolute = unchecked((delta ^ (delta >> 31)) - (delta >> 31));
                float value = (float)unchecked(absolute * sum * Engagement.MoraleKind13Factor) / 100f;
                if (Objects.Trunc(value) == 0) value = value >= 0f ? 1f : -1f;
                delta = unchecked(delta + Objects.Trunc(value));
            }
        }
        int next = Clamp(unchecked(Morale(unit) + delta), MoraleMaximum(unit));
        SetMorale(unit, next);
        if (next == 0) immediateRoute(new(DestructionQueue.Handler.UnitDestroyed, unit, null, null, Mode: 1, Reason: 0));
    }

    private void RecoverHomeWounded(Rec unit, Rec? home)
    {
        bool preset = w.Settings.I32(0xE58) != 0;
        if (!(preset && w.Settings.I32(0xCC) == 0 || !preset && BattlePower.Slider(w, 7) > 0) || !Objects.Valid(w, home)) return;
        int amount = unchecked(unit.U16(0x18) * HomeWoundedPercent) / 100;
        if (!preset)
        {
            int original = amount;
            amount = Math.Min(unchecked(BattlePower.Slider(w, 7) * amount) / 100, unit.U16(0x16) + unit.U16(0x18));
            difficultyLog(new(7, 0x13ACCB0, original, amount));
        }
        int maximum = Engagement.MaxTroops(w, home!);
        int value = Clamp(unchecked(home!.I32(0x40) + amount), maximum);
        Dword(home, 0x40, Clamp(value, maximum));
    }

    private void LowerLoyalty(Rec commander, Rec force)
    {
        if (force.Index == 51 || (uint)(force.Index - 46) < 5 || !Objects.PersonValid(commander)) return;
        Rec? ruler = w.OfficerById(force.U16(0x10));
        if (!Objects.Valid(w, ruler)) return;
        float value = ((float)commander.I16(0x16C) - 20f) * -1f / ReputationDivisor +
            (100f - (float)commander.U8(0xE8)) / LoyaltyDivisor + (float)(LinkOffset - Link.Grade(w, commander, ruler, false));
        int loss = Math.Max(0, Objects.Trunc(value));
        int loyalty = Clamp(commander.U8(0xE8) - unchecked((byte)loss), 255);
        commander.Bytes[0xE8] = (byte)((commander.U16(0x15E) & 0x2000) != 0 ? 255 : loyalty);
    }

    private void LeaderMorale(Rec unit, Rec commander, Rec force, bool special)
    {
        if (special) return;
        Rec? office = Objects.PersonValid(commander) ? w.Table(Tables.Offices)[commander.U8(0xE7)] : null;
        bool ruler = commander.U16(0x10) == (force.Index != 0 ? force.U16(0x10) : 5014);
        if (!(Objects.Valid(w, office) && office!.U8(0x37) <= OfficeNoticeLimit || ruler)) return;
        int loss;
        if (ruler) loss = (10 - w.Entry(Tables.Ranks, force.U8(0x47), 12).U8(0xAC)) * 2;
        else
        {
            byte[] values = office!.Vector40 ?? [];
            int ability = values.Length == 20 ? Objects.Decode(values[4], 5, office.Index) : office.U8(0x38);
            loss = unchecked(ability + 10 - office.U8(0x37));
        }
        foreach (Rec nearby in Map.UnitsNear(w, unit, 3))
            if (Objects.UnitForce(w, nearby) == force.Index && Morale(nearby) > 1)
            {
                int amount = Math.Min(unchecked(loss * AllyLossPercent) / 100, AllyLossMaximum);
                amount = Math.Min(amount, Morale(nearby) - 1);
                AddMorale(nearby, -amount);
                moraleVisual(nearby, 0x42);
            }
    }

    private void NearbyPersonalityMorale(Rec unit, Rec force)
    {
        foreach (Rec nearby in Map.UnitsNear(w, unit, Personality.DefaultRange))
        {
            Rec? commander = w.OfficerById(nearby.U16(0x12));
            if (!Objects.Valid(w, commander) || !Objects.ForceValid(force) || Objects.UnitForce(w, nearby) != force.Index) continue;
            bool positiveRefresh = false, negativeRefresh = false;
            var seen = new HashSet<Rec>();
            for (int slot = 0; slot < 9; slot++)
            {
                Rec p = w.Entry(Tables.Personalities, Personality.Slot(w, commander!, slot), 355);
                if (!Personality.Valid(p) || p.U8(0xA4) != 0x27 || !seen.Add(p)) continue;
                foreach (int at in new[] { 0xB8, 0xBC })
                {
                    int value = Math.Clamp((int)p.I16(at), -100, 2000);
                    if (value > 0)
                    {
                        int cap = MoraleMaximum(nearby);
                        SetMorale(nearby, Math.Min(cap, unchecked(Morale(nearby) + value)));
                        if (!positiveRefresh && refreshAvailable) refresh(nearby);
                        positiveRefresh = true;
                    }
                    else if (value < 0 && Morale(nearby) > 0)
                    {
                        AddMorale(nearby, value);
                        if (!negativeRefresh && refreshAvailable) refresh(nearby);
                        negativeRefresh = true;
                    }
                }
            }
        }
    }

    internal void ApplyUnopposedRemoval(Rec? target)
    {
        if (!Objects.Valid(w, target) || w.List(Lists.EngagedUnits).Any(n => ReferenceEquals(n[0], target))) return;
        Rec unit = target!;
        Rec commander = w.OfficerById(unit.U16(0x12)) ??
            throw new InvalidOperationException("The native destruction body dereferences a null commander.");
        Rec force = w.Entry(Tables.Forces, Objects.UnitForce(w, unit), 51);
        if (Objects.IsPlayerForce(w, force)) throw new NotSupportedException("Player return/notification composition is a separate body.");
        Rec[] held = PrisonerRelease.HeldBy(w, unit).ToArray();
        if (held.Any(p => Objects.IsPlayerForce(w, Objects.ForceOf(w, p))))
            throw new NotSupportedException("Player prisoner release requires its notification host requests.");
        bool special = force.Index == 51 || (uint)(force.Index - 46) < 5 && Objects.TribeOutside(w, commander);
        if (special && BitConverter.ToUInt64(commander.Bytes, 0x1B0) > 7)
            throw new NotSupportedException("The heap-backed commander string requires shrink/free support.");
        Rec? home = Map.Base(w, Objects.PersonValid(commander) ? commander.U16(0xE2) : 0);
        int hex = unit.U16(0x2A);
        unit.Bytes[0x150] |= 2;
        for (int slot = 0; slot < 9; slot++) personalities.Deactivate(unit, slot);
        for (int slot = 0; slot < 36; slot++) personalities.DropReceived(unit, slot, false);
        RecoverHomeWounded(unit, home);
        LowerLoyalty(commander, force);
        LeaderMorale(unit, commander, force, special);
        if (!special && commander.U8(0xE6) is >= 1 and <= 4) prisoners.StartReturn(commander);
        foreach (Rec prisoner in PrisonerRelease.HeldBy(w, unit).ToArray()) prisoners.Apply(prisoner);
        foreach (Rec registered in w.List(Lists.UnitList).Select(n => n[0]).OfType<Rec>())
            if (registered.U16(0x3E) == unit.Index && registered.U8(0x25) != 0)
            {
                registered.Bytes[0x25] = TacticEffects.AilmentMaximum < 0 ? unchecked((byte)TacticEffects.AilmentMaximum) : (byte)0;
                registered.Bytes[0x3C] = 255;
                Word(registered, 0x3E, 0);
            }
        NearbyPersonalityMorale(unit, force);
        Word(w.Entry(Tables.Tiles, hex, 0xBD0F), 0x1C, 0);
        if (!w.List(Lists.EngagedUnits).Any(n => ReferenceEquals(n[0], unit))) w.List(Lists.EngagedUnits).Add([unit]);
        if (special)
        {
            w.List(Lists.OfficerList).RemoveAll(n => ReferenceEquals(n[0], commander));
            officers.ResetSlot(commander);
        }
        foreach (Rec registered in w.List(Lists.UnitList).Select(n => n[0]).OfType<Rec>())
            if (Objects.UnitValid(registered) && registered.U8(0x37) == 7 && registered.U8(0x38) == 0x1B && registered.U16(0x3A) == unit.Index)
            {
                registered.Bytes[0x38] = 0x1B;
                Word(registered, 0x3A, 0);
            }
    }
}

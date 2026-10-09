namespace San14Tenkazu.Formulas;

// The eligibility gates and deferred-damage transfer around TacticFlow (0x16FDD0, battle step 6).
internal static class EngagementTactic
{
    internal readonly record struct Frame(ushort BasicDamage, ushort ExtraDamage, int TacticFired);
    internal readonly record struct Result(int NextStep, Frame Frame, TacticFlow.Resolution? Flow);

    // 0x17D7D0: field armies with a tactic, an empty cooldown, and no incompatible confusion/provocation.
    internal static bool UnitReady(World w, Rec unit, Engagement.Side target) => Objects.Valid(w, unit)
        && unit.U8(0x10) == 1 && UnitTactics.Candidates(w, unit).Count != 0 && unit.U8(0x20) == 0 && unit.U8(0x23) == 0
        && (unit.U8(0x25) == 0 || unit.U16(0x3E) == target.Index && unit.U8(0x3C) == target.Kind);

    // 0x17D960: the governor must be in this base, with at most a light injury and a compatible target.
    internal static bool BaseReady(World w, Rec? baseRec, Engagement.Side target)
    {
        if (baseRec is null || !Objects.Valid(w, baseRec) || baseRec.U8(0x48) == 0) return false;
        Rec governor = w.OfficerById(baseRec.U16(0x2E)) ?? w.Table(Tables.Officers)[0];
        if (!Objects.Valid(w, governor)) return false;
        var place = Objects.Place(governor);
        int id = baseRec.U16(0x4E);
        if (id < 52 && (place.Kind != 5 || place.Number != id)) return false;
        if ((uint)(id - 52) < 10 && (place.Kind != 6 || place.Number != id - 51)) return false;
        return governor.U8(0x161) <= 1 && baseRec.I16(0x4C) == 0 && baseRec.U8(0x49) == 0
            && (baseRec.U8(0x4B) == 0 || baseRec.U8(0x62) == target.Kind && baseRec.U16(0x64) == target.Index);
    }

    // 0x17D650 adds command-mode/target and the player's recorded-deployment restriction to readiness.
    internal static bool Allowed(World w, Engagement.Side source, Engagement.Side target)
    {
        if (source.Kind == 27)
        {
            Rec unit = w.Entry(Tables.Units, source.Index, 500);
            if (!UnitReady(w, unit, target)) return false;
            int kind = unit.U8(0x3C), number = unit.I16(0x3E);
            if (kind == 255) { kind = unit.U8(0x38); number = unit.I16(0x3A); }
            if (!(kind == target.Kind && number == (short)target.Index)
                && !(unit.U8(0x25) == 0 && unit.U8(0x37) is not (4 or 5 or 7))) return false;
            int player = w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 51).Index;
            if (player != Objects.UnitForce(w, unit) || !TacticLink.RecordedDelayReached(w, unit)) return true;
            Rec commander = w.OfficerById(unit.U16(0x12)) ?? w.Table(Tables.Officers)[0];
            return (commander.U8(0x15E) & 0x20) != 0;
        }
        if (source.Kind is 5 or 6)
        {
            int id = source.Kind == 5 ? source.Index <= 51 ? source.Index : 0
                : (uint)(source.Index - 1) < 10 ? source.Index + 51 : 0;
            Rec? baseRec = Map.Base(w, id);
            return baseRec is not null && BaseReady(w, baseRec, target) && (baseRec.U8(0x4B) == 0
                || baseRec.U8(0x62) == target.Kind && baseRec.I16(0x64) == (short)target.Index);
        }
        return false;
    }

    internal static Result Apply(World w, Engagement.Side? source, Engagement.Side target, int recordedTargetTroops, Frame frame)
    {
        if (source is null) return new(8, frame, null);
        if (w.Flag("challenge_active") && (w.Value("challenge_flags") & 1) != 0
            || frame.BasicDamage >= (ushort)recordedTargetTroops || !Allowed(w, source.Value, target)) return new(7, frame, null);
        TacticFlow.Resolution flow = TacticFlow.Apply(w, source.Value, target);
        if (flow.Main is null) return new(7, frame, flow);
        return new(7, Transfer(w, target, frame, flow.UnitDamage, flow.BaseDamage), flow);
    }

    internal static Frame Transfer(World w, Engagement.Side target, Frame frame,
        IReadOnlyDictionary<Rec, int> unitDamage, IReadOnlyDictionary<Rec, int> baseDamage)
    {
        ushort extra = frame.ExtraDamage;
        foreach (var entry in unitDamage)
            if (Objects.Valid(w, entry.Key) && target.Kind == 27 && entry.Key.Index == target.Index) extra = unchecked((ushort)entry.Value);
        var bases = baseDamage.Where(entry => Objects.Valid(w, entry.Key) && target.Kind is 5 or 6
            && BaseNumber(entry.Key) == target.Index).ToArray();
        if (bases.Length > 1 && bases.Any(entry => entry.Key.NativeAddress == 0))
            throw new InvalidOperationException("A native table address is required to order colliding base damage-map entries.");
        // The native loop compares only the converted number, without checking city versus gate kind.
        foreach (var entry in bases.OrderBy(entry => entry.Key.NativeAddress)) extra = unchecked((ushort)entry.Value);
        return frame with { ExtraDamage = extra, TacticFired = 1 };
    }

    private static int BaseNumber(Rec baseRec)
    {
        int id = baseRec.U16(0x4E);
        return id <= 51 ? id : (uint)(id - 52) < 10 ? id - 51 : 0;
    }

    // 0x1749A0 adds zero-extended words, applying the minimum to basic damage only when their sum is zero.
    internal static (Frame Frame, int TotalDamage) Combine(Frame frame)
    {
        if (frame.BasicDamage + frame.ExtraDamage == 0) frame = frame with { BasicDamage = 1 };
        return (frame, frame.BasicDamage + frame.ExtraDamage);
    }
}

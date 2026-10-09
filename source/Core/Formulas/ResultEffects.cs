namespace San14Tenkazu.Formulas;

// 0x3789E0's notification prefix and 0x3790C0's semantic output requests.
// Localization, history storage, queued-event execution and the caller's combat suffix are external boundaries.
internal static class ResultEffects
{
    [Rva(0x1A493A0)] internal const int MoraleThreshold = 50;

    internal readonly record struct Notice(Rec First, Rec Second, int PrimaryMessage, int? SecondaryMessage,
        int Kind, int Mode, int Corps, int Hex, int Tick, int Force, int ForceName, bool RelationEvent);

    internal static Notice? FromCaller(World w, Rec? first, Rec? second, int reason, int selector,
        Func<Rec, int>? commandHex = null)
    {
        if (!Objects.Valid(w, first) || !Objects.Valid(w, second) || selector is not (0 or 1)) return null;
        Rec a = selector == 0 ? first! : second!, b = selector == 0 ? second! : first!;
        Rec ua = Objects.UnitOf(w, a), ub = Objects.UnitOf(w, b);
        if (!Objects.UnitValid(ua) || !Objects.UnitValid(ub)) return null;

        bool marked = ((selector == 0 ? b : a).U16(0x15E) & 0x4000) != 0;
        bool above = false;
        if (reason is not (1 or 2))
        {
            Rec moraleUnit = selector == 0 ? ub : ua;
            Rec moraleForce = w.Entry(Tables.Forces, Objects.UnitForce(w, moraleUnit), 0x33);
            above = unchecked(Math.Min(moraleUnit.U8(0x1A), BattlePower.MaxMorale(w, moraleForce)) - MoraleThreshold) > 0;
        }
        int primary, kind = 5, mode = 0;
        int? secondary;
        if (selector == 0)
        {
            primary = reason == 2 ? (marked ? 0xC954 : 0xC953)
                : reason == 1 || !above ? 0xC954 : 0xC952;
            secondary = reason == 2 ? (marked ? 0x53A4 : 0x53A3)
                : reason == 1 || !above ? 0x53A4 : null;
            if (reason is 1 or 2 || !above) kind = mode = 1;
        }
        else
        {
            primary = reason == 2 ? (marked ? 0xC957 : 0xC956)
                : reason == 1 ? 0xC958 : above ? 0xC955 : 0xC957;
            secondary = reason == 2 ? (marked ? null : 0x53A5) : reason == 1 ? 0x53A6 : null;
            if (reason is 1 or 2) kind = mode = 1;
        }

        Rec force = Objects.ForceOf(w, a);
        Rec ruler = Objects.ForceValid(force) ? w.OfficerById(force.U16(0x10)) ?? w.Table(Tables.Officers)[0]
            : w.OfficerById(0) ?? w.Table(Tables.Officers)[0];
        bool relation = selector == 1 && reason == 1 && RelatedToRuler(w, ruler, a);
        bool commanded = w.CmdActions.Any(c => c.Officers.Contains(a.U16(0x10)));
        int hex = commanded
            ? commandHex is not null ? commandHex(a) : throw new NotSupportedException("A dynamic command needs its native location resolver.")
            : Capture.OfficerHex(w, a);
        if (hex < 0) throw new NotSupportedException("The notification location is outside the supported officer placement paths.");
        return new(a, b, primary, secondary, kind, mode, a.U8(0xE0), hex, w.WorldState.I32(0x450),
            force.Index, force.Index == 0 ? 0x1396 : force.U16(0x10), relation);
    }

    // 0x2C9BD0 / 0x2C8AC0 / 0x2767E0 / 0x232EE0, in their native short-circuit order.
    private static bool RelatedToRuler(World w, Rec ruler, Rec person) =>
        OfficerRelations.Sworn(w, ruler, person) || OfficerRelations.Spouse(w, ruler, person) ||
        OfficerRelations.Related(w, ruler, person) || OfficerRelations.Likes(w, ruler, person);
}

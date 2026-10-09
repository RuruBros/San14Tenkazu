namespace San14Tenkazu.Formulas;

internal static partial class TacticEffects
{
    // Route requests are returned to the caller, which owns the pending-engagement queue and destruction flow.
    internal readonly record struct MoraleApplication(List<Applied> Results, List<Rec> Routed);

    // 0x177900 / 0x270550 / 0x241690: kind-13 mitigation applies only to unit morale losses.
    internal static MoraleApplication ApplyMorale(World w, Rec tactic, Tactic.Side source, int slot, int links, IReadOnlyList<Rec> targets)
    {
        var results = new List<Applied>();
        var routed = new List<Rec>();
        float linked = (float)unchecked(links * LinkPercent) / 100f + 1f;
        float supply = SupplyFactor(w, source);
        foreach (Rec tile in targets)
        {
            var at = Reach.ObjectAt(w, tile);
            Rec? unit = at.Kind == 27 ? w.Entry(Tables.Units, at.Number, 500) : null;
            // A successful route request hides that unit from subsequent ObjectAt calls in this effect.
            if (unit is not null && routed.Contains(unit)) continue;
            Rec? b = at.Kind is 5 or 6 ? Map.Base(w, at.Kind == 5 ? at.Number < 52 ? at.Number : 0 : (uint)(at.Number - 1) < 10 ? at.Number + 51 : 0) : null;
            if (!Objects.Valid(w, unit) && !Objects.Valid(w, b)) continue;
            float factor = AttackFactor(w, tactic, source, new Tactic.Side(unit, b));
            int loss = Objects.Trunc((float)Amount(tactic, slot) * factor * linked * supply);
            Rec target = unit ?? b!;
            int force = unit is not null ? Objects.UnitForce(w, unit) : Map.BaseForce(w, b!);
            int cap = BattlePower.MaxMorale(w, w.Entry(Tables.Forces, force, 51));
            int off = unit is not null ? 0x1A : 0x44;
            int current = unit is not null ? Math.Min((int)target.U8(off), cap) : target.U8(off);
            int delta = unchecked(-loss);
            if (unit is not null && delta < 0)
            {
                int sum = Personality.UnitSums(w, unit, false)[13];
                if (sum != 0)
                {
                    int absolute = delta < 0 ? unchecked(-delta) : delta;
                    float mitigation = (float)unchecked(absolute * sum * Engagement.MoraleKind13Factor) / 100f;
                    if (Objects.Trunc(mitigation) == 0) mitigation = mitigation >= 0f ? 1f : -1f;
                    delta = unchecked(delta + Objects.Trunc(mitigation));
                }
            }
            if (unit is not null || (uint)(force - 46) > 4)
            {
                int next = unchecked(current + delta);
                int after = next < 0 ? 0 : Math.Min(next, cap);
                target.Bytes[off] = (byte)after;
                if (unit is not null && after == 0) routed.Add(unit);
            }
            results.Add(new Applied(at.Kind, at.Number, tile.Index, 0));
        }
        return new MoraleApplication(results, routed);
    }
}

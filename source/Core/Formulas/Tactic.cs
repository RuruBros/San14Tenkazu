namespace San14Tenkazu.Formulas;

// Whether a side's 전법 fires in an engagement (rva 0x16B880, the game's debug lines "戦法 確率" and "戦法種値"): the
// first of the side's tactics whose roll succeeds. A tactic's chance (%) is
//   formation's value for it (+0x88 + 2 × tactic kind; a city or gate 100) × difficulty / 100 × terrain's value
//   (+0x2E + 2 × kind) / 100 × the 군 facilities' bonus (units of forces 46..51 and special officers: 1)
// at most 100, rolled with the seeded random from the two hexes (attacker hex × (5 + its place in the list),
// defender hex × 2).
internal static class Tactic
{
    [Rva(0x1A48A30)] internal const int CityValue = 100;
    [Rva(0x1A48A3C)] internal const int GateValue = 100;
    [Rva(0x1A489EC)] internal const int Difficulty = 4;
    [Rva(0x1A48A04)] internal const int PlayerPreset0 = 16;
    [Rva(0x1A48A10)] internal const int PlayerPreset1 = 8;
    [Rva(0x1A48A1C)] internal const int PlayerPresetOther = 4;
    [Rva(0x1A48A9C)] internal const int OthersPreset4Plus = 16;

    // The side: either a unit or a base (city or gate); the hex it stands on and its force.
    internal readonly record struct Side(Rec? Unit, Rec? Base);

    // 0x16B880: the first tactic of `tactics` (CTacticsData records, in order) that fires for the attacker against
    // the defender, or null. Challenge flag 2 replaces the roll with the native special-tactic predicate.
    internal static Rec? Fire(World w, Side attacker, Side defender, IReadOnlyList<Rec> tactics)
    {
        Rec hexA; Rec force;
        if (attacker.Unit is { } ua && Objects.Valid(w, ua))
        {
            hexA = w.Entry(Tables.Tiles, ua.U16(0x2A), 0xBD0F);
            force = w.Entry(Tables.Forces, Objects.UnitForce(w, ua), 0x33);
        }
        else if (attacker.Base is { } ba && Objects.Valid(w, ba))
        {
            hexA = Map.BuildingTile(w, ba);
            force = w.Entry(Tables.Forces, Map.BaseForce(w, ba), 0x33);
        }
        else return null;

        Rec hexB;
        if (defender.Unit is { } ud && Objects.Valid(w, ud)) hexB = w.Entry(Tables.Tiles, ud.U16(0x2A), 0xBD0F);
        else if (defender.Base is { } bd && Objects.Valid(w, bd)) hexB = Map.BuildingTile(w, bd);
        else return null;

        Rec terrain = w.Entry(Tables.Terrains, hexA.U8(0x10), 0x1E);
        int k = 5;
        foreach (Rec tactic in tactics)
        {
            int kind = tactic.U8(0x40);
            int slot = Math.Min(kind, 8);
            int value;
            if (attacker.Unit is { } u && Objects.Valid(w, u))
            {
                Rec formation = Objects.FormationOf(w, u);
                if (!Objects.FormationValid(formation)) return null;
                value = formation.U16(0x88 + slot * 2);
            }
            else
            {
                int id = attacker.Base!.U16(0x4E);
                value = id <= 0x33 ? CityValue : (uint)(id - 0x34) < 10 ? GateValue : 0;
            }
            int terrainValue = terrain.U16(0x2E + slot * 2);

            float facility;
            if (attacker.Unit is { } uu && Objects.Valid(w, uu) && w.OfficerById(uu.U16(0x12)) is { } cmd && Objects.PersonValid(cmd)
                && (Objects.ForceOf(w, cmd).Index == 0x33 || Objects.InForces46To50(w, cmd) || Objects.SpecialOfficer(w, cmd, false)))
                facility = 1f;
            else
                facility = BattlePower.Facility(w, hexA, force.Index, out _);

            int preset = w.Settings.I32(0xE58) == 0 ? w.Settings.I32(0x134) : w.Settings.I32(0x28);
            int diff = Difficulty;
            // the game compares the force objects themselves (0x32E910), so force 0 counts when the world's player is 0
            bool player = force.Index == w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 0x33).Index;
            if (player) diff = preset == 0 ? PlayerPreset0 : preset == 1 ? PlayerPreset1 : PlayerPresetOther;
            else if (preset > 3) diff = OthersPreset4Plus;

            float chance = (float)value * diff;
            chance /= 100f;
            chance *= terrainValue;
            chance /= 100f;
            chance *= facility;
            float capped = chance > 100f ? 100f : chance;
            bool fired = false;
            if (capped > 0f)
            {
                int roll = Wounded.Seeded(w, 10000, 1, hexA.Index * k, hexB.Index * 2, 0, 0);
                fired = roll < Objects.Trunc(capped * 10000f / 100f);
            }
            if (w.Flag("challenge_active") && (w.Value("challenge_flags") & 2) != 0) fired = Special(tactic);
            if (fired) return tactic;
            k++;
        }
        return null;
    }

    // 0x2336F0: named and event tactics, with the native gaps between numbered groups.
    internal static bool Special(Rec tactic) => (uint)(tactic.Index - 1) < 200
        && ((uint)(tactic.Index - 31) < 100 || tactic.Index > 139);
}

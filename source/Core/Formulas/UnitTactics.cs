namespace San14Tenkazu.Formulas;

internal static class UnitTactics
{
    // 0x22A320: most officers need flag 1 on the tactic or a valid matching reference at raw +0x79118.
    internal static int OfficerSlot(World w, Rec person, int slot)
    {
        int id = person.U16(0x10), number = person.U8(0x130 + slot);
        if ((uint)(id - 5101) < 100 || (uint)(id - 4101) < 100) return number;
        Rec tactic = w.Entry(Tables.Tactics, number, 200);
        if (Objects.Valid(w, tactic) && !TacticEffects.Flag(tactic, 1))
        {
            bool match = w.Table(Tables.TacticReferences).Any(r => r.Index != 0 && r.U8(0x53) != 0 && r.U8(0x52) == tactic.Index);
            if (!match) return 0;
        }
        return number;
    }

    // 0x2ADD40 / 0x231360: land uses the unit's three assigned tactics. Water uses its commander's ten slots,
    // filtered by the water formation mask, retaining native order and duplicates, with a maximum of three.
    internal static List<Rec> Candidates(World w, Rec unit)
    {
        var result = new List<Rec>();
        if (!Objects.Valid(w, unit)) return result;
        Rec tile = w.Entry(Tables.Tiles, unit.U16(0x2A), 48399);
        Rec terrain = w.Entry(Tables.Terrains, tile.U8(0x10), 30);
        if (!Objects.Valid(w, terrain)) return result;
        if ((terrain.U8(0x2C) & 2) == 0)
        {
            for (int k = 0; k < 3; k++)
            {
                Rec tactic = w.Entry(Tables.Tactics, unit.U8(0x1D + k), 200);
                if (Objects.Valid(w, tactic)) result.Add(tactic);
            }
            return result;
        }
        Rec? commander = w.OfficerById(unit.U16(0x12));
        if (!Objects.Valid(w, commander)) return result;
        int formation = unit.U8(0x1C);
        for (int k = 0; k < 10; k++)
        {
            Rec tactic = w.Entry(Tables.Tactics, OfficerSlot(w, commander!, k), 200);
            if (Objects.Valid(w, tactic) && formation <= 20 && ((tactic.U32(0x6C) >> formation) & 1) != 0)
            {
                result.Add(tactic);
                if (result.Count == 3) break;
            }
        }
        return result;
    }
}

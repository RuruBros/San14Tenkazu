namespace San14Tenkazu.Formulas;

// 0x292C00(param1=0), as invoked by force-fall events. Unit route recalculation for param1!=0 is separate.
internal sealed class ForceCoalition(World w, TileOwnership ownership, TileTransfer transfer, int cooldownDays,
    bool refreshAvailable, Action refresh)
{
    private IEnumerable<Rec> Units(Rec force)
    {
        if (force.Index is < 1 or > 51) yield break;
        foreach (Rec?[] node in w.List(Lists.UnitList))
        {
            Rec unit = node[0] ?? throw new InvalidDataException("Native force unit iterators require a payload.");
            if (Objects.UnitForce(w, unit) == force.Index && !Map.Engaged(w, unit)) yield return unit;
        }
    }

    internal void ClearWithoutUnitReset()
    {
        w.WorldState.Bytes[0x1656] = 0;
        w.WorldState.Bytes[0x1657] = 0;
        w.WorldState.Bytes[0x165C] = 0;
        List<Rec?[]> forces = w.List(Lists.ForceList);
        foreach (Rec?[] node in forces)
        {
            Rec force = node[0] ?? throw new InvalidDataException("Native force lists require a payload.");
            foreach (Rec unit in Units(force))
            {
                if ((force.U8(0x12) & 2) == 0) continue;
                Rec tile = w.Entry(Tables.Tiles, unit.U16(0x2A), 48399);
                int owner = tile.U8(0x14);
                if (owner == force.Index || (force.U8(0xEA + owner) & 0x7F) != 0) continue;
                ownership.Apply(tile, 0);
                Rec? commander = w.OfficerById(unit.U16(0x12));
                Rec? home = Map.Base(w, commander is not null && Objects.PersonValid(commander) ? commander.U16(0xE2) : 0);
                if (home is null) throw new InvalidDataException("The native unit home-base lookup requires a payload.");
                Rec city = OfficerCapture.ParentCity(w, home);
                transfer.Apply(tile, force.Index, city.Index, 1, 0, 0);
            }
        }
        foreach (Rec?[] node in forces) node[0]!.Bytes[0x12] &= 0xF9;
        Span<byte> date = stackalloc byte[5];
        NativeCalendar.CopyFive(w.WorldState.Bytes.AsSpan(0x34), date);
        NativeCalendar.AddHours(date, unchecked(cooldownDays * 24));
        NativeCalendar.CopyFive(date, w.WorldState.Bytes.AsSpan(0x16A0));
        if (refreshAvailable) refresh();
    }
}

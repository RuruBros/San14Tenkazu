using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// 0x185B30: gameplay tile transfer with explicit presentation requests and copied global dirty storage.
internal sealed class TileTransfer(World w, TileOwnership ownership, byte[] countyChanges, Action markMapDirty,
    Action<Rec, int, int> notifyUnit, Action<int, Rec> announceCounty, Action<Rec, bool, int> showEffect,
    Rec? challengeCounter = null)
{
    // 0x2C6700 with occupancy, traversal and boat checks disabled by this caller.
    internal static bool Allowed(World w, Rec tile, int destination)
    {
        if (w.Entry(Tables.Terrains, tile.U8(0x10), 30).U16(0x2A) == 0) return false;
        Rec building = Reach.Building(w, tile);
        if (Reach.IsGate(building))
            return building.U16(0x10) == tile.Index && !Reach.Hostile(w, destination, tile.U8(0x14));
        Rec definition = w.Entry(Tables.ObjectKinds, building.U8(0x12), 30);
        return definition.Index == 0 || definition.U8(0x2C) == 4;
    }

    // 0x2409A0: the city byte clamps independently from the original argument used by free officers.
    internal static void SetCountyCity(World w, Rec county, int city)
    {
        county.Bytes[0x35] = (byte)Math.Clamp(city, 0, 51);
        foreach (Rec?[] node in w.List(Lists.OfficerList))
        {
            Rec person = node[0] ?? throw new InvalidDataException("Native officer lists require a payload.");
            int place = person.U16(0xE4);
            if ((uint)(place - 562) >= 500 || place - 561 != county.Index || person.U8(0xE6) != 5) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(person.Bytes.AsSpan(0xE2), (ushort)(city is >= 0 and <= 51 ? city : 0));
        }
    }

    private void NotifyAt(Rec city, Rec tile, int kind, int number)
    {
        if (!Map.BaseValid(w, city)) return;
        foreach (Rec?[] node in w.List(Lists.UnitList))
        {
            Rec unit = node[0] ?? throw new InvalidDataException("Native unit lists require a payload.");
            Rec? commander = w.OfficerById(unit.U16(0x12));
            int home = commander is not null && Objects.PersonValid(commander) ? commander.U16(0xE2) : 0;
            if (city.U16(0x4E) is >= 1 and <= 61 && !Map.Engaged(w, unit) && home == city.U16(0x4E) &&
                Objects.UnitValid(unit) && unit.U16(0x2A) == tile.Index)
                notifyUnit(unit, kind, number);
        }
    }

    internal int Apply(Rec tile, int destination, int city, int visuals, int announce, int overrideFortress)
    {
        if (!Allowed(w, tile, destination))
        {
            Rec building = Reach.Building(w, tile);
            if (overrideFortress == 0 || !Map.BuildingValid(building) ||
                w.Entry(Tables.ObjectKinds, building.U8(0x12), 30).U8(0x2C) != 3) return 0;
        }
        if (tile.U8(0x14) == unchecked((uint)destination)) return 0;
        int word = tile.U16(0x12) >> 5;
        if (word * 4 + 4 > countyChanges.Length)
            throw new InvalidDataException("The copied county dirty window does not contain this tile's word.");
        if (w.Flag("challenge_active") && challengeCounter is null)
            throw new NotSupportedException("Challenge transfer requires its copied counter.");
        bool normal = (uint)(destination - 1) < 51 && (uint)(city - 1) < 51 && (uint)(destination - 46) > 4;
        bool acquiredCounty = false;
        Rec county = w.Entry(Tables.Counties, tile.U16(0x12), 500);
        if (tile.U8(0x10) is >= 15 and <= 18)
        {
            if (normal)
            {
                Rec previousCity = w.Entry(Tables.Cities, county.U8(0x35), 51);
                if (Map.BaseForce(w, previousCity) != destination) SetCountyCity(w, county, city);
                NotifyAt(w.Entry(Tables.Cities, city, 51), tile, 30, county.Index);
                int option = w.Settings.I32(w.Settings.I32(0xE58) == 0 ? 0x23C : 0xC0);
                if (option == 0 && announce != 0) announceCounty(destination, county);
                acquiredCounty = true;
            }
            else if (destination == 51 || (uint)(destination - 46) < 5)
                SetCountyCity(w, county, county.U8(0x34));
        }
        Rec scenery = w.Entry(Tables.Scenery, tile.U8(0x1E), 100);
        if (TileOwnership.SceneryValid(w, scenery) && (scenery.U8(0x5F) & 1) != 0 && normal)
            NotifyAt(w.Entry(Tables.Cities, city, 51), tile, 31, scenery.Index);
        int owner = destination;
        bool retain = false;
        if (destination == 51) owner = 0;
        else if ((uint)(destination - 46) < 5)
        {
            Rec parent = w.Entry(Tables.Cities, county.U8(0x35), 51);
            if (Map.BaseForce(w, parent) != destination)
            {
                Rec tribe = w.Entry(Tables.Forces, destination, 51);
                retain = Objects.ForceValid(tribe) && tribe.U8(0x194) == tile.U8(0x14);
                owner = 0;
            }
        }
        if (!retain) ownership.Apply(tile, owner);
        if ((visuals & 1) != 0 && Objects.ForceValid(w.Entry(Tables.Forces, tile.U8(0x14), 51)))
            showEffect(tile, acquiredCounty, destination);
        if (w.Flag("challenge_active"))
            BinaryPrimitives.WriteInt32LittleEndian(challengeCounter!.Bytes, unchecked(challengeCounter.I32(0) + 1));
        markMapDirty();
        Span<byte> dirty = countyChanges.AsSpan(word * 4, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(dirty, BinaryPrimitives.ReadUInt32LittleEndian(dirty) | 1u << (tile.U16(0x12) & 31));
        return 1;
    }
}

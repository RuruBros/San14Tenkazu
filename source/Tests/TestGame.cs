using System.Buffers.Binary;
using System.Text;

namespace San14Tenkazu.Tests;

// A small game made in memory for the rules' tests: forces with their corps, cities, officers, 정책 slots and units,
// as records holding only the fields the rules read. Each record gets an address of its own (made up).
internal sealed class TestGame
{
    // The record types' vtables in this made-up game (each record starts with its own).
    internal static readonly GameClasses Classes = new(0x7001, 0x7002, 0x7003, 0x7004, 0x7005, 0x7006, 0x7007, 0x7008, 0x7009,
        0x700A, 0x700B, 0x700C, 0x700D, 0x700E, 0x700F, 0x7010, 0x7011, 0x7012, 0x7013, 0x7014);

    private ulong nextAddress = 0x10_0000;
    private readonly CityRecord[] cities;
    private readonly Dictionary<int, OfficerRecord> officers = [];
    private readonly Dictionary<int, ForceRecord> forces = [];
    private readonly Dictionary<int, CorpsRecord> corpsRecords = [];
    private MilitaryPointCaps? militaryCaps;
    private readonly byte[] corpsForce = new byte[52];
    private readonly List<UnitRecord> units = [];
    private readonly List<ItemRecord> items = [];
    private readonly List<PersonalityRecord> personalities = [];
    private readonly Dictionary<int, BuildingRecord> castles = [];
    private readonly List<GateRecord> gates = [];
    private readonly List<Facility> facilities = [];
    private readonly List<CountyRecord> counties = [];
    private byte[] tileOwners = [];
    private ushort[] tileCounties = [];
    private PlayRecord? play;
    private (int Year, int Month, int Day)? date = (203, 3, 2);

    internal TestGame()
    {
        cities = Enumerable.Range(0, GameMemory.CityCount + 1).Select(id =>
        {
            byte[] data = Record(GameMemory.CityStride, Classes.City);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(CityFields.IdOffset), (ushort)id);
            data[0xCE] = id == 0 ? (byte)0 : (byte)1;
            Encoding.Unicode.GetBytes(CityNames.ById[id]).CopyTo(data, CityFields.NameOffset);
            return new CityRecord(id, Address(), data);
        }).ToArray();
    }

    // A force led by its ruler, holding the given corps.
    internal TestGame Force(int id, int ruler, params int[] corps)
    {
        byte[] data = Record(0x120, Classes.Force);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(ForceFields.RulerOffset), (ushort)ruler);
        Set(data, ForceFields.Friendship(id), 100);
        forces[id] = new ForceRecord(id, Address(), data);
        foreach (int c in corps) corpsForce[c] = (byte)id;
        return this;
    }

    internal TestGame MilitaryPoints(int corps, int force, int number, int leader, int points, int rank = 9)
    {
        byte[] data = Record(CorpsFields.Size, Classes.Corps);
        data[0x10] = (byte)force; data[0x11] = (byte)number;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x12), (ushort)leader);
        Set(data, CorpsFields.MilitaryPoints, points);
        corpsRecords[corps] = new(corps, Address(), data);
        corpsForce[corps] = (byte)force;
        Set(forces[force].Data, ForceFields.Rank, rank);
        byte[] caps = new byte[MilitaryPointCaps.Size];
        foreach (var (at, value) in new[] { (0, 50_000), (8, 100_000), (20, 150_000), (32, 200_000) })
            BinaryPrimitives.WriteInt32LittleEndian(caps.AsSpan(at), value);
        militaryCaps ??= new(Address(), caps);
        return this;
    }

    internal TestGame City(int id, int corps, int governor = 0)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(cities[id].Data.AsSpan(CityFields.CorpsOffset), (ushort)corps);
        Set(cities[id].Data, CityFields.Governor, governor);
        return this;
    }

    // An officer of a corps (0 for none) in a base; he is in it unless placed elsewhere.
    internal TestGame Officer(int id, string name, int corps, int baseId, int status, int? place = null, int loyalty = 90, int office = OfficerFields.NoOffice)
    {
        byte[] data = Record(GameMemory.OfficerStride, Classes.Officer);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(OfficerFields.IdOffset), (ushort)id);
        Encoding.Unicode.GetBytes(name).CopyTo(data, OfficerFields.SurnameOffset);
        Set(data, OfficerFields.CorpsField, corps);
        Set(data, OfficerFields.BaseField, baseId);
        Set(data, OfficerFields.PlaceField, place ?? baseId);
        Set(data, OfficerFields.StatusField, status);
        Set(data, OfficerFields.Office, office);
        Set(data, OfficerFields.Loyalty, loyalty);
        officers[id] = new OfficerRecord(id, Address(), data);
        return this;
    }

    // A force's slots of one 정책 field: (officer, days still to go) from the first slot.
    internal TestGame Policy(int force, int field, params (int Officer, int Days)[] slots)
    {
        for (int slot = 0; slot < slots.Length; slot++)
        {
            Set(forces[force].Data, ForceFields.PolicyOfficer(field, slot), slots[slot].Officer);
            Set(forces[force].Data, ForceFields.PolicyDays(field, slot), slots[slot].Days);
        }
        return this;
    }

    internal TestGame Strategist(int force, int officer)
    {
        Set(forces[force].Data, ForceFields.Strategist, officer);
        return this;
    }

    // A unit on the map led by the officer.
    internal TestGame Leading(int officer)
    {
        byte[] data = Record(GameMemory.UnitStride, Classes.Unit);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(UnitFields.KindOffset), UnitFields.ArmyKind);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(UnitFields.CommanderOffset), (ushort)officer);
        units.Add(new UnitRecord(units.Count, Address(), data, 0));
        return this;
    }

    // Any field of an officer, for what the shortcuts above do not set (배우자, 의형제, 능력...).
    internal TestGame With(int officer, FieldDef field, long value)
    {
        Set(officers[officer].Data, field, value);
        return this;
    }

    // A 명품: who holds it (0 none), its state (1 found, 2 not found yet), where an unfound one lies, and the ability
    // it adds (0 none) by how much.
    internal TestGame Item(int index, string name, int owner, int state = ItemFields.Found, int place = 0, int ability = 0, int bonus = 0)
    {
        byte[] data = Record(GameMemory.ItemStride, Classes.Item);
        Encoding.Unicode.GetBytes(name).CopyTo(data, ItemFields.NameOffset);
        Set(data, ItemFields.Owner, owner);
        Set(data, ItemFields.State, state);
        Set(data, ItemFields.Place, place);
        Set(data, ItemFields.Ability, ability);
        Set(data, ItemFields.Bonus, bonus);
        items.Add(new ItemRecord(index, Address(), data));
        return this;
    }

    // A 개성 with its description and its effects (kind, value; kind 0 for none), and whom they reach and when.
    // A 개성 with its effects, condition (its number, and the second condition its units must meet) and duration (-1
    // always on, 0 while the condition holds, days once attacked).
    internal TestGame Personality(int index, string name, string description, (int Kind, int Value) first, (int Kind, int Value) second = default,
        int condition = PersonalityFields.NoCondition, int conditionValue = 0, int filter = 0, int duration = 0)
    {
        byte[] data = Record(PersonalityFields.Size, Classes.Personality);
        Encoding.Unicode.GetBytes(name).CopyTo(data, PersonalityFields.NameOffset);
        Encoding.Unicode.GetBytes(description).CopyTo(data, PersonalityFields.DescriptionOffset);
        BitConverter.TryWriteBytes(data.AsSpan(PersonalityFields.ConditionOffset), condition);
        BitConverter.TryWriteBytes(data.AsSpan(PersonalityFields.ConditionValueOffset), conditionValue);
        BitConverter.TryWriteBytes(data.AsSpan(PersonalityFields.FilterOffset), filter);
        BitConverter.TryWriteBytes(data.AsSpan(PersonalityFields.DurationOffset), (short)duration);
        foreach ((int effect, (int kind, int value)) in new[] { (0, first), (1, second) })
        {
            Set(data, PersonalityFields.Kinds[effect], kind);
            Set(data, PersonalityFields.Values[effect], value);
        }
        personalities.Add(new PersonalityRecord(index, Address(), data));
        return this;
    }

    // A city's castle building with its durability.
    internal TestGame Castle(int city, int durability, int tile = 0)
    {
        byte[] data = Record(GameMemory.BuildingStride, Classes.Building);
        Set(data, CityFields.Durability, durability);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(GameMemory.BuildingTileOffset), checked((ushort)tile));
        data[GameMemory.BuildingTypeOffset] = 1;
        castles[city] = new BuildingRecord(castles.Count, Address(), data);
        return this;
    }

    // A gate (52~61) with its 태수, building and county centre. Its county's land belongs to the given corps' force.
    internal TestGame Gate(int id, int corps, int governor = 0, int durability = 5000)
    {
        byte[] data = Record(0xC0, Classes.Gate);
        Encoding.Unicode.GetBytes(CityNames.BaseName(id)).CopyTo(data, CityFields.NameOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(CityFields.IdOffset), (ushort)id);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(CityFields.CorpsOffset), (ushort)corps);
        Set(data, CityFields.GateGovernor, governor);
        Set(data, GateFields.BaseDurability, durability);
        byte[] building = Record(GameMemory.BuildingStride, Classes.Building);
        Set(building, GateFields.Durability, durability);
        int tile = id * 2, center = tile + 1;
        BinaryPrimitives.WriteUInt16LittleEndian(building.AsSpan(GameMemory.BuildingTileOffset), (ushort)tile);
        building[GameMemory.BuildingTypeOffset] = 2;
        building[GameMemory.BuildingForceOffset] = corpsForce[corps];
        byte[] county = Record(GameMemory.CountyStride, Classes.County);
        county[CountyFields.HomeCityOffset] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(county.AsSpan(CountyFields.CenterTileOffset), (ushort)center);
        counties.Add(new CountyRecord(id, Address(), county));
        if (tileOwners.Length == 0) { tileOwners = new byte[GameMemory.TileCount]; tileCounties = new ushort[GameMemory.TileCount]; }
        tileOwners[tile] = tileOwners[center] = corpsForce[corps];
        tileCounties[tile] = (ushort)id;
        gates.Add(new GateRecord(id, Address(), data, new BuildingRecord(100 + gates.Count, Address(), building)));
        return this;
    }

    internal TestGame Facility(int index, int kind, int tile, int force, int durability, bool built = true)
    {
        byte[] data = Record(GameMemory.BuildingStride, Classes.Building);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(GameMemory.BuildingTileOffset), (ushort)tile);
        data[GameMemory.BuildingTypeOffset] = (byte)kind;
        data[GameMemory.BuildingForceOffset] = (byte)force;
        data[GameMemory.BuildingBuiltOffset] = built ? (byte)1 : (byte)0;
        Set(data, FacilityFields.Durability, durability);
        facilities.Add(San14Tenkazu.Facility.FromBuilding(new BuildingRecord(index, Address(), data)));
        return this;
    }

    // The player's play record with these 고대무장 bits and tutorials cleared (no 전기 cleared).
    internal TestGame Play(ulong unlocks, uint tutorials = 0)
    {
        byte[] data = new byte[PlayRecordFields.Size];
        BinaryPrimitives.WriteUInt64LittleEndian(data, PlayRecordFields.Header);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(PlayRecordFields.Unlocks1.Offset), unlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(PlayRecordFields.TutorialsOffset), tutorials);
        data.AsSpan(PlayRecordFields.RanksOffset, PlayRecordFields.RankCount).Fill(PlayRecordFields.NoRank);
        play = new PlayRecord(Address(), data);
        return this;
    }

    internal TestGame WithoutDate()
    {
        date = null;
        return this;
    }

    internal GameSnapshot Build(RuntimeRuleSettings? rules = null, int playerForce = -1) => new()
    {
        Session = new(GameSessionPhase.Playing, 0, 0, []),
        RuntimeRules = rules ?? RuntimeRuleSettings.Original,
        PlayerForce = playerForce,
        Cities = cities,
        Counties = [.. counties],
        Officers = [.. officers.Values],
        CastleByCity = castles,
        TileOwners = tileOwners,
        TileCounties = tileCounties,
        Forces = forces.Values.ToDictionary(f => f.Id, f => new ForceInfo(f.Id, f.Ruler, 0, "")),
        ForceRecords = forces.Values.ToDictionary(f => f.Id),
        CorpsRecords = corpsRecords.ToDictionary(),
        MilitaryCaps = militaryCaps,
        CorpsForce = corpsForce,
        OfficerById = officers,
        Units = [.. units],
        Items = [.. items],
        Personalities = [.. personalities],
        Gates = [.. gates],
        Facilities = [.. facilities],
        Play = play,
        Date = date
    };

    private ulong Address() => nextAddress += 0x1000;

    private static byte[] Record(int size, ulong vtable)
    {
        var data = new byte[size];
        BinaryPrimitives.WriteUInt64LittleEndian(data, vtable);
        return data;
    }

    private static void Set(byte[] record, FieldDef field, long value) => field.Encode(value).CopyTo(record, field.Offset);
}

// The game as a plan would leave it: each record it writes, as read with its changes on top.
internal sealed class Written(EditPlan plan)
{
    private readonly Dictionary<ulong, byte[]> records = Apply(plan);

    private static Dictionary<ulong, byte[]> Apply(EditPlan plan)
    {
        var records = new Dictionary<ulong, byte[]>();
        foreach (RecordEdit edit in plan.Edits)
        {
            if (!records.TryGetValue(edit.Address, out byte[]? record)) records[edit.Address] = record = (byte[])edit.Expected.Clone();
            foreach (FieldChange change in edit.Changes) change.Field.Encode(change.Value).CopyTo(record, change.Field.Offset);
        }
        return records;
    }

    // A field of a record after the plan (as it was when the plan does not write that record).
    internal long this[OfficerRecord officer, FieldDef field] => Value(officer.Address, officer.Data, field);
    internal long this[CityRecord city, FieldDef field] => Value(city.Address, city.Data, field);
    internal long this[ForceRecord force, FieldDef field] => Value(force.Address, force.Data, field);
    internal long this[ItemRecord item, FieldDef field] => Value(item.Address, item.Data, field);
    internal long this[BuildingRecord building, FieldDef field] => Value(building.Address, building.Data, field);
    internal long this[PersonalityRecord personality, FieldDef field] => Value(personality.Address, personality.Data, field);
    internal long this[GateRecord gate, FieldDef field] => Value(gate.Address, gate.Data, field);

    internal bool Writes(ulong address) => records.ContainsKey(address);

    private long Value(ulong address, byte[] before, FieldDef field) => field.Read(records.TryGetValue(address, out byte[]? after) ? after : before);
}

// The game's memory for the write tests: a copy of every record of a made-up game at its address. A test changes a
// field here to stand in for the game moving on between a read and a write.
internal sealed class FakeMemory : IRecordMemory
{
    private readonly Dictionary<ulong, byte[]> records = [];

    internal FakeMemory(GameSnapshot data)
    {
        IEnumerable<(ulong Address, byte[] Data)> all = data.Cities.Select(r => (r.Address, r.Data))
            .Concat(data.Officers.Select(r => (r.Address, r.Data))).Concat(data.ForceRecords.Values.Select(r => (r.Address, r.Data)))
            .Concat(data.CorpsRecords.Values.Select(r => (r.Address, r.Data)))
            .Concat(data.MilitaryCaps is { } caps ? [(caps.Address, caps.Data)] : [])
            .Concat(data.Units.Select(r => (r.Address, r.Data))).Concat(data.Items.Select(r => (r.Address, r.Data)))
            .Concat(data.CastleByCity.Values.Select(r => (r.Address, r.Data))).Concat(data.Personalities.Select(r => (r.Address, r.Data)))
            .Concat(data.Gates.Select(r => (r.Address, r.Data))).Concat(data.Gates.Select(g => g.Building).OfType<BuildingRecord>().Select(r => (r.Address, r.Data)))
            .Concat(data.Facilities.Select(f => f.Building).OfType<BuildingRecord>().Select(r => (r.Address, r.Data)))
            .Concat(data.Play is { } play ? [(play.Address, play.Data)] : []);
        foreach ((ulong address, byte[] bytes) in all) records[address] = (byte[])bytes.Clone();
    }

    // A record as it is now; null once it is gone.
    internal byte[]? this[ulong address] => records.TryGetValue(address, out byte[]? data) ? (byte[])data.Clone() : null;

    // The game changes a field of a record.
    internal void Set(ulong address, FieldDef field, long value) => field.Encode(value).CopyTo(records[address], field.Offset);

    // The record is gone (a unit that fell, say).
    internal void Remove(ulong address) => records.Remove(address);

    public byte[] Read(ulong address, int length)
    {
        (ulong start, byte[] data) = Holding(address);
        return data.AsSpan((int)(address - start), length).ToArray();
    }

    public void Write(ulong address, byte[] bytes)
    {
        (ulong start, byte[] data) = Holding(address);
        bytes.CopyTo(data, (int)(address - start));
    }

    private (ulong Start, byte[] Data) Holding(ulong address) =>
        records.FirstOrDefault(r => address >= r.Key && address < r.Key + (ulong)r.Value.Length) is { Value: not null } found
            ? (found.Key, found.Value) : throw new System.ComponentModel.Win32Exception(299, "읽을 수 없는 주소");
}

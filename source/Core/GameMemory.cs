using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace San14Tenkazu;

// The game process the editor is connected to: its exe's SHA-256 (Verified when it is the build every offset here
// was checked against) and the vtables of its record types, found in that exe.
internal readonly record struct GameIdentity(int ProcessId, DateTime StartTimeUtc, ulong ModuleBase, string ExeHash, bool Verified, GameClasses Classes);

// Where each table of pointers to the game's records lies (addresses in the game). They are all listed in one
// "game base" object: for the verified build at known offsets in it, for another build found by their records'
// classes (GameMemory.DiscoverTables). State is the slot that holds the pointer to the game state object; a table
// not found is 0.
internal sealed record GameTables(ulong City, ulong Force, ulong Corps, ulong Tile, ulong County, ulong Building, ulong Officer,
    ulong Unit, ulong State, ulong Country, ulong Item, ulong Personality, ulong Tactic, ulong Policy, ulong Ideology, ulong Office, ulong Rank, ulong Formation);

// Where each game table lives. Every array below is contiguous; the addresses stay valid until a different save is
// loaded. State is the game state object (date, player force); 0 when it did not check out.
// The base table goes on past the cities: the five tribes (IDs 47~51) follow the cities in the same array
// (Tribes is false when they do not), then the ten gates (IDs 52~61) in their own array (Gates, 0 = missing).
internal sealed record GameLayout(
    GameTables Tables, ulong CityArray, ulong CountyArray, int CountyCount, ulong OfficerArray, int OfficerCount,
    ulong TileArray, ulong BuildingArray, int BuildingCount, ulong[] Forces, ulong[] Corps, ulong UnitArray, int UnitCount,
    ulong State, bool Tribes = false, ulong[]? Gates = null, ulong ItemArray = 0, int ItemCount = 0)
{
    // For a build other than the verified one: the exe's static pointers that led to the city table, kept by the
    // editor so the next connection to that build needs no search (empty when none were looked for).
    internal IReadOnlyList<BasePointer> Pointers { get; init; } = [];
    internal bool SameStorage(GameLayout other) => Tables == other.Tables && CityArray == other.CityArray
        && CountyArray == other.CountyArray && CountyCount == other.CountyCount && OfficerArray == other.OfficerArray && OfficerCount == other.OfficerCount
        && TileArray == other.TileArray && BuildingArray == other.BuildingArray && BuildingCount == other.BuildingCount
        && UnitArray == other.UnitArray && UnitCount == other.UnitCount && State == other.State && Tribes == other.Tribes
        && ItemArray == other.ItemArray && ItemCount == other.ItemCount && Forces.SequenceEqual(other.Forces) && Corps.SequenceEqual(other.Corps)
        && (Gates ?? []).SequenceEqual(other.Gates ?? []);
}

// A static pointer in the exe leading to the city table: [exe + Rva] + Delta. (On the verified build the one the game
// itself uses is exe+0x277DF18, 0xDD38 below the city table.)
internal readonly record struct BasePointer(ulong Rva, ulong Delta);

internal sealed record CityRecord(int CityId, ulong Address, byte[] Data)
{
    internal string Name => GameMemory.ReadName(Data, CityFields.NameOffset, 4);
    internal int Corps => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(CityFields.CorpsOffset));
    internal int Governor => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(CityFields.GovernorOffset));
    internal long this[FieldDef field] => field.Read(Data);
}

internal sealed record CountyRecord(int Index, ulong Address, byte[] Data)
{
    internal string Name => GameMemory.ReadName(Data, CountyFields.NameOffset, 5);
    internal int HomeCity => Data[CountyFields.HomeCityOffset];
    internal int CurrentCity => Data[CountyFields.CurrentCityOffset];
    internal int CenterTile => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(CountyFields.CenterTileOffset));
    internal long this[FieldDef field] => field.Read(Data);
}

// A city, gate or facility's building; its +0x14 is the durability the game actually uses.
internal sealed record BuildingRecord(int Index, ulong Address, byte[] Data)
{
    internal int Tile => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(GameMemory.BuildingTileOffset));
    internal long this[FieldDef field] => field.Read(Data);
}

internal sealed record OfficerRecord(int Slot, ulong Address, byte[] Data)
{
    internal int Id => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(OfficerFields.IdOffset));
    internal string Name => GameMemory.ReadName(Data, OfficerFields.SurnameOffset, 7) + GameMemory.ReadName(Data, OfficerFields.GivenNameOffset, 7);
    internal string CourtesyName => GameMemory.ReadName(Data, OfficerFields.CourtesyNameOffset, 3);
    internal int Corps => Data[OfficerFields.CorpsOffset];
    internal int BaseId => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(OfficerFields.BaseOffset));
    internal int Status => Data[OfficerFields.StatusOffset];
    internal int Face => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(OfficerFields.FaceOffset));
    internal long this[FieldDef field] => field.Read(Data);
}

// A gate (관문, base IDs 52~61): a shorter record than a city's (0xC0 bytes) laid out as a city's up to its ID, so
// its name, 태수, corps, troops, morale and base durability sit where a city's do (GateFields). Its building (type 2)
// holds its tile and its real durability, as a castle holds a city's; null when it was not found (Tile -1).
internal sealed record GateRecord(int Id, ulong Address, byte[] Data, BuildingRecord? Building)
{
    internal string Name => GameMemory.ReadName(Data, CityFields.NameOffset, 4);
    internal int Corps => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(CityFields.CorpsOffset));
    internal int Governor => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(CityFields.GovernorOffset));
    internal int Tile => Building?.Tile ?? -1;
    internal int Durability => Building is { } building ? (int)building[GateFields.Durability] : 0;
    internal long this[FieldDef field] => field.Read(Data);
}

// A facility a force built on the map (진, 요새 and the like): building types 3 and up (MapArt.Type), with
// the owner and durability the building record holds; Built is false while it is still going up.
internal readonly record struct Facility(int Kind, int Tile, int Force, int Durability, bool Built = true, int Index = -1)
{
    internal BuildingRecord? Building { get; init; }

    internal static Facility FromBuilding(BuildingRecord building) => new(building.Data[GameMemory.BuildingTypeOffset], building.Tile,
        building.Data[GameMemory.BuildingForceOffset], (int)building[FacilityFields.Durability], building.Data[GameMemory.BuildingBuiltOffset] != 0, building.Index)
        { Building = building };
}

// A unit (부대) on the map. County is the county under its tile (0 when none), read from the tile table.
internal sealed record UnitRecord(int Slot, ulong Address, byte[] Data, int County)
{
    internal UnitTerrainState? Terrain { get; init; }
    internal int Kind => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(UnitFields.KindOffset));
    internal bool IsArmy => Kind == UnitFields.ArmyKind;
    internal int Commander => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(UnitFields.CommanderOffset));
    internal int Tile => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(UnitFields.TileOffset));
    internal int TargetKind => Data[UnitFields.TargetKindOffset];
    internal int Target => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(UnitFields.TargetOffset));
    internal int NextTile => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(UnitFields.NextTileOffset));
    internal int Formation => Data[UnitFields.FormationOffset];
    // Order 2 sends a unit to its own base: a transport delivering, an army simply moving there.
    internal UnitFields.Order Order => UnitFields.OrderOf(Data[UnitFields.OrderOffset]) is UnitFields.Order.Transport && Kind != UnitFields.TransportKind
        ? UnitFields.Order.Move : UnitFields.OrderOf(Data[UnitFields.OrderOffset]);
    internal long this[FieldDef field] => field.Read(Data);
}

// A 명품 (item) the game has, by its index in the game's table.
internal sealed record ItemRecord(int Index, ulong Address, byte[] Data)
{
    internal string Name => GameMemory.ReadName(Data, ItemFields.NameOffset, ItemFields.NameChars);
    internal int Owner => (int)this[ItemFields.Owner];
    internal int State => (int)this[ItemFields.State];
    internal int Ability => Data[ItemFields.AbilityOffset];
    internal int Bonus => Data[ItemFields.BonusOffset];
    internal long this[FieldDef field] => field.Read(Data);
}

// A 개성 (personality) with a name, by its number in the game's table (the number officers hold in their slots).
internal sealed record PersonalityRecord(int Index, ulong Address, byte[] Data)
{
    internal bool? SlotEnabled { get; init; }
    internal string Name => GameMemory.ReadName(Data, PersonalityFields.NameOffset, PersonalityFields.NameChars);
    internal string Description => GameMemory.ReadName(Data, PersonalityFields.DescriptionOffset, (PersonalityFields.Size - PersonalityFields.DescriptionOffset) / 2).Replace('，', ',');
    internal int Kind(int effect) => (int)this[PersonalityFields.Kinds[effect]];
    internal long Value(int effect) => this[PersonalityFields.Values[effect]];
    internal int Condition => (int)this[PersonalityFields.Condition];
    internal int ConditionValue => (int)this[PersonalityFields.ConditionValue];
    internal int Filter => (int)this[PersonalityFields.Filter];
    internal PersonalityRules.Timing Timing => PersonalityRules.TimingOf(BitConverter.ToInt16(Data, PersonalityFields.DurationOffset));
    internal int Days => BitConverter.ToInt16(Data, PersonalityFields.DurationOffset);
    internal long this[FieldDef field] => field.Read(Data);
}

// The play record as read (PlayRecordFields), up to its 전기제패 ranks.
internal sealed record PlayRecord(ulong Address, byte[] Data)
{
    // Which 고대무장 may appear, a bit each (AncientOfficers.Bit).
    internal ulong Unlocks => BitConverter.ToUInt64(Data, PlayRecordFields.Unlocks1.Offset);
    internal uint Tutorials => BitConverter.ToUInt32(Data, PlayRecordFields.TutorialsOffset);
    internal ReadOnlySpan<byte> Ranks => Data.AsSpan(PlayRecordFields.RanksOffset, PlayRecordFields.RankCount);
    internal long this[FieldDef field] => field.Read(Data);
}

// A force's record as read (the first 0x60 bytes): what the force card shows and writes.
internal sealed record ForceRecord(int Id, ulong Address, byte[] Data)
{
    internal int Ruler => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(ForceFields.RulerOffset));
    internal long this[FieldDef field] => field.Read(Data);
}

// Whether the game moves on: paused by the player ([정지]), running, or not known.
internal enum GamePace { Unknown, Paused, Running }

// ColorIndex picks the game's force colour (MapView.GamePalette); Country is the force's 국호, "" when none.
internal readonly record struct ForceInfo(int Id, int RulerId, int ColorIndex, string Country);

internal sealed class GameSnapshot
{
    internal required CityRecord[] Cities { get; init; }          // index = city ID
    internal required CountyRecord[] Counties { get; init; }      // counties owned by the 46 cities
    internal required OfficerRecord[] Officers { get; init; }     // officers with a name
    internal required Dictionary<int, BuildingRecord> CastleByCity { get; init; }
    internal required byte[] TileOwners { get; init; }            // owning force per map tile
    internal required ushort[] TileCounties { get; init; }        // county index per map tile
    internal required Dictionary<int, ForceInfo> Forces { get; init; }
    internal Dictionary<int, ForceRecord> ForceRecords { get; init; } = [];
    internal Dictionary<int, CorpsRecord> CorpsRecords { get; init; } = [];
    internal MilitaryPointCaps? MilitaryCaps { get; init; }
    internal required byte[] CorpsForce { get; init; }            // corps ID -> force ID
    internal ushort[] CorpsLeaders { get; init; } = [];             // corps ID -> 군단장 (corps record +0x12)
    internal required Dictionary<int, OfficerRecord> OfficerById { get; init; }
    internal required UnitRecord[] Units { get; init; }            // occupied unit slots only
    internal CityRecord[] Tribes { get; init; } = [];              // the tribes' bases, IDs 47~51
    internal GateRecord[] Gates { get; init; } = [];
    internal (int Id, int Tile)[] Landmarks { get; init; } = [];  // 명소 1~100 (CityNames.Landmarks), by tile
    internal Facility[] Facilities { get; init; } = [];
    // 명품 with a name (the table's unused "아이템" records left out); empty when the table did not check out.
    internal ItemRecord[] Items { get; init; } = [];
    // 개성 with a name (the table's unused "개성123" records left out); empty when the table did not check out.
    internal PersonalityRecord[] Personalities { get; init; } = [];
    // The player's play record (which 고대무장 may appear); null when it was not found.
    internal PlayRecord? Play { get; init; }
    // The 고대무장 that come with a DLC whose DLC the game has (AncientOfficers.ComesWithDlc); null when not read
    // (a build other than the verified one).
    internal IReadOnlySet<int>? DlcOfficers { get; init; }
    // Force the player controls; 0 in AUTO(데모) 플레이 (the game plays every force), -1 when unknown.
    internal int PlayerForce { get; init; } = -1;
    // The game's own AUTO(데모) 플레이 flag; while it is set, saving, loading and editing stay disabled.
    internal bool DemoFlag { get; init; }
    // The game's date (game state +0x34 year, +0x36 month, +0x37 day); null when it was not read.
    internal (int Year, int Month, int Day)? Date { get; init; }
    // The hour within the day (game state +0x38, 0~22 in steps of 2), which moves only while the game runs; -1 unread.
    internal int Hour { get; init; } = -1;
    // Whether the game moved on since the read before this one (ReadSnapshot's before); Unknown without one.
    internal GamePace Pace { get; init; }
    internal bool IsDemoPlay => DemoFlag || PlayerForce == 0;

    internal int ForceOfCorps(int corps) => corps >= 0 && corps < CorpsForce.Length ? CorpsForce[corps] : 0;
    internal int CorpsLeader(int corps) => corps >= 0 && corps < CorpsLeaders.Length ? CorpsLeaders[corps] : 0;
    internal int ForceOfCity(int cityId) => ForceOfCorps(Cities[cityId].Corps);

    // A tribe's base belongs to its corps' force, or without one to whoever holds the land under it.
    internal int ForceOfBase(int corps, int tile) => ForceOfCorps(corps) is > 0 and var force ? force : TileOwners[tile];

    // CGateData slot +0xB0 (0x22CD80): the owner of its county's centre hex. A gate's +0x30 corps can still
    // name the former owner's corps (observed for 동관 and 호관), so it never determines ownership.
    internal int ForceOfGate(GateRecord gate)
    {
        if (gate.Tile < 0 || gate.Tile >= TileCounties.Length || TileOwners.Length == 0) return 0;
        if (County(TileCounties[gate.Tile]) is not { Index: > 0, HomeCity: >= 1 and <= GameMemory.TribeLast } county) return 0;
        // The native table lookup maps an out-of-range centre hex to tile 0.
        int tile = county.CenterTile < GameMemory.TileCount ? county.CenterTile : 0;
        return tile < TileOwners.Length ? TileOwners[tile] : 0;
    }

    internal IEnumerable<int> CitiesOf(int force) => Enumerable.Range(1, GameMemory.CityCount).Where(id => ForceOfCity(id) == force);

    internal IEnumerable<GateRecord> GatesOf(int force) => Gates.Where(g => ForceOfGate(g) == force);

    // A force's ruler and the officers serving under him.
    internal IEnumerable<OfficerRecord> OfficersOf(int force) =>
        Officers.Where(o => OfficerFields.IsMember(o.Status) && ForceOfCorps(o.Corps) == force);

    internal string OfficerName(int id) => OfficerById.TryGetValue(id, out OfficerRecord? officer) ? officer.Name : "";

    // A unit is called after its commander; one whose name is not known goes by the number.
    internal string CommanderName(int id) => OfficerName(id) is { Length: > 0 } name ? name : L.Terms.OfficerNumber(id);

    // A slot is used again by another unit once this one is gone, so a unit is named by its commander too.
    internal UnitRecord? Unit(int slot, int commander) => Units.FirstOrDefault(u => u.Slot == slot && u.Commander == commander);

    private Dictionary<int, CountyRecord>? countyByIndex;
    internal CountyRecord? County(int index) => (countyByIndex ??= Counties.ToDictionary(c => c.Index)).GetValueOrDefault(index);

    // A unit belongs to the force of its commander.
    internal int ForceOfUnit(UnitRecord unit) => OfficerById.TryGetValue(unit.Commander, out OfficerRecord? officer) ? ForceOfCorps(officer.Corps) : 0;

    // The base the unit's commander belongs to (the city it marched from), "" when unknown.
    internal string UnitBase(UnitRecord unit) =>
        OfficerById.TryGetValue(unit.Commander, out OfficerRecord? officer) ? CityNames.BaseName(officer.BaseId) : "";

    // The unit another unit is going after, null otherwise.
    internal UnitRecord? TargetUnit(UnitRecord unit) =>
        unit.TargetKind == UnitFields.UnitTarget ? Units.FirstOrDefault(u => u.Slot == unit.Target && u.Slot != unit.Slot) : null;

    // The facility or wall a unit is going after (an index into the buildings), null otherwise.
    internal Facility? TargetFacility(UnitRecord unit) =>
        unit.TargetKind == UnitFields.BuildingTarget && Facilities.FirstOrDefault(f => f.Index == unit.Target) is { Index: >= 0 } facility ? facility : null;

    // The same facility must still occupy this slot, tile and force before a card or its history opens it.
    internal Facility? Facility(int index, int kind, int tile, int force) =>
        Facilities.FirstOrDefault(f => f.Index == index && f.Kind == kind && f.Tile == tile && f.Force == force) is { Building: not null } facility ? facility : null;

    // (Where a unit is going and what a hex is called need the map's city hexes: GamePlaces in the editor.)

    // The county a unit stands in and the city it belongs to, e.g. "계교 (업)".
    internal string PlaceName(UnitRecord unit)
    {
        if (County(unit.County) is not { } county) return "";
        string city = CityNames.ById[county.HomeCity];
        return county.Name == city ? city : $"{county.Name} ({city})";
    }

    // The mark on a force's city banners, like "허" in the game: its 국호, or the ruler's surname without one
    // (seen for 사섭 → "사"). Always one character, the first (사마 → "사"): the colour tells the forces apart.
    internal string ForceMark(int force)
    {
        if (!Forces.TryGetValue(force, out ForceInfo info)) return "";
        string name = info.Country.Length > 0 ? info.Country
            : OfficerById.TryGetValue(info.RulerId, out OfficerRecord? ruler) ? GameMemory.ReadName(ruler.Data, OfficerFields.SurnameOffset, 7) : "";
        return System.Globalization.StringInfo.GetNextTextElement(name);
    }

    // The game as a write would leave it: the records it writes with their changes on top, the rest as read.
    internal GameSnapshot With(IReadOnlyList<RecordEdit> edits)
    {
        var after = new Dictionary<ulong, byte[]>();
        foreach (RecordEdit edit in edits)
        {
            if (!after.TryGetValue(edit.Address, out byte[]? data))
            {
                if (DataAt(edit.Kind, edit.Address) is not { } now) continue;
                after[edit.Address] = data = (byte[])now.Clone();
            }
            foreach (FieldChange change in edit.Changes) change.Field.Encode(change.Value).CopyTo(data, change.Field.Offset);
        }
        byte[] Data(ulong address, byte[] data) => after.GetValueOrDefault(address) ?? data;
        OfficerRecord[] officers = Officers.Select(o => o with { Data = Data(o.Address, o.Data) }).ToArray();
        return new GameSnapshot
        {
            Cities = Cities.Select(c => c with { Data = Data(c.Address, c.Data) }).ToArray(),
            Counties = Counties.Select(c => c with { Data = Data(c.Address, c.Data) }).ToArray(),
            Officers = officers,
            CastleByCity = CastleByCity.ToDictionary(pair => pair.Key, pair => pair.Value with { Data = Data(pair.Value.Address, pair.Value.Data) }),
            TileOwners = TileOwners,
            TileCounties = TileCounties,
            Forces = Forces,
            ForceRecords = ForceRecords.ToDictionary(pair => pair.Key, pair => pair.Value with { Data = Data(pair.Value.Address, pair.Value.Data) }),
            CorpsRecords = CorpsRecords.ToDictionary(pair => pair.Key, pair => pair.Value with { Data = Data(pair.Value.Address, pair.Value.Data) }),
            MilitaryCaps = MilitaryCaps,
            CorpsForce = CorpsForce,
            CorpsLeaders = CorpsLeaders,
            OfficerById = officers.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First()),
            Units = Units.Select(u => u with { Data = Data(u.Address, u.Data) }).ToArray(),
            Tribes = Tribes,
            Gates = Gates.Select(g => g with { Data = Data(g.Address, g.Data), Building = g.Building is { } b ? b with { Data = Data(b.Address, b.Data) } : null }).ToArray(),
            Landmarks = Landmarks,
            Facilities = Facilities.Select(f => f.Building is { } b
                ? San14Tenkazu.Facility.FromBuilding(b with { Data = Data(b.Address, b.Data) }) : f).ToArray(),
            Items = Items.Select(i => i with { Data = Data(i.Address, i.Data) }).ToArray(),
            Personalities = Personalities.Select(p => p with { Data = Data(p.Address, p.Data) }).ToArray(),
            Play = Play is { } play ? play with { Data = Data(play.Address, play.Data) } : null,
            DlcOfficers = DlcOfficers,
            PlayerForce = PlayerForce,
            DemoFlag = DemoFlag,
            Date = Date,
            Hour = Hour,
            Pace = Pace
        };
    }

    // A record's bytes as read now, by its kind and address; null when no such record is here any more.
    internal byte[]? DataAt(RecordKind kind, ulong address) => kind switch
    {
        RecordKind.Force => ForceRecords.Values.FirstOrDefault(f => f.Address == address)?.Data,
        RecordKind.Corps => CorpsRecords.Values.FirstOrDefault(c => c.Address == address)?.Data,
        RecordKind.City => Cities.FirstOrDefault(c => c.Address == address)?.Data,
        RecordKind.County => Counties.FirstOrDefault(c => c.Address == address)?.Data,
        RecordKind.Building => CastleByCity.Values.FirstOrDefault(b => b.Address == address)?.Data ??
            Gates.FirstOrDefault(g => g.Building?.Address == address)?.Building?.Data ??
            Facilities.FirstOrDefault(f => f.Building?.Address == address).Building?.Data,
        RecordKind.Unit => Units.FirstOrDefault(u => u.Address == address)?.Data,
        RecordKind.Item => Items.FirstOrDefault(i => i.Address == address)?.Data,
        RecordKind.Personality => Personalities.FirstOrDefault(p => p.Address == address)?.Data,
        RecordKind.Gate => Gates.FirstOrDefault(g => g.Address == address)?.Data,
        RecordKind.PlayRecord => Play is { } play && play.Address == address ? play.Data : null,
        RecordKind.AiNode or RecordKind.AiArgs => null,   // not in a snapshot: read from the game (GameMemory.ReadAiRecord)
        _ => Officers.FirstOrDefault(o => o.Address == address)?.Data
    };

    internal string ForceName(int force)
    {
        if (force <= 0) return L.Terms.NoForce;
        if (Forces.TryGetValue(force, out ForceInfo info) && OfficerName(info.RulerId) is { Length: > 0 } ruler) return L.Terms.ForceOfRuler(ruler);
        return force is >= 46 and <= 50 ? L.Terms.Tribes : L.Terms.ForceNumber(force);
    }
}

internal sealed record RecordEdit(RecordKind Kind, ulong Address, byte[] Expected, string Label, IReadOnlyList<FieldChange> Changes,
    IReadOnlyList<SlotRestart>? Restarts = null, MilitaryPointGuard? MilitaryPoints = null);

// An officer's 개성 slot the game is to judge afresh once the 개성 is written (PersonalityEdits.Plan): its mark of
// being lit for his unit is cleared, as the game's own slot setter clears it.
internal readonly record struct SlotRestart(ulong Officer, int Slot, int Personality);

// The memory edits are written to: the game's (GameMemory.Apply opens its process) or, in the tests, records made up.
internal interface IRecordMemory
{
    byte[] Read(ulong address, int length);
    void Write(ulong address, byte[] data);
}

// A write refused because the game changed a linked field of the record after it was read (GameMemory.Apply).
internal sealed class StaleEditException(string label, IEnumerable<string> moved)
    : InvalidOperationException($"{label}: {string.Join(", ", moved)}");

// A number in one of the game's tables with the name the game gives it, and its description ("" when none).
internal readonly record struct GameName(int Id, string Name, string Description);

// The game's names for what an officer has: 개성, 전법, 정책, 주의, 관직 and 진형 (with its 기동), and a force's 작위 (the
// tables' placeholders left out).
internal sealed record GameCatalog(GameName[] Personalities, GameName[] Tactics, GameName[] Policies, GameName[] Ideologies, GameName[] Offices, GameName[] Ranks,
    GameName[] Formations)
{
    internal static readonly GameCatalog Empty = new([], [], [], [], [], [], []);
    // Eligible in CEditPersonData -> CPersonData (0x20A800), separate from the live editor's full catalog.
    internal GameName[]? RegisteredPersonalities { get; init; }
    internal GameName[]? RegisteredTactics { get; init; }

    internal static string NameOf(GameName[] names, long id) => names.FirstOrDefault(n => n.Id == id).Name ?? "";

    // A 진형 as the game's table names it, with its 기동 as the description; the editor's own name when the table did not
    // check out.
    internal GameName Formation(int id) =>
        Formations.FirstOrDefault(f => f.Id == id) is { Name: not null } named ? named : new GameName(id, UnitFields.FormationName(id), "");
}

internal static partial class GameMemory
{
    private const string ProcessName = "SAN14CE_KO";
    private const string VerifiedExeHash = "87ED4E46590E0939C9EFCAEF14FA0F892917604DFDA86E0A4BE319FCCFFA2D6A";

    // A test switch: SAN14TENKAZU_UNVERIFIED=1 treats the verified build as an unknown one (tables found by class,
    // read-only until the self-checks pass and writing is allowed), to try that path without another build.
    private static readonly bool TreatAsUnverified = Environment.GetEnvironmentVariable("SAN14TENKAZU_UNVERIFIED") == "1";

    // Game base object of the verified build: [exe + BasePointerRva] + BaseAdjust, and the offsets of its tables.
    private const ulong BasePointerRva = 0x277DF18;
    private const ulong BaseAdjust = 0x290;
    private const ulong CityTable = 0xDAA8, ForceTable = 0xDCA0, CorpsTable = 0xDE40, TileTable = 0xDFE0;
    private const ulong CountyTable = 0x6C860, BuildingTable = 0x6D808, OfficerTable = 0x737C0, UnitTable = 0x7E5A8;
    private const int MaxCounties = 501, MaxBuildings = 3063, MaxOfficers = 4096, ForceCount = 52, UnitSlots = 501;
    // The map: 220 × 220 hexes, one tile record each (MapTerrain in the editor draws them).
    internal const int MapColumns = 220, MapRows = 220;
    internal const int TileCount = MapColumns * MapRows;

    // Game state object (CWorldData): [base + StatePointer]. Its byte +0x3A is the player's force, which the game
    // uses to index the force table. 설정 → 데모 플레이 (exe+0x33B9F0) sets that byte to 0 and bit 0x100 of the
    // dword at +0x16A8, which the game checks before saving, loading, editing and counting records.
    // (It also sets bit 2 of +0x17CD, only stored in saves, and resets the player's corps delegation settings.)
    private const ulong StatePointer = 0x86F58;
    private const int StateRead = 0x40, StatePlayerForceOffset = 0x3A, StateFlagsOffset = 0x16A8, StateYearOffset = 0x34, StateHourOffset = 0x38;
    // Whether the game runs is told by its clock (date and hour) moving: watched every PaceStep ms for up to PaceWatch
    // ms. While it runs the hour moves every 0.1~0.2 s, but it stands a moment now and then (half a second was once
    // too short). (2026-10-02: recording the game while the user paused and resumed it found no flag that holds;
    // exe+0x1B5CAD0 followed the presses then, but read 1 most of the time once the game ran unwatched.)
    private const int PaceWatch = 800, PaceStep = 50;
    private const uint DemoPlayFlag = 0x100;
    internal const int MaxPlayerForce = 45; // 46~50 are the tribes
    internal const int CityStride = 0x168, CountyStride = 0x98, OfficerStride = 0x1F0, TileStride = 0x20, BuildingStride = 0x20, UnitStride = 0x200;
    private const int TileCountyOffset = 0x12;
    private const int ForceRead = 0x120, CorpsRead = CorpsFields.Size, ForceCountryOffset = 0x48, ForceColorOffset = 0x49;
    // A corps record: +0x10 its force, +0x11 its number within the force, +0x12 its 군단장 (read 2026-10-02: 조조, 손책, 원소
    // leading their first corps).
    private const int CorpsLeaderOffset = 0x12;
    // Past the cities in the base table: tribes 47~51 (city records), a placeholder, then gates 52~61.
    internal const int TribeFirst = 47, TribeLast = 51, GateFirst = 52, GateLast = 61;
    private const int BaseSlots = 63, GateSlot = 53;
    private const int GateRead = 0xC0;
    private const int TileLandmarkOffset = 0x1E, TileBaseOffset = 0x1A;
    internal const int BuildingTileOffset = 0x10;
    internal const int BuildingTypeOffset = 0x12, BuildingForceOffset = 0x13, BuildingDurabilityOffset = 0x14, BuildingBuiltOffset = 0x16;
    // Country names (국호): base + CountryTable holds 101 pointers to records with the UTF-16 name at +0x10;
    // a force's byte +0x48 indexes it (0 = none; 1~48 are the game's names such as 위·촉·오·허).
    private const ulong CountryTable = 0x77ED8;
    private const ulong ItemTable = 0x77888;
    private const int ItemSlots = 202;
    internal const int ItemStride = 0x80;
    private const int CountryCount = 101, CountryRead = 0x30, CountryNameOffset = 0x10;
    // More tables of names, each base + Table holding pointers to records with the name at +0x10 (read on
    // 2026-10-02, every record checked by its vtable): 개성 (the description at +0x3C), 전법 (1~26 the common
    // ones, 31~170 the officers' own), 정책 and 주의 (descriptions at +0x3C, +0x2C), 관직 (73 없음; its grade at +0x37
    // and the troops its holder may lead at +0x34: 정동장군 2품 18,000, 없음 3,000), 작위 (what it takes at +0x44,
    // such as "지배도시 25이상.").
    private const ulong PersonalityTableOffset = 0x7DA88, TacticTableOffset = 0x77240, PolicyTableOffset = 0x78AC0, IdeologyTableOffset = 0x78A68,
        OfficeTableOffset = 0x78268, RankTableOffset = 0x78200, FormationTableOffset = 0x77198;
    private sealed record NameTable(Func<GameTables, ulong> Table, int Count, Func<GameClasses, ulong> Class, int Read, int NameChars,
        int DescriptionOffset = 0, Func<byte[], string>? Describe = null);
    private static readonly NameTable PersonalityTable = new(t => t.Personality, 356, c => c.Personality, 0xB6, 6, 0x3C);
    private static readonly NameTable TacticTable = new(t => t.Tactic, 201, c => c.Tactic, 0x24, 5);
    private static readonly NameTable PolicyTable = new(t => t.Policy, 121, c => c.Policy, 0xA8, 6, 0x3C);
    private static readonly NameTable IdeologyTable = new(t => t.Ideology, 11, c => c.Doctrine, 0x78, 4, 0x2C);
    private static readonly NameTable OfficeTable = new(t => t.Office, 256, c => c.Post, 0x38, 5,
        Describe: record => L.Terms.GradeAndCap(record[0x37], BitConverter.ToUInt16(record, 0x34)));
    private static readonly NameTable RankTable = new(t => t.Rank, 13, c => c.Rank, 0x80, 13, 0x44);
    // 진형: 21 records of 0xC8 bytes (0 unused), +0x7F its 기동, what a unit's speed is made from (0x2B0210 reads it, or
    // the same value kept encoded beside it; both agreed on all 20 on 2026-10-03).
    private const int FormationMobilityOffset = 0x7F;
    private static readonly NameTable FormationTable = new(t => t.Formation, 21, c => c.Formation, 0x80, 5,
        Describe: record => L.Terms.Mobility(record[FormationMobilityOffset]));

    // The verified build's tables, from its base object.
    private static GameTables VerifiedTables(ulong baseObject) => new(
        baseObject + CityTable, baseObject + ForceTable, baseObject + CorpsTable, baseObject + TileTable, baseObject + CountyTable,
        baseObject + BuildingTable, baseObject + OfficerTable, baseObject + UnitTable, baseObject + StatePointer, baseObject + CountryTable,
        baseObject + ItemTable, baseObject + PersonalityTableOffset, baseObject + TacticTableOffset, baseObject + PolicyTableOffset,
        baseObject + IdeologyTableOffset, baseObject + OfficeTableOffset, baseObject + RankTableOffset, baseObject + FormationTableOffset);
    private const int TableNameOffset = 0x10;
    internal const int CityCount = 46;

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmOperation = 0x0008;
    private const uint MemCommit = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQueryEx(IntPtr handle, IntPtr address, byte[] information, UIntPtr length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr bytesRead);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr bytesWritten);

    internal static GameIdentity FindGame()
    {
        Process[] games = Process.GetProcessesByName(ProcessName);
        try
        {
            if (games.Length == 0)
                throw new InvalidOperationException(L.Errors.GameNotFound(ProcessName));
            if (games.Length != 1)
                throw new InvalidOperationException(L.Errors.GameTwice(ProcessName, games.Length));
            Process game = games[0];
            ProcessModule module = game.MainModule ?? throw new InvalidOperationException(L.Errors.ExeInfo);
            // Another build (a patch) is not refused: its record types are found by name in its exe, and the editor
            // treats it as unverified (EditorForm: read-only until its self-checks pass and writing is allowed).
            byte[] exe = File.ReadAllBytes(module.FileName);
            string hash = Convert.ToHexString(SHA256.HashData(exe));
            bool verified = hash.Equals(VerifiedExeHash, StringComparison.OrdinalIgnoreCase) && !TreatAsUnverified;
            ulong moduleBase = (ulong)module.BaseAddress.ToInt64();
            return new GameIdentity(game.Id, game.StartTime.ToUniversalTime(), moduleBase, hash, verified, GameClasses.Find(exe, moduleBase));
        }
        finally
        {
            foreach (Process process in games) process.Dispose();
        }
    }

    // Whether the game is running (verified or not): the editor then connects by itself.
    internal static bool IsRunning()
    {
        Process[] games = Process.GetProcessesByName(ProcessName);
        foreach (Process process in games) process.Dispose();
        return games.Length > 0;
    }

    // The running game's exe before it is connected; null when it is not running or cannot be asked.
    internal static string? RunningGamePath()
    {
        Process[] games = Process.GetProcessesByName(ProcessName);
        try
        {
            foreach (Process process in games)
            {
                try
                {
                    if (process.MainModule?.FileName is { } path) return path;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
            return null;
        }
        finally
        {
            foreach (Process process in games) process.Dispose();
        }
    }

    internal static string? ExecutablePath(GameIdentity identity)
    {
        try
        {
            using Process process = Process.GetProcessById(identity.ProcessId);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    internal static bool IsSameGame(GameIdentity identity)
    {
        try
        {
            using Process process = Process.GetProcessById(identity.ProcessId);
            return process.ProcessName == ProcessName &&
                process.StartTime.ToUniversalTime() == identity.StartTimeUtc &&
                (ulong)(process.MainModule?.BaseAddress.ToInt64() ?? 0) == identity.ModuleBase;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    // Finds the game base object through the exe's static pointer (the verified build), or for another build through
    // the static pointers found on an earlier connection to it (known). Failing that, searches memory for the city
    // table once; the other tables are then at their known offsets from it (verified build) or found by their classes,
    // and for another build the static pointers leading to it are found for the next connection.
    internal static GameLayout Resolve(GameIdentity identity, Action<string>? progress, CancellationToken cancellationToken,
        IReadOnlyList<BasePointer>? known = null)
    {
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle = OpenRead(identity.ProcessId);
        try
        {
            byte[] pointer = new byte[8];
            if (identity.Verified && TryRead(handle, identity.ModuleBase + BasePointerRva, pointer))
            {
                ulong candidate = BitConverter.ToUInt64(pointer);
                if (candidate != 0 && TryBuildLayout(handle, identity, VerifiedTables(candidate + BaseAdjust)) is { } quick) return quick;
            }
            if (!identity.Verified)
                foreach (BasePointer path in known ?? [])
                    if (TryRead(handle, identity.ModuleBase + path.Rva, pointer) && BitConverter.ToUInt64(pointer) is > 0 and var target &&
                        IsCityTable(handle, identity, target + path.Delta) &&
                        TryBuildLayout(handle, identity, DiscoverTables(handle, identity, target + path.Delta)) is { } remembered)
                        return remembered with { Pointers = known! };

            progress?.Invoke(identity.Verified ? L.Connection.SearchAfterQuick
                : L.Connection.SearchUnverified);
            ulong table = ScanCityTable(handle, identity, progress, cancellationToken);
            GameTables tables = identity.Verified ? VerifiedTables(table - CityTable) : DiscoverTables(handle, identity, table);
            GameLayout layout = TryBuildLayout(handle, identity, tables)
                ?? throw new InvalidOperationException(L.Errors.DataNotFound);
            return identity.Verified ? layout : layout with { Pointers = StaticPointers(handle, identity, table) };
        }
        finally { CloseHandle(handle); }
    }

    // The city table: 47 pointers to the city records 0~46, one record (0x168 bytes) after another. Memory is read
    // once, a 4 MiB piece at a time by a few workers each reusing its one buffer, for that run; each run found is
    // checked against the records it points at. (2026-10-02: the game held 7.6 GB, nearly all of it its own
    // read-write memory, so the search cannot skip much; a buffer per piece had allocated 1 GB on the way.)
    private static ulong ScanCityTable(IntPtr handle, GameIdentity identity, Action<string>? progress, CancellationToken cancellationToken)
    {
        const int Piece = 4 * 1024 * 1024, Lookahead = (CityCount + 1) * 8;
        var pieces = new List<(ulong Address, int Length)>();
        foreach ((ulong start, ulong size) in ReadableRegions(handle))
            for (ulong offset = 0; offset < size; offset += Piece)
                pieces.Add((start + offset, (int)Math.Min((ulong)Piece, size - offset)));
        long total = pieces.Sum(p => (long)p.Length), scanned = 0, reported = 0;
        int next = -1;
        var hits = new System.Collections.Concurrent.ConcurrentBag<ulong>();
        void Work()
        {
            byte[] buffer = new byte[Piece + Lookahead];
            for (int index; (index = Interlocked.Increment(ref next)) < pieces.Count;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (ulong address, int size) = pieces[index];
                // Read past the piece's end for a run that starts inside it (where the next page cannot be read, less).
                int length = size + Lookahead;
                if (!ReadProcessMemory(handle, (IntPtr)unchecked((long)address), buffer, (UIntPtr)(uint)length, out UIntPtr read) || read.ToUInt64() < (ulong)length)
                    length = ReadProcessMemory(handle, (IntPtr)unchecked((long)address), buffer, (UIntPtr)(uint)size, out read) ? (int)read.ToUInt64() : 0;
                // Nearly every word is ruled out by the gap to the next one; the rest are checked in full.
                ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(buffer.AsSpan(0, length));
                for (int i = 0, end = Math.Min(size / 8, words.Length - CityCount); i < end; i++)
                    if (words[i + 1] - words[i] == CityStride && IsCityTableAt(MemoryMarshal.AsBytes(words.Slice(i, CityCount + 1))) &&
                        IsCityTable(handle, identity, address + (ulong)(i * 8))) hits.Add(address + (ulong)(i * 8));
                long done = Interlocked.Add(ref scanned, size);
                if (progress is not null && done - Interlocked.Read(ref reported) >= 512L * 1024 * 1024)
                {
                    Interlocked.Exchange(ref reported, done);
                    progress(L.Connection.Searching(done / (1024 * 1024), total / (1024 * 1024)));
                }
            }
        }
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Factory.StartNew(Work, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)))
            .GetAwaiter().GetResult();
        ulong[] found = hits.Distinct().ToArray();
        if (found.Length != 1) throw new InvalidOperationException(L.Errors.TablesNotFound(found.Length));
        return found[0];
    }

    // Whether 47 pointers here go up one city record at a time (the shape of the city table, before reading what they point at).
    internal static bool IsCityTableAt(ReadOnlySpan<byte> words)
    {
        ulong first = BinaryPrimitives.ReadUInt64LittleEndian(words);
        if (first == 0 || (first & 7) != 0) return false;
        for (int k = 1; k <= CityCount; k++)
            if (BinaryPrimitives.ReadUInt64LittleEndian(words[(k * 8)..]) != first + (ulong)(k * CityStride)) return false;
        return true;
    }

    // Whether the table at this address points at the city records 0~46, each a city by its vtable and ID.
    private static bool IsCityTable(IntPtr handle, GameIdentity identity, ulong table)
    {
        byte[] words = new byte[(CityCount + 1) * 8];
        return TryRead(handle, table, words) && IsCityTableAt(words) && VerifyCityArray(handle, BitConverter.ToUInt64(words), identity.Classes.City);
    }

    // The committed, readable memory of the game, region by region.
    private static List<(ulong Start, ulong Size)> ReadableRegions(IntPtr handle)
    {
        var regions = new List<(ulong, ulong)>();
        byte[] info = new byte[48]; // 64-bit MEMORY_BASIC_INFORMATION
        ulong cursor = 0;
        while (cursor < 0x00007FFFFFFFFFFFUL && VirtualQueryEx(handle, (IntPtr)unchecked((long)cursor), info, (UIntPtr)info.Length) != UIntPtr.Zero)
        {
            ulong start = BitConverter.ToUInt64(info, 0), size = BitConverter.ToUInt64(info, 24);
            if (size == 0 || start + size <= cursor) break;
            if (BitConverter.ToUInt32(info, 32) == MemCommit && IsReadable(BitConverter.ToUInt32(info, 36))) regions.Add((start, size));
            cursor = start + size;
        }
        return regions;
    }

    // The exe's static pointers (in its own writable memory) to at most 1 MiB below the city table: the base object
    // that holds the table is among them. (2026-10-02, verified build: three, the game's own exe+0x277DF18 one of them.)
    private static List<BasePointer> StaticPointers(IntPtr handle, GameIdentity identity, ulong cityTable)
    {
        const ulong Reach = 0x100000;
        var pointers = new List<BasePointer>();
        byte[] info = new byte[48];
        ulong cursor = identity.ModuleBase;
        while (VirtualQueryEx(handle, (IntPtr)unchecked((long)cursor), info, (UIntPtr)info.Length) != UIntPtr.Zero)
        {
            ulong start = BitConverter.ToUInt64(info, 0), allocation = BitConverter.ToUInt64(info, 8), size = BitConverter.ToUInt64(info, 24);
            if (allocation != identity.ModuleBase || size == 0) break;
            uint protection = BitConverter.ToUInt32(info, 36) & 0xFF;
            if (BitConverter.ToUInt32(info, 32) == MemCommit && protection is 0x04 or 0x08 && size <= int.MaxValue)
            {
                byte[] data = new byte[size];
                if (TryRead(handle, start, data))
                    for (int i = 0; i + 8 <= data.Length; i += 8)
                        if (BitConverter.ToUInt64(data, i) is var value && value <= cityTable && cityTable - value <= Reach && (value & 7) == 0)
                            pointers.Add(new BasePointer(start + (ulong)i - identity.ModuleBase, cityTable - value));
            }
            cursor = start + size;
        }
        return pointers.Take(16).ToList();
    }

    // For a build other than the verified one: the tables near the city table, each the longest run of pointers to
    // records of its class (empty slots allowed inside a run). For the officers there are several: the officer
    // table is the one whose slot k holds officer k, and of the two that do, the array of records itself (1,851
    // records one after another) rather than the index by officer number in front of the base object (its slots
    // past 1,000 point elsewhere). Read on the verified build on 2026-10-02, this finds every table at its known offset.
    private static GameTables DiscoverTables(IntPtr handle, GameIdentity identity, ulong cityTable)
    {
        const int Before = 0x20000, After = 0xA0000;
        ulong start = cityTable - Before;
        byte[] window = ReadPages(handle, start, Before + After);
        int slots = window.Length / 8;
        ulong At(int slot) => BitConverter.ToUInt64(window, slot * 8);

        GameClasses c = identity.Classes;
        var known = new HashSet<ulong> { c.Force, c.Corps, c.Tile, c.County, c.Building, c.Officer, c.Unit, c.World, c.Kingdom, c.Item,
            c.Personality, c.Tactic, c.Policy, c.Doctrine, c.Post, c.Rank, c.Formation };
        var vtables = new Dictionary<ulong, ulong>();
        byte[] word = new byte[8];
        ulong VtableOf(ulong pointer)
        {
            if (pointer < 0x10000 || pointer > 0x7FFFFFFFFFFF || (pointer & 7) != 0) return 0;
            if (!vtables.TryGetValue(pointer, out ulong vtable)) vtables[pointer] = vtable = TryRead(handle, pointer, word) ? BitConverter.ToUInt64(word) : 0;
            return vtable;
        }

        var runs = new List<(ulong Address, ulong Vtable, int Count)>();
        for (int i = 0; i < slots;)
        {
            ulong vtable = VtableOf(At(i));
            if (!known.Contains(vtable)) { i++; continue; }
            int last = i;
            for (int j = i + 1; j < slots && (At(j) == 0 || VtableOf(At(j)) == vtable); j++)
                if (At(j) != 0) last = j;
            runs.Add((start + (ulong)(i * 8), vtable, last - i + 1));
            i = last + 1;
        }

        byte[] id = new byte[2];
        bool HoldsOfficersByNumber(ulong table)
        {
            ulong[] pointers = ReadPointers(handle, table, 9);
            for (int k = 1; k < pointers.Length; k++)
                if (!TryRead(handle, pointers[k] + OfficerFields.IdOffset, id) || BitConverter.ToUInt16(id) != k) return false;
            return true;
        }
        // Of several runs of a class, the longest: besides its table, a few pointers to one record may sit there (two to
        // single units in 211, the map's chosen ones perhaps).
        ulong Find(ulong vtable)
        {
            var found = runs.Where(r => r.Vtable == vtable).OrderByDescending(r => r.Count).ToList();
            return found.Count == 0 || found.Count > 1 && found[1].Count == found[0].Count ? 0 : found[0].Address;
        }
        ulong FindOfficers()
        {
            var found = runs.Where(r => r.Vtable == c.Officer && HoldsOfficersByNumber(r.Address))
                .Select(r => (r.Address, Records: ContiguousCount(ReadPointers(handle, r.Address, r.Count), OfficerStride)))
                .OrderByDescending(r => r.Records).ToList();
            return found.Count == 0 || found.Count > 1 && found[1].Records == found[0].Records ? 0 : found[0].Address;
        }
        return new GameTables(cityTable, Find(c.Force), Find(c.Corps), Find(c.Tile), Find(c.County), Find(c.Building),
            FindOfficers(), Find(c.Unit), Find(c.World), Find(c.Kingdom), Find(c.Item),
            Find(c.Personality), Find(c.Tactic), Find(c.Policy), Find(c.Doctrine), Find(c.Post), Find(c.Rank), Find(c.Formation));
    }

    // The same, on its own (the live tests compare it with the verified build's tables).
    internal static GameTables DiscoverTables(GameIdentity identity, ulong cityTable)
    {
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return DiscoverTables(handle, identity, cityTable); }
        finally { CloseHandle(handle); }
    }

    // Reads a stretch of memory a page at a time where a single read fails; pages that cannot be read stay zero.
    private static byte[] ReadPages(IntPtr handle, ulong address, int length)
    {
        byte[] data = new byte[length];
        if (TryRead(handle, address, data)) return data;
        const int Page = 0x1000;
        byte[] page = new byte[Page];
        for (int offset = 0; offset < length; offset += Page)
            if (TryRead(handle, address + (ulong)offset, page)) page.AsSpan(0, Math.Min(Page, length - offset)).CopyTo(data.AsSpan(offset));
        return data;
    }

    // The game as it is now; before, the read before this one, tells whether the game moved on since (Pace).
    internal static GameSnapshot ReadSnapshot(GameIdentity identity, GameLayout layout, GameSnapshot? before = null)
    {
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle = OpenRead(identity.ProcessId);
        try
        {
            using var buffers = new SnapshotReadBuffers();
            byte[] allCities = buffers.Read(handle, layout.CityArray, (CityCount + 1) * CityStride);
            var cities = new CityRecord[CityCount + 1];
            for (int id = 0; id <= CityCount; id++)
            {
                byte[] data = Slice(allCities, id, CityStride);
                ValidateCity(data, identity.Classes.City, id);
                cities[id] = new CityRecord(id, layout.CityArray + (ulong)(id * CityStride), data);
            }

            byte[] allCounties = buffers.Read(handle, layout.CountyArray, layout.CountyCount * CountyStride);
            var counties = new List<CountyRecord>();
            for (int index = 0; index < layout.CountyCount; index++)
            {
                byte[] data = Slice(allCounties, index, CountyStride);
                RequireVtable(data, identity.Classes.County, L.Terms.RecordCounty);
                if (data[CountyFields.HomeCityOffset] is >= 1 and <= CityCount)
                    counties.Add(new CountyRecord(index, layout.CountyArray + (ulong)(index * CountyStride), data));
            }

            byte[] allOfficers = buffers.Read(handle, layout.OfficerArray, layout.OfficerCount * OfficerStride);
            var officers = new List<OfficerRecord>();
            for (int slot = 1; slot < layout.OfficerCount; slot++)
            {
                byte[] data = Slice(allOfficers, slot, OfficerStride);
                RequireVtable(data, identity.Classes.Officer, L.Terms.RecordOfficer);
                var officer = new OfficerRecord(slot, layout.OfficerArray + (ulong)(slot * OfficerStride), data);
                if (officer.Id != 0 && officer.Name.Length > 0) officers.Add(officer);
            }

            byte[] tiles = buffers.Read(handle, layout.TileArray, TileCount * TileStride);
            RequireVtable(tiles, identity.Classes.Tile, L.Terms.RecordMap);
            (byte[] owners, ushort[] tileCounties) = ReadTileMap(tiles.AsSpan(0, TileCount * TileStride), before?.TileOwners, before?.TileCounties);

            // Castle buildings are type 1 (cities and tribes), gates type 2; the tile under them names the base.
            // Types 3 and up are facilities forces built (the records past ~3000 are not buildings in use).
            byte[] allBuildings = buffers.Read(handle, layout.BuildingArray, layout.BuildingCount * BuildingStride);
            var castles = new Dictionary<int, BuildingRecord>();
            var gateBuildings = new Dictionary<int, BuildingRecord>();
            var facilities = new List<Facility>();
            for (int index = 0; index < layout.BuildingCount; index++)
            {
                byte[] data = Slice(allBuildings, index, BuildingStride);
                RequireVtable(data, identity.Classes.Building, L.Terms.RecordBuilding);
                int tile = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(BuildingTileOffset));
                if (tile >= TileCount) continue;
                int type = data[BuildingTypeOffset], durability = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(BuildingDurabilityOffset));
                int baseId = BinaryPrimitives.ReadUInt16LittleEndian(tiles.AsSpan(tile * TileStride + TileBaseOffset));
                if (type == 1 && baseId is >= 1 and <= TribeLast)
                    castles[baseId] = new BuildingRecord(index, layout.BuildingArray + (ulong)(index * BuildingStride), data);
                else if (type == 2 && baseId is >= GateFirst and <= GateLast)
                    gateBuildings[baseId] = new BuildingRecord(index, layout.BuildingArray + (ulong)(index * BuildingStride), data);
                else if (type is >= 3 and <= 17 && durability > 0)
                    facilities.Add(Facility.FromBuilding(new BuildingRecord(index, layout.BuildingArray + (ulong)(index * BuildingStride), data)));
            }

            var landmarks = new List<(int Id, int Tile)>();
            for (int i = 0; i < TileCount; i++)
                if (BinaryPrimitives.ReadUInt16LittleEndian(tiles.AsSpan(i * TileStride + TileLandmarkOffset)) is var id and >= 1 and <= 100)
                    landmarks.Add((id, i));

            var tribes = new List<CityRecord>();
            if (layout.Tribes)
            {
                byte[] allTribes = buffers.Read(handle, layout.CityArray + (ulong)(TribeFirst * CityStride), (TribeLast - TribeFirst + 1) * CityStride);
                for (int id = TribeFirst; id <= TribeLast; id++)
                {
                    byte[] data = Slice(allTribes, id - TribeFirst, CityStride);
                    if (BitConverter.ToUInt64(data, 0) == identity.Classes.City && BitConverter.ToUInt16(data, CityFields.IdOffset) == id)
                        tribes.Add(new CityRecord(id, layout.CityArray + (ulong)(id * CityStride), data));
                }
            }

            var gates = new List<GateRecord>();
            byte[] gateBuffer = new byte[GateRead];
            for (int i = 0; layout.Gates is not null && i < layout.Gates.Length; i++)
            {
                int id = GateFirst + i;
                if (layout.Gates[i] == 0 || !TryRead(handle, layout.Gates[i], gateBuffer) ||
                    BitConverter.ToUInt64(gateBuffer, 0) != identity.Classes.Gate || BitConverter.ToUInt16(gateBuffer, CityFields.IdOffset) != id) continue;
                gates.Add(new GateRecord(id, layout.Gates[i], (byte[])gateBuffer.Clone(), gateBuildings.GetValueOrDefault(id)));
            }

            // Units on the map; the tile under each one names its county.
            var units = new List<UnitRecord>();
            if (layout.UnitCount > 0)
            {
                byte[] allUnits = buffers.Read(handle, layout.UnitArray, layout.UnitCount * UnitStride);
                for (int slot = 0; slot < layout.UnitCount; slot++)
                {
                    byte[] data = Slice(allUnits, slot, UnitStride);
                    RequireVtable(data, identity.Classes.Unit, L.Terms.RecordUnit);
                    int tile = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(UnitFields.TileOffset));
                    int county = tile < TileCount ? BinaryPrimitives.ReadUInt16LittleEndian(tiles.AsSpan(tile * TileStride + TileCountyOffset)) : 0;
                    var unit = new UnitRecord(slot, layout.UnitArray + (ulong)(slot * UnitStride), data, county)
                        { Terrain = UnitTerrainState.Read(tiles.AsSpan(0, TileCount * TileStride), tile) };
                    if (unit.Kind != 0 && unit.Commander != 0) units.Add(unit);
                }
            }

            // Cities and officers belong to a corps; the corps record (+0x10) names the force.
            var forces = new Dictionary<int, ForceInfo>();
            var forceRecords = new Dictionary<int, ForceRecord>();
            var corpsRecords = new Dictionary<int, CorpsRecord>();
            MilitaryPointCaps? militaryCaps = null;
            if (identity.Verified)
            {
                var caps = new MilitaryPointCaps(identity.ModuleBase + MilitaryPointCaps.Rva, ReadBytes(handle, identity.ModuleBase + MilitaryPointCaps.Rva, MilitaryPointCaps.Size));
                if (caps.Valid) militaryCaps = caps;
            }
            var corpsForce = new byte[ForceCount];
            var corpsLeaders = new ushort[ForceCount];
            byte[] forceBuffer = new byte[ForceRead];
            byte[] corpsBuffer = new byte[CorpsRead];
            for (int i = 1; i < ForceCount; i++)
            {
                if (layout.Forces[i] != 0 && TryRead(handle, layout.Forces[i], forceBuffer) &&
                    BitConverter.ToUInt64(forceBuffer, 0) == identity.Classes.Force)
                {
                    forces[i] = new ForceInfo(i, BitConverter.ToUInt16(forceBuffer, 0x10), forceBuffer[ForceColorOffset],
                        CountryName(handle, identity, layout, forceBuffer[ForceCountryOffset]));
                    forceRecords[i] = new ForceRecord(i, layout.Forces[i], (byte[])forceBuffer.Clone());
                }
                if (layout.Corps[i] != 0 && TryRead(handle, layout.Corps[i], corpsBuffer) &&
                    BitConverter.ToUInt64(corpsBuffer, 0) == identity.Classes.Corps)
                {
                    corpsForce[i] = corpsBuffer[0x10];
                    corpsLeaders[i] = BitConverter.ToUInt16(corpsBuffer, CorpsLeaderOffset);
                    corpsRecords[i] = new CorpsRecord(i, layout.Corps[i], (byte[])corpsBuffer.Clone());
                }
            }

            var items = new List<ItemRecord>();
            if (layout.ItemCount > 0)
            {
                byte[] allItems = buffers.Read(handle, layout.ItemArray, layout.ItemCount * ItemStride);
                for (int index = 1; index < layout.ItemCount; index++)
                {
                    byte[] data = Slice(allItems, index, ItemStride);
                    RequireVtable(data, identity.Classes.Item, L.Terms.RecordItem);
                    var item = new ItemRecord(index, layout.ItemArray + (ulong)(index * ItemStride), data);
                    if (item.Name is { Length: > 0 } name && name != "아이템") items.Add(item);
                }
            }
            List<PersonalityRecord> personalities = ReadPersonalities(handle, identity, layout);
            PlayRecord? play = ReadPlayRecord(handle, identity);
            IReadOnlySet<int>? dlcOfficers = identity.Verified ? ReadDlcOfficers(handle, identity) : null;

            int playerForce = -1, hour = -1;
            bool demoFlag = false;
            (int Year, int Month, int Day)? date = null;
            byte[] state = new byte[StateRead], flags = new byte[4];
            if (layout.State != 0 && TryRead(handle, layout.State, state) && BitConverter.ToUInt64(state, 0) == identity.Classes.World &&
                TryRead(handle, layout.State + StateFlagsOffset, flags))
            {
                playerForce = state[StatePlayerForceOffset];
                demoFlag = (BitConverter.ToUInt32(flags) & DemoPlayFlag) != 0;
                int year = BitConverter.ToUInt16(state, StateYearOffset), month = state[StateYearOffset + 2], day = state[StateYearOffset + 3];
                if (year is > 100 and < 400 && month is >= 1 and <= 12 && day is >= 1 and <= 31) date = (year, month, day);
                hour = state[StateHourOffset];
            }

            return new GameSnapshot
            {
                Cities = cities,
                Counties = counties.ToArray(),
                Officers = officers.ToArray(),
                CastleByCity = castles,
                TileOwners = owners,
                TileCounties = tileCounties,
                Forces = forces,
                ForceRecords = forceRecords,
                CorpsRecords = corpsRecords,
                MilitaryCaps = militaryCaps,
                CorpsForce = corpsForce,
                CorpsLeaders = corpsLeaders,
                OfficerById = officers.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First()),
                Units = units.ToArray(),
                PlayerForce = playerForce,
                DemoFlag = demoFlag,
                Date = date,
                Hour = hour,
                Pace = before is not { Date: not null, Hour: >= 0 } || date is null || hour < 0 ? GamePace.Unknown
                    : before.Date == date && before.Hour == hour ? GamePace.Paused : GamePace.Running,
                Tribes = tribes.ToArray(),
                Gates = gates.ToArray(),
                Landmarks = landmarks.ToArray(),
                Facilities = facilities.ToArray(),
                Items = items.ToArray(),
                Personalities = personalities.ToArray(),
                Play = play,
                DlcOfficers = dlcOfficers
            };
        }
        finally { CloseHandle(handle); }
    }

    // The play record (PlayRecordFields): on the verified build at its place in the exe's data; on another, looked
    // for there by its header, its change mark (0 or 1) and its 전기제패 ranks (0~4 each), once per run of the game
    // (again a minute later while not found: the game builds it when it first reads prdataN.s14). Null until the
    // game has built it.
    private const ulong PlayRecordRva = 0x2784AB0;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, DateTime), (ulong Address, DateTime Looked)> playRecordAt = new();

    private static PlayRecord? ReadPlayRecord(IntPtr handle, GameIdentity identity)
    {
        ulong address = identity.Verified ? identity.ModuleBase + PlayRecordRva : FindPlayRecord(handle, identity);
        byte[] data = new byte[PlayRecordFields.Size];
        return address != 0 && TryRead(handle, address, data) && IsPlayRecord(data) ? new PlayRecord(address, data) : null;
    }

    internal static bool IsPlayRecord(ReadOnlySpan<byte> data) =>
        data.Length >= PlayRecordFields.Size && BinaryPrimitives.ReadUInt64LittleEndian(data) == PlayRecordFields.Header &&
        data[PlayRecordFields.Changed.Offset] <= 1 &&
        !data.Slice(PlayRecordFields.RanksOffset, PlayRecordFields.RankCount).ContainsAnyExceptInRange((byte)0, (byte)PlayRecordFields.NoRank);

    private static ulong FindPlayRecord(IntPtr handle, GameIdentity identity)
    {
        (int, DateTime) run = (identity.ProcessId, identity.StartTimeUtc);
        if (playRecordAt.TryGetValue(run, out var known) && (known.Address != 0 || DateTime.UtcNow - known.Looked < TimeSpan.FromMinutes(1)))
            return known.Address;
        ulong found = 0;
        byte[] info = new byte[48];
        ulong cursor = identity.ModuleBase;
        while (found == 0 && VirtualQueryEx(handle, (IntPtr)unchecked((long)cursor), info, (UIntPtr)info.Length) != UIntPtr.Zero)
        {
            ulong start = BitConverter.ToUInt64(info, 0), allocation = BitConverter.ToUInt64(info, 8), size = BitConverter.ToUInt64(info, 24);
            if (allocation != identity.ModuleBase || size == 0) break;
            uint protection = BitConverter.ToUInt32(info, 36) & 0xFF;
            if (BitConverter.ToUInt32(info, 32) == MemCommit && protection is 0x04 or 0x08 && size <= int.MaxValue)
            {
                byte[] data = new byte[size];
                if (TryRead(handle, start, data))
                    for (int i = 0; i + PlayRecordFields.Size <= data.Length; i += 8)
                        if (BitConverter.ToUInt64(data, i) == PlayRecordFields.Header && IsPlayRecord(data.AsSpan(i)))
                        {
                            found = start + (ulong)i;
                            break;
                        }
            }
            cursor = start + size;
        }
        playRecordAt[run] = (found, DateTime.UtcNow);
        return found;
    }

    // The 고대무장 that come with a DLC whose DLC the game has: the game's content object (exe + 0x2781DC0, built by
    // 0x36E480) keeps two sets of officer IDs, a bit each from +0x5C0 and +0xE70, and offers such an officer only
    // when both hold him (0x36F140). Read on the verified build only; on 2026-10-03, with no DLC officers of
    // 2078~2082, both held none of them and 24 of the DLC officers 5101~5152.
    private const ulong ContentRva = 0x2781DC0;
    private const int ContentHeldOffset = 0x5C0, ContentOfferedOffset = 0xE70;

    private static IReadOnlySet<int>? ReadDlcOfficers(IntPtr handle, GameIdentity identity)
    {
        const int first = AncientOfficers.FirstWithDlc, last = AncientOfficers.LastWithDlc;
        int from = first / 8, length = last / 8 - from + 1;
        byte[] held = new byte[length], offered = new byte[length];
        ulong content = identity.ModuleBase + ContentRva;
        if (!TryRead(handle, content + ContentHeldOffset + (ulong)from, held) || !TryRead(handle, content + ContentOfferedOffset + (ulong)from, offered)) return null;
        bool Has(byte[] bits, int id) => (bits[id / 8 - from] >> (id % 8) & 1) != 0;
        return Enumerable.Range(first, last - first + 1).Where(id => Has(held, id) && Has(offered, id)).ToHashSet();
    }

    // The 개성 with a name, each checked by its class; none when the table is not there.
    private static List<PersonalityRecord> ReadPersonalities(IntPtr handle, GameIdentity identity, GameLayout layout)
    {
        var personalities = new List<PersonalityRecord>();
        if (layout.Tables.Personality == 0) return personalities;
        ulong[] pointers;
        try { pointers = ReadPointers(handle, layout.Tables.Personality, PersonalityTable.Count); }
        catch (Win32Exception) { return personalities; }
        byte[] buffer = new byte[PersonalityFields.Size];
        for (int index = 1; index < pointers.Length; index++)
        {
            if (!TryRead(handle, pointers[index], buffer) || BitConverter.ToUInt64(buffer, 0) != identity.Classes.Personality) continue;
            var personality = new PersonalityRecord(index, pointers[index], (byte[])buffer.Clone());
            personality = personality with { SlotEnabled = UnitConditions.ReadSlotEnabled(personality, (address, size) =>
            {
                byte[] encoded = new byte[size];
                return TryRead(handle, address, encoded) ? encoded : null;
            }) };
            if (!IsPlaceholder(personality.Name)) personalities.Add(personality);
        }
        return personalities;
    }

    // Whether the game is paused now: whether its date and hour moved (Running, as soon as they do) or stood still for
    // PaceWatch ms (Paused). The game's own halt of a few seconds at the turn of a 旬 reads as Paused.
    internal static GamePace ReadPace(GameIdentity identity, GameLayout layout)
    {
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle = OpenRead(identity.ProcessId);
        try
        {
            if (layout.State == 0) return GamePace.Unknown;
            byte[] before = new byte[5], after = new byte[5];
            if (!TryRead(handle, layout.State + StateYearOffset, before)) return GamePace.Unknown;
            for (int waited = 0; waited < PaceWatch; waited += PaceStep)
            {
                Thread.Sleep(PaceStep);
                if (!TryRead(handle, layout.State + StateYearOffset, after)) return GamePace.Unknown;
                if (!before.AsSpan().SequenceEqual(after)) return GamePace.Running;
            }
            return GamePace.Paused;
        }
        finally { CloseHandle(handle); }
    }

    // Writes every edit, re-validating each record first and reading it back afterwards.
    // If anything fails, restore only bytes still belonging to this write and verify the recovery.
    // An incomplete recovery reports every failed field instead of claiming the write was undone.
    // The game moves on while the editor shows what it read (and while a question waits for an answer), so each
    // record is compared with edit.Expected right before it is written: an edit that changes a linked field
    // (LinkedFields) is refused when any linked field of its record has moved since, and a plain value the game
    // changed meanwhile is written over and returned, to be reported.
    internal static IReadOnlyList<string> Apply(GameIdentity identity, IReadOnlyList<RecordEdit> edits)
    {
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        uint access = ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation;
        IntPtr handle = OpenProcess(access, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try
        {
            ValidateForceAgreementCalls(handle, identity, edits);
            return Apply(new ProcessMemory(handle), identity.Classes, edits);
        }
        finally { CloseHandle(handle); }
    }

    // The write itself, on the game's memory or (in the tests) on records made up in memory.
    internal static IReadOnlyList<string> Apply(IRecordMemory memory, GameClasses classes, IReadOnlyList<RecordEdit> edits)
    {
        var undo = new WriteRecovery(memory, classes);
        var written = new HashSet<ulong>();   // fields this call has written: a later edit of the record finds them changed
        var overwritten = new List<string>();
        try
        {
            foreach (RecordEdit edit in edits)
            {
                if (edit.Changes.Count == 0) continue;
                byte[] before = memory.Read(edit.Address, edit.Expected.Length);
                ValidateSameRecord(classes, edit, before);
                if (edit.Kind == RecordKind.Corps) MilitaryIntelligence.Check(memory, classes, edit, before);
                bool Moved(FieldDef field) => !written.Contains(edit.Address + (ulong)field.Offset) && field.Read(before) != field.Read(edit.Expected);
                IReadOnlyList<FieldDef> linked = LinkedFields.Of(edit.Kind);
                if (edit.Changes.Where(c => ForceFields.IsAgreement(c.Field)).Select(c => c.Field).Where(Moved).ToList() is { Count: > 0 } treaties)
                    throw new StaleEditException(edit.Label, treaties.Select(f => $"{f.Label} {f.Read(edit.Expected):N0} → {f.Read(before):N0}"));
                if (edit.Changes.Any(c => linked.Contains(c.Field)) && linked.Where(Moved).ToList() is { Count: > 0 } moved)
                    throw new StaleEditException(edit.Label, moved.Select(f => $"{f.Label} {f.Read(edit.Expected):N0} → {f.Read(before):N0}"));
                foreach (FieldChange change in edit.Changes)
                    if (Moved(change.Field) && change.Field.Read(before) != change.Value)
                        overwritten.Add(L.Edits.Overwritten(edit.Label, change.Field.Label, change.Field.Read(edit.Expected), change.Field.Read(before), change.Value));
                // A 개성 slot goes in with what its old 개성 had going cleared, as the game writes one; a change to
                // the play record raises its change mark, as the game's own setters do.
                List<FieldChange> along = edit.Kind switch
                {
                    RecordKind.Officer => OfficerEdits.SlotStateCleared(before, edit.Changes),
                    RecordKind.PlayRecord => [new FieldChange(PlayRecordFields.Changed, 1)],
                    _ => []
                };
                foreach (FieldChange change in edit.Changes.Concat(along))
                {
                    ulong target = edit.Address + (ulong)change.Field.Offset;
                    undo.Write(edit, before, change);
                    written.Add(target);
                }
                byte[] after = memory.Read(edit.Address, edit.Expected.Length);
                ValidateSameRecord(classes, edit, after);
                if (edit.Kind == RecordKind.Corps) MilitaryIntelligence.Check(memory, classes, edit, after);
                foreach (FieldChange change in edit.Changes)
                {
                    long actual = change.Field.Read(after);
                    if (actual != change.Value)
                        throw new InvalidOperationException(L.Errors.WriteMismatch(edit.Label, change.Field.Label, actual));
                }
                // The slots still holding the 개성 and lit go dark, read as they are now (the game lights them within
                // seconds); not undone, as an undo restarts them again.
                foreach (SlotRestart restart in edit.Restarts ?? [])
                {
                    FieldDef lit = OfficerFields.PersonalitiesInEffect;
                    byte[] officer = memory.Read(restart.Officer, lit.Offset + lit.Size);
                    long marks = lit.Read(officer);
                    if (OfficerFields.Personalities[restart.Slot].Read(officer) != restart.Personality || (marks >> restart.Slot & 1) == 0) continue;
                    var reset = new FieldChange(lit, marks & ~(1L << restart.Slot));
                    var holder = new RecordEdit(RecordKind.Officer, restart.Officer, officer, edit.Label, [reset]);
                    ValidateSameRecord(classes, holder, officer);
                    undo.Write(holder, officer, reset, [OfficerFields.Personalities[restart.Slot]]);
                }
            }
            return overwritten;
        }
        catch (Exception failure)
        {
            undo.Restore(failure);
            throw;
        }
    }

    private sealed class ProcessMemory(IntPtr handle) : IRecordMemory
    {
        public byte[] Read(ulong address, int length) => ReadBytes(handle, address, length);
        public void Write(ulong address, byte[] data) => WriteBytes(handle, address, data);
    }

    // Ends 설정 → 데모 플레이, which the game cannot undo itself: hands the force back to the player, then has the
    // game run its own switch to end it (GameMemory.Calls.cs), which clears the demo flag and puts the force back on
    // its screen; where that cannot be done, clears the flag itself. The state object and the force are checked
    // again right before the write, both values are read back, and the old values are restored if anything fails.
    internal static DemoEnd EndDemoPlay(GameIdentity identity, GameLayout layout, int force)
    {
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        if (layout.State == 0) throw new InvalidOperationException(L.Errors.NoStateForPlayer);
        if (force is < 1 or > MaxPlayerForce) throw new ArgumentOutOfRangeException(nameof(force));
        uint access = ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation;
        IntPtr handle = OpenProcess(access, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try
        {
            if (ReadPointers(handle, layout.Tables.State, 1)[0] != layout.State || !HasVtable(handle, layout.State, identity.Classes.World))
                throw new InvalidOperationException(L.Errors.StateMoved);
            byte[] record = ReadBytes(handle, layout.Forces[force], ForceRead);
            int ruler = BitConverter.ToUInt16(record, 0x10);
            if (BitConverter.ToUInt64(record, 0) != identity.Classes.Force || ruler is 0 or 0xFFFF)
                throw new InvalidOperationException(L.Errors.ForceGone);

            ulong forceAddress = layout.State + StatePlayerForceOffset, flagsAddress = layout.State + StateFlagsOffset;
            byte[] oldForce = ReadBytes(handle, forceAddress, 1), oldFlags = ReadBytes(handle, flagsAddress, 4);
            try
            {
                WriteBytes(handle, forceAddress, [(byte)force]);
                if (ReadBytes(handle, forceAddress, 1)[0] != force) throw new InvalidOperationException(L.Errors.AutoStopUnconfirmed);
                string? why = RunDemoSwitchOff(handle, identity);
                if (why is not null)
                {
                    // Only the demo bit changes; the game may update the other bits at the same time, so re-read right before.
                    uint flags = BitConverter.ToUInt32(ReadBytes(handle, flagsAddress, 4));
                    if ((flags & DemoPlayFlag) != 0) WriteBytes(handle, flagsAddress, BitConverter.GetBytes(flags & ~DemoPlayFlag));
                }
                if (ReadBytes(handle, forceAddress, 1)[0] != force || (BitConverter.ToUInt32(ReadBytes(handle, flagsAddress, 4)) & DemoPlayFlag) != 0)
                    throw new InvalidOperationException(L.Errors.AutoStopUnconfirmed);
                return new DemoEnd(why is null, why);
            }
            catch
            {
                try { WriteBytes(handle, forceAddress, oldForce); WriteBytes(handle, flagsAddress, oldFlags); }
                catch (Win32Exception) { /* Report the first error. */ }
                throw;
            }
        }
        finally { CloseHandle(handle); }
    }

    // The game's 국호 names by index (1~100; empty slots, which read "국호", are left out).
    internal static List<(int Index, string Name)> ReadCountryNames(GameIdentity identity, GameLayout layout)
    {
        var names = new List<(int, string)>();
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, identity.ProcessId);
        if (handle == IntPtr.Zero) return names;
        try
        {
            for (int index = 1; index < CountryCount; index++)
                if (CountryName(handle, identity, layout, index) is { Length: > 0 } name && name != "국호") names.Add((index, name));
        }
        finally { CloseHandle(handle); }
        return names;
    }

    // A force's 국호 by its index; "" for none or when the record does not check out.
    private static string CountryName(IntPtr handle, GameIdentity identity, GameLayout layout, int index)
    {
        if (index is <= 0 or >= CountryCount) return "";
        byte[] pointer = new byte[8], record = new byte[CountryRead];
        if (layout.Tables.Country == 0 || !TryRead(handle, layout.Tables.Country + (ulong)(index * 8), pointer)) return "";
        ulong address = BitConverter.ToUInt64(pointer);
        if (address == 0 || !TryRead(handle, address, record) || BitConverter.ToUInt64(record, 0) != identity.Classes.Kingdom) return "";
        return ReadName(record, CountryNameOffset, (CountryRead - CountryNameOffset) / 2);
    }

    // The names the officer card offers. A table that does not check out is left empty: the card then shows numbers.
    internal static GameCatalog ReadCatalog(GameIdentity identity, GameLayout layout)
    {
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, identity.ProcessId);
        if (handle == IntPtr.Zero) return GameCatalog.Empty;
        try
        {
            byte[]? Vector(ulong address, int size)
            {
                var bytes = new byte[size];
                return address != 0 && TryRead(handle, address, bytes) ? bytes : null;
            }
            GameName[] Read(NameTable table, Func<int, byte[], bool>? eligible = null, int minimumRead = 0)
            {
                var names = new List<GameName>();
                ulong[] pointers;
                if (table.Table(layout.Tables) == 0) return [];
                try { pointers = ReadPointers(handle, table.Table(layout.Tables), table.Count); }
                catch (Win32Exception) { return []; }
                byte[] record = new byte[Math.Max(table.Read, minimumRead)];
                for (int id = 1; id < table.Count; id++)
                {
                    if (!TryRead(handle, pointers[id], record) || BitConverter.ToUInt64(record, 0) != table.Class(identity.Classes)) return [];
                    if (eligible is not null && !eligible(id, record)) continue;
                    string name = ReadName(record, TableNameOffset, table.NameChars);
                    if (IsPlaceholder(name)) continue;
                    string description = table.Describe?.Invoke(record)
                        ?? (table.DescriptionOffset == 0 ? "" : ReadName(record, table.DescriptionOffset, (table.Read - table.DescriptionOffset) / 2));
                    names.Add(new GameName(id, name, description.Replace('，', ',')));
                }
                return names.ToArray();
            }
            return new GameCatalog(Read(PersonalityTable), Read(TacticTable), Read(PolicyTable), Read(IdeologyTable), Read(OfficeTable), Read(RankTable),
                Read(FormationTable))
            {
                RegisteredPersonalities = Read(PersonalityTable, (id, r) => RegisteredOfficerCatalog.Personality(id, r, Vector), 0xE8),
                RegisteredTactics = Read(TacticTable, (id, r) => RegisteredOfficerCatalog.Tactic(id, r, Vector), 0x90)
            };
        }
        finally { CloseHandle(handle); }
    }

    // The tables' unused records: empty, or named after their number (개성220, 전법27, 전200) or their kind (정책).
    internal static bool IsPlaceholder(string name) =>
        name.Length == 0 || name == "정책" || System.Text.RegularExpressions.Regex.IsMatch(name, @"^(개성|전법|전)\d+$");

    internal static string ReadName(byte[] data, int offset, int maxChars)
    {
        var name = new System.Text.StringBuilder();
        for (int i = 0; i < maxChars && offset + i * 2 + 1 < data.Length; i++)
        {
            char c = (char)BitConverter.ToUInt16(data, offset + i * 2);
            if (c == '\0') break;
            name.Append(c);
        }
        return name.ToString();
    }

    // The fields that say which object a record is (a city's ID, a unit's kind and commander, ...).
    private static (int Start, int Length) IdentityOf(RecordKind kind) => kind switch
    {
        RecordKind.City => (CityFields.IdOffset, 2),
        RecordKind.County => (CountyFields.NameOffset, 0x25),
        RecordKind.Building => (0x10, 3),
        RecordKind.Unit => (UnitFields.KindOffset, 4),
        RecordKind.Force => (ForceFields.RulerOffset, 2),
        RecordKind.Corps => (0x10, 4),
        RecordKind.Item => (ItemFields.NameOffset, ItemFields.NameChars * 2),
        RecordKind.Personality => (PersonalityFields.NameOffset, PersonalityFields.NameChars * 2),
        RecordKind.Gate => (CityFields.IdOffset, 2),
        RecordKind.PlayRecord => (0, PlayRecordFields.HeaderLength),
        // An AI node: its vtable, class hash and flags. A condition's args have no header of their own; AiEdits checks
        // their node when it plans.
        RecordKind.AiNode => (0, AiEdits.NodeIdentity),
        RecordKind.AiArgs => (0, 0),
        _ => (OfficerFields.IdOffset, 0x1E)
    };

    // Whether two reads of one address hold the same object (a unit's slot is used again by another unit).
    internal static bool IsSameRecord(RecordKind kind, byte[] record, byte[] other)
    {
        (int start, int length) = IdentityOf(kind);
        if (kind == RecordKind.Building && other[BuildingTypeOffset] is >= 3 and <= 17)
        {
            // Do not restore a destroyed facility or write into a slot whose owner changed since the read.
            if (FacilityFields.Durability.Read(record) == 0) return false;
            length = 4;
        }
        return record.AsSpan(start, length).SequenceEqual(other.AsSpan(start, length));
    }

    // The record must still be the same object: same type and the same identifying fields.
    private static void ValidateSameRecord(GameClasses classes, RecordEdit edit, byte[] data)
    {
        // An AI node's class is many: it must be the one read (its vtable is in its identity).
        if (edit.Kind is RecordKind.AiNode or RecordKind.AiArgs)
        {
            if (!IsSameRecord(edit.Kind, data, edit.Expected)) throw new InvalidOperationException(L.Errors.RecordMismatch(edit.Label));
            return;
        }
        ulong vtable = edit.Kind switch
        {
            RecordKind.City => classes.City,
            RecordKind.County => classes.County,
            RecordKind.Building => classes.Building,
            RecordKind.Unit => classes.Unit,
            RecordKind.Force => classes.Force,
            RecordKind.Corps => classes.Corps,
            RecordKind.Item => classes.Item,
            RecordKind.Personality => classes.Personality,
            RecordKind.Gate => classes.Gate,
            // The play record has no class; its header (with the version) stands where a vtable would.
            RecordKind.PlayRecord => PlayRecordFields.Header,
            _ => classes.Officer
        };
        if (BitConverter.ToUInt64(data, 0) != vtable || !IsSameRecord(edit.Kind, data, edit.Expected))
            throw new InvalidOperationException(edit.Kind == RecordKind.Unit
                ? L.Errors.UnitChanged(edit.Label)
                : L.Errors.RecordMismatch(edit.Label));
    }

    private static GameLayout? TryBuildLayout(IntPtr handle, GameIdentity identity, GameTables t)
    {
        if (t.City == 0 || t.County == 0 || t.Officer == 0 || t.Tile == 0 || t.Building == 0 || t.Force == 0 || t.Corps == 0) return null;
        try
        {
            ulong[] baseTable = ReadPointers(handle, t.City, BaseSlots);
            ulong[] cityTable = baseTable[..(CityCount + 1)];
            bool tribes = ContiguousCount(baseTable, CityStride) > TribeLast;
            ulong[] gates = baseTable[GateSlot..(GateSlot + GateLast - GateFirst + 1)];
            if (!gates.All(g => HasVtable(handle, g, identity.Classes.Gate))) gates = new ulong[gates.Length];
            if (ContiguousCount(cityTable, CityStride) < CityCount + 1 ||
                !VerifyCityArray(handle, cityTable[0], identity.Classes.City)) return null;

            ulong[] countyTable = ReadPointers(handle, t.County, MaxCounties);
            int counties = ContiguousCount(countyTable, CountyStride);
            ulong[] officerTable = ReadPointers(handle, t.Officer, MaxOfficers);
            int officers = ContiguousCount(officerTable, OfficerStride);
            ulong[] tileTable = ReadPointers(handle, t.Tile, TileCount);
            ulong[] buildingTable = ReadPointers(handle, t.Building, MaxBuildings);
            int buildings = ContiguousCount(buildingTable, BuildingStride);
            if (counties < 344 || officers < 1001 || buildings < 100 || ContiguousCount(tileTable, TileStride) != TileCount) return null;
            if (!HasVtable(handle, countyTable[0], identity.Classes.County) ||
                !HasVtable(handle, officerTable[0], identity.Classes.Officer) ||
                !HasVtable(handle, tileTable[0], identity.Classes.Tile) ||
                !HasVtable(handle, buildingTable[0], identity.Classes.Building)) return null;

            ulong[] forces = ReadPointers(handle, t.Force, ForceCount);
            ulong[] corps = ReadPointers(handle, t.Corps, ForceCount);
            if (!HasVtable(handle, forces[1], identity.Classes.Force) || !HasVtable(handle, corps[1], identity.Classes.Corps)) return null;

            // Units are optional: if their table does not check out, the other tables still work.
            ulong[] unitTable = t.Unit == 0 ? [0] : ReadPointers(handle, t.Unit, UnitSlots);
            bool unitsFound = ContiguousCount(unitTable, UnitStride) == UnitSlots && HasVtable(handle, unitTable[0], identity.Classes.Unit);
            // The state object is optional too: without it only the AUTO(데모) 플레이 switch is unavailable.
            ulong state = t.State == 0 ? 0 : ReadPointers(handle, t.State, 1)[0];
            if (!HasVtable(handle, state, identity.Classes.World)) state = 0;
            // And the items (명품): without them the officer card offers none.
            ulong[] itemTable = t.Item == 0 ? [0] : ReadPointers(handle, t.Item, ItemSlots);
            bool itemsFound = ContiguousCount(itemTable, ItemStride) == ItemSlots && HasVtable(handle, itemTable[0], identity.Classes.Item);

            return new GameLayout(t, cityTable[0], countyTable[0], counties, officerTable[0], officers,
                tileTable[0], buildingTable[0], buildings, forces, corps, unitsFound ? unitTable[0] : 0, unitsFound ? UnitSlots : 0, state,
                tribes, gates, itemsFound ? itemTable[0] : 0, itemsFound ? ItemSlots : 0);
        }
        catch (Win32Exception) { return null; }
    }

    private static ulong[] ReadPointers(IntPtr handle, ulong address, int count)
    {
        byte[] data = ReadBytes(handle, address, count * 8);
        var result = new ulong[count];
        for (int i = 0; i < count; i++) result[i] = BitConverter.ToUInt64(data, i * 8);
        return result;
    }

    private static int ContiguousCount(ulong[] table, int stride)
    {
        int count = 0;
        while (count < table.Length && table[0] != 0 && table[count] == table[0] + (ulong)(count * stride)) count++;
        return count;
    }

    private static bool HasVtable(IntPtr handle, ulong address, ulong vtable)
    {
        byte[] value = new byte[8];
        return address != 0 && TryRead(handle, address, value) && BitConverter.ToUInt64(value) == vtable;
    }

    private static bool VerifyCityArray(IntPtr handle, ulong arrayBase, ulong vtable)
    {
        try
        {
            byte[] records = ReadBytes(handle, arrayBase, (CityCount + 1) * CityStride);
            for (int id = 0; id <= CityCount; id++)
            {
                int start = id * CityStride;
                if (BitConverter.ToUInt64(records, start) != vtable ||
                    BitConverter.ToUInt16(records, start + CityFields.IdOffset) != id) return false;
            }
            return true;
        }
        catch (Win32Exception) { return false; }
    }

    private static void ValidateCity(byte[] record, ulong vtable, int cityId)
    {
        if (BitConverter.ToUInt64(record, 0) != vtable || BitConverter.ToUInt16(record, CityFields.IdOffset) != cityId)
            throw new InvalidOperationException(L.Errors.CityMismatch);
    }

    private static void RequireVtable(byte[] record, ulong vtable, string what)
    {
        if (BitConverter.ToUInt64(record, 0) != vtable)
            throw new InvalidOperationException(L.Errors.KindMismatch(what));
    }

    private static byte[] Slice(byte[] all, int index, int stride) => all.AsSpan(index * stride, stride).ToArray();

    private static bool IsReadable(uint protection)
    {
        if ((protection & 0x101) != 0) return false;
        return (protection & 0xFF) is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;
    }

    private static IntPtr OpenRead(int processId)
    {
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.CannotRead);
        return handle;
    }

    private static bool TryRead(IntPtr handle, ulong address, byte[] buffer) => TryRead(handle, address, buffer, buffer.Length);

    // A rented buffer can exceed the requested range; its spare capacity must never extend the process read.
    private static bool TryRead(IntPtr handle, ulong address, byte[] buffer, int length) =>
        ReadProcessMemory(handle, (IntPtr)unchecked((long)address), buffer, (UIntPtr)(uint)length, out UIntPtr read) &&
        read.ToUInt64() == (ulong)length;

    private static byte[] ReadBytes(IntPtr handle, ulong address, int length)
    {
        byte[] data = new byte[length];
        if (!TryRead(handle, address, data)) throw AccessError(L.Errors.ReadFailed);
        return data;
    }

    private static void WriteBytes(IntPtr handle, ulong address, byte[] data)
    {
        bool success = WriteProcessMemory(handle, (IntPtr)unchecked((long)address), data,
            (UIntPtr)(uint)data.Length, out UIntPtr bytesWritten);
        if (!success || bytesWritten.ToUInt64() != (ulong)data.Length)
            throw AccessError(L.Errors.WriteFailed);
    }

    private static Exception AccessError(string message) =>
        new Win32Exception(Marshal.GetLastWin32Error(), message);
}

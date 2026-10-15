using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// The game's rules, written from its own code (SAN14CE_KO, read from memory and checked against that code run in an
// emulator: source/Reverse). Each formula names the function it mirrors by RVA and reads the game's objects at the
// offsets that function reads, so it can be held against the game line by line.

// A game object's bytes, read at the offsets the game's code reads them.
internal sealed class Rec(int index, byte[] bytes, int table = 0, ulong nativeAddress = 0)
{
    // Its place in the game's array: the code works it out from the pointer ((p - first) / size).
    internal int Index { get; } = index;
    // The table it belongs to (Tables), which picks the class's own validity check (Objects.Valid).
    internal int Table { get; } = table;
    // Native pointer order matters when damage-map entries from different base tables share an object number.
    internal ulong NativeAddress { get; } = nativeAddress;
    internal byte[] Bytes { get; } = bytes;
    // CPostData +0x40/+0x48: a vector of dwords outside the record (the office's ability bonuses, encoded).
    internal byte[]? Vector40 { get; set; }
    // CObjectKindData +0x50/+0x58: nineteen encoded facility definition values.
    internal byte[]? Vector50 { get; set; }
    // CPolicyEffectData +0x98/+0xA0: its value at each of ten levels, a dword each (low word encoded).
    internal byte[]? Vector98 { get; set; }
    // CPersonalityData +0xC8/+0xD0: its flags, kept encoded in the first entry's low word.
    internal byte[]? VectorC8 { get; set; }
    // CFormationData +0xA8/+0xB0: its values (attack, siege, defense…), kept encoded.
    internal byte[]? VectorA8 { get; set; }
    // CAdvantageData +0xA0/+0xA8: its requirement and two effects (kind, value), kept encoded.
    internal byte[]? VectorA0 { get; set; }
    // CTacticsData +0x70/+0x78: flags and the two effects' kind, range, value and level, encoded.
    internal byte[]? Vector70 { get; set; }
    // CPersonData +0x1D0/+0x1D8: the county IDs governed by this officer; count at +0x1E8.
    internal byte[]? Vector1D0 { get; set; }

    internal byte U8(int o) => Bytes[o];
    internal sbyte I8(int o) => (sbyte)Bytes[o];
    internal ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(o));
    internal short I16(int o) => BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(o));
    internal int I32(int o) => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(o));
    internal uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(o));
    internal float F32(int o) => BinaryPrimitives.ReadSingleLittleEndian(Bytes.AsSpan(o));
}

// The objects the formulas read. A table is named by the offset of its pointer list from the game's world pointer
// (exe + 0x277DF18, "raw"), as the code addresses it: [raw + 0xDF30 + i * 8] is Table(Tables.Forces)[i].
internal sealed class World(IReadOnlyDictionary<int, Rec[]> tables, Rec settings, IReadOnlyDictionary<int, List<Rec?[]>> lists,
    int[] byId, Rec worldState, Dictionary<string, int> flags)
{
    // The commands under way (CCmdAction objects): each its kind (vtable +0x98) and the IDs of the officers on it.
    internal RuntimeRuleSettings RuntimeRules { get; set; } = RuntimeRuleSettings.Original;
    internal List<(int Kind, List<int> Officers)> CmdActions { get; set; } = [];

    internal Rec[] Table(int raw) => tables[raw];

    // [raw + 0x158 + id * 8]: an officer by ID (ID 6000 at most; the code takes officer 0 above that), null for none.
    internal Rec? OfficerById(int id) => id < 0x1771 ? byId[id] is var i and >= 0 ? tables[Tables.Officers][i] : null : tables[Tables.Officers][0];

    // CWorldData ([raw + 0x871E8]): the date, the player's force, the game's mode; the code tests the pointer.
    internal Rec WorldState { get; } = worldState;
    internal bool HasWorldState => WorldState.Bytes.Length > 0x460;

    // exe globals the formulas read (world.py EXE_VALUES): a value, or whether it is set.
    internal int Value(string name) => flags.TryGetValue(name, out int v) ? v : 0;
    internal bool Flag(string name) => Value(name) != 0;
    internal void SetValue(string name, int value) => flags[name] = value;

    // A linked list the game keeps, named by the raw offset of its head: its elements in order, each the records
    // its fields point at (null for none). Lists names them.
    internal List<Rec?[]> List(int head) => lists[head];

    // The options object (exe + 0x1A46CA0, what 0x3E9C90 returns), from its start.
    internal Rec Settings { get; } = settings;

    // The code's bounded lookup: entry i when i <= max, else entry 0 (the table's "none").
    internal Rec Entry(int raw, int i, int max) => tables[raw][i <= max ? i : 0];
}

// Raw offsets of the tables (the code's; GameMemory's base is raw + 0x290, so its offsets are these less 0x290).
internal static class Tables
{
    internal const int Officers = 0x73A50;   // CPersonData, 0x1F0 each, contiguous
    internal const int Forces = 0xDF30;      // CForceData, 0x268, by force number (0..0x33)
    internal const int Corps = 0xE0D0;       // CDistrictData, 0x30, by corps number (0..0x33)
    internal const int Items = 0x77B18;      // CItemData, 0x80
    internal const int Ranks = 0x78490;      // CRankData (작위), 0xB8, 0..12
    internal const int Offices = 0x784F8;    // CPostData (관직), 0x58, 0..255
    internal const int Doctrine = 0x85F20;   // 주의 panel entries, 0x50, 0..0x4F
    internal const int Units = 0x7E838;      // CArmyUnitData, 0x200, 0..500
    internal const int Troops = 0x7F8D8;     // CTroopsData, 0x40, 0..500
    internal const int CautionTroops = 0x80BA8; // CCautionTroopsData, 0x48, 0..500
    internal const int CautionUnits = 0x81B50; // CCautionArmyUnitData, 0x18, 0..1000
    internal const int Tiles = 0xE270;       // CHexData, 0x20, 220 x 220 (0..0xBD0F)
    internal const int Terrains = 0x73860;   // CTerrainData, 0x48, 0..30
    internal const int Formations = 0x77428; // CFormationData (진형), 0xC8, 0..20
    internal const int Policies = 0x78D50;   // CPolicyData (정책), 0xA8, 0..120
    internal const int PolicyEffects = 0x80880; // CPolicyEffectData, 0xB0, 0..100
    internal const int Credits = 0x85DF0;    // CCreditEffectData, 0x18, 0..6
    internal const int Scenery = 0x85A90;    // CSceneryData, 0x78, 0..100
    internal const int History = 0x79E90;    // CHistoryData, 0x140, 0..2000
    internal const int Personalities = 0x7DD18; // CPersonalityData (개성), 0xE8, 0..355
    internal const int Counties = 0x6CAF0;   // CAreaData (군), 0x98, 0..500
    internal const int ObjectKinds = 0x73958; // the building kinds (a CObjectData's +0x12), 0x68, 0..30
    internal const int Clans = 0x78CF8;      // the officer clans (+0x14C), 0x78, 0..6
    // Not off the world: the ability levels hang off exe + 0x277DF00 (+0x60 the pointer list); world.py EXE_TABLES
    // names the table by that sum.
    internal const int Levels = 0x277DF60;   // 0x28, 0..100: +0x10 + 4k the exp ability k needs to leave the level
    internal const int Cities = 0xDD38;      // CCityData, 0x168, 0..0x33
    internal const int Gates = 0xDED8;       // CGateData, 0xC0, 0..10
    internal const int Buildings = 0x6DA98;  // CObjectData, 0x20, 0..3000
    internal const int States = 0xDC80;      // CStateData (주), 0x38, 0..22
    internal const int Advantages = 0x85990; // CAdvantageData (지역 이점), 0xB8, 0..17
    internal const int Tactics = 0x774D0;    // CTacticsData (전법), 0x90, 0..200
    internal const int TacticReferences = 0x79118; // 0x58, 0..30; tactic byte +0x52, validity byte +0x53
}

// Raw offsets of the heads of the game's linked lists.
internal static class Lists
{
    internal const int UnitList = 0x60;
    internal const int ForceList = 0xC0;
    internal const int CorpsList = 0xD0;
    internal const int CautionTroopList = 0x120;
    // The items with an owner, kept by the game as ownership changes: an owner written into an item (+0x4A) alone
    // does not put it here, and the ability bonus and 옥새 only see items here.
    internal const int OwnedItems = 0x110;
    // The bases, cities then gates (World_GetBase 0x32C130 looks a base up here by its +0x4E).
    internal const int Bases = 0x80;
    // Legacy formula names: units pending final removal (0x31ECC0 -> 0x28E290), and deferred destruction/capture
    // requests (target unit, target base, attacker unit). 0x233440 inspects both lists.
    internal const int EngagedUnits = 0x70;
    internal const int Engagements = 0x140;
    // The cities (0x2AAAA0 counts a force's cities in a 주 here) and the officers the base iterator walks (0x234810).
    internal const int CityList = 0x90;
    internal const int GateList = 0xA0;
    internal const int CountyList = 0xB0;
    internal const int Facilities = 0xE0;
    internal const int OfficerList = 0x100;
    // Buildings that take no siege damage (0x233590 asks it); empty in every snapshot so far.
    internal const int SiegeExempt = 0xF0;
}

// A constant of the game's rule block in .data (0x1A47C00..0x1A4A800), where the code reads it.
[AttributeUsage(AttributeTargets.Field)]
internal sealed class RvaAttribute(int rva) : Attribute
{
    internal int Rva { get; } = rva;
}

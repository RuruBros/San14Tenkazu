using System.Buffers.Binary;
using System.Globalization;

namespace San14Tenkazu;

internal enum FieldKind { Byte, SByte, UInt16, Int16, Int32, UInt32 }

// Which game record a field lives in. A city's real durability sits in its castle building. AiNode is a node of the
// AI's trees, AiArgs the args of one of a condition node's terms (AiEdits).
internal enum RecordKind { City, County, Building, Officer, Unit, Force, Item, Gate, Personality, PlayRecord, AiNode, AiArgs, Formation, Tactic, Policy, Office, Rank, Doctrine, Corps }

internal sealed record FieldDef(string Label, RecordKind Record, int Offset, FieldKind Kind, long Min, long Max, string Hint = "")
{
    internal int Size => Kind switch { FieldKind.Byte or FieldKind.SByte => 1, FieldKind.UInt16 or FieldKind.Int16 => 2, _ => 4 };

    internal long Read(ReadOnlySpan<byte> record) => Kind switch
    {
        FieldKind.Byte => record[Offset],
        FieldKind.SByte => (sbyte)record[Offset],
        FieldKind.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(record[Offset..]),
        FieldKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(record[Offset..]),
        FieldKind.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(record[Offset..]),
        _ => BinaryPrimitives.ReadInt32LittleEndian(record[Offset..])
    };

    // What to type when a value is out of range.
    internal string RangeMessage => L.Fields.RangeError(Label, Min, Max);

    internal byte[] Encode(long value)
    {
        if (value < Min || value > Max)
            throw new ArgumentOutOfRangeException(nameof(value), RangeMessage);
        byte[] data = new byte[Size];
        switch (Kind)
        {
            case FieldKind.Byte: data[0] = (byte)value; break;
            case FieldKind.SByte: data[0] = (byte)(sbyte)value; break;
            case FieldKind.UInt16: BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)value); break;
            case FieldKind.Int16: BinaryPrimitives.WriteInt16LittleEndian(data, (short)value); break;
            case FieldKind.UInt32: BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)value); break;
            default: BinaryPrimitives.WriteInt32LittleEndian(data, (int)value); break;
        }
        return data;
    }
}

internal readonly record struct FieldChange(FieldDef Field, long Value);

// Facilities use Building_SetDurability's 9999 cap (0x1F42B0). Zero also needs the game's destruction
// lifecycle (0x294B90), so this editor changes the durability of a standing facility, starting at 1.
internal static class FacilityFields
{
    internal static readonly FieldDef Durability = new(L.Fields.Durability, RecordKind.Building, GameBuildFacts.Fields.FacilityFieldsDurabilityOffset, FieldKind.UInt16, 1, 9999, L.Facility.DurabilityHint);
}

// City record (0x168 bytes). Verified against the running game and public PK tables
// (fearlessrevolution RTK14 PK tables, github.com/PaysageDVII7/San14PaysageMod).
internal static class CityFields
{
    internal static readonly FieldDef Gold = new(L.Fields.Gold, RecordKind.City, GameBuildFacts.Fields.CityFieldsGoldOffset, FieldKind.Int32, 0, int.MaxValue, CapsHint(c => c.Gold));
    internal static readonly FieldDef Food = new(L.Fields.Food, RecordKind.City, GameBuildFacts.Fields.CityFieldsFoodOffset, FieldKind.Int32, 0, int.MaxValue, CapsHint(c => c.Food));
    internal static readonly FieldDef Troops = new(L.Fields.Troops, RecordKind.City, GameBuildFacts.Fields.CityFieldsTroopsOffset, FieldKind.Int32, 0, int.MaxValue, CapsHint(c => c.Troops));
    internal static readonly FieldDef Wounded = new(L.Fields.Wounded, RecordKind.City, GameBuildFacts.Fields.CityFieldsWoundedOffset, FieldKind.Int32, 0, int.MaxValue, L.Fields.WoundedHint);
    internal static readonly FieldDef Morale = new(L.Fields.Morale, RecordKind.City, GameBuildFacts.Fields.CityFieldsMoraleOffset, FieldKind.UInt16, 0, 255, L.Fields.CityMoraleHint);
    internal static readonly FieldDef Durability = new(L.Fields.Durability, RecordKind.Building, GameBuildFacts.Fields.CityFieldsDurabilityOffset, FieldKind.UInt16, 0, 60_000, L.Fields.DurabilityHint);
    internal static readonly FieldDef Scale = new(L.Fields.Scale, RecordKind.City, GameBuildFacts.Fields.CityFieldsScaleOffset, FieldKind.Byte, 1, 4, L.Fields.ScaleHint);

    // +0x46 is left out: it held its scale's durability cap in 44 of 46 cities, but the game does not take its full
    // durability from it (한중 held 2,000 at 중 while the game showed 3,100 / 3,100). A castle's full durability is its
    // scale's cap plus a bonus the same for every city of a force (+0, +100, +150, +200, +400 read 2026-10-03).
    internal static readonly FieldDef[] All = [Gold, Food, Troops, Wounded, Morale, Durability, Scale];

    // 태수: the officer who governs a city (0 none, which the game leaves at times while officers are present), and the
    // same field of a gate's record.
    internal static readonly FieldDef Governor = new(L.Fields.Governor, RecordKind.City, GameBuildFacts.Fields.CityFieldsGovernorOffset, FieldKind.UInt16, 0, ushort.MaxValue);
    internal static readonly FieldDef GateGovernor = new(L.Fields.Governor, RecordKind.Gate, GameBuildFacts.Fields.CityFieldsGateGovernorOffset, FieldKind.UInt16, 0, ushort.MaxValue);
    internal const int NameOffset = 0x12;
    internal const int IdOffset = 0x4E;
    internal const int GovernorOffset = 0x2E;
    internal const int CorpsOffset = 0x30;

    private static readonly string[] ScaleNames = ["?", L.Terms.ScaleSmall, L.Terms.ScaleMedium, L.Terms.ScaleLarge, L.Terms.ScaleHuge];

    internal static string ScaleName(long scale) => ScaleNames[Math.Clamp(scale, 0, ScaleNames.Length - 1)];

    internal static (int Gold, int Food, int Troops, int Durability)? Caps(long scale) => scale switch
    {
        1 => (50_000, 100_000, 50_000, 2_000),
        2 => (100_000, 200_000, 70_000, 3_000),
        3 => (150_000, 300_000, 100_000, 4_000),
        4 => (200_000, 400_000, 150_000, 5_000),
        _ => null
    };

    // "규모별 상한 50,000/100,000/150,000/200,000": one of the caps for each scale, 소 to 거대.
    private static string CapsHint(Func<(int Gold, int Food, int Troops, int Durability), int> pick) =>
        L.Fields.CapsHint(string.Join("/", Enumerable.Range(1, 4).Select(scale => pick(Caps(scale)!.Value).ToString("N0", CultureInfo.CurrentCulture))));
}

// Gate (관문) record (0xC0 bytes, GateRecord): its troops, morale and base durability sit where a city's do (its gold,
// food and wounded stay 0: all ten gates read 2026-10-03), its 태수 is CityFields.GateGovernor, and its real
// durability is in its building, as a city's is in its castle. Every gate not under attack held durability equal to
// its base durability (5,000~8,000).
internal static class GateFields
{
    internal static readonly FieldDef Troops = new(L.Fields.Troops, RecordKind.Gate, GameBuildFacts.Fields.GateFieldsTroopsOffset, FieldKind.Int32, 0, int.MaxValue);
    internal static readonly FieldDef Morale = new(L.Fields.Morale, RecordKind.Gate, GameBuildFacts.Fields.GateFieldsMoraleOffset, FieldKind.UInt16, 0, 255, L.Fields.GateMoraleHint);
    internal static readonly FieldDef Durability = new(L.Fields.Durability, RecordKind.Building, GameBuildFacts.Fields.GateFieldsDurabilityOffset, FieldKind.UInt16, 0, 60_000, L.Fields.GateDurabilityHint);
    internal static readonly FieldDef BaseDurability = new(L.Fields.BaseDurability, RecordKind.Gate, GameBuildFacts.Fields.GateFieldsBaseDurabilityOffset, FieldKind.UInt16, 0, ushort.MaxValue, L.Fields.GateBaseDurabilityHint);

    internal static readonly FieldDef[] All = [Troops, Morale, Durability, BaseDurability];
}

// County (군) record (0x98 bytes). Each city owns several counties; development values
// run 0~1000 with levels at 100/300/600/1000.
internal static class CountyFields
{
    internal static readonly FieldDef Population = new(L.Fields.Population, RecordKind.County, GameBuildFacts.Fields.CountyFieldsPopulationOffset, FieldKind.UInt16, 0, ushort.MaxValue, L.Fields.PopulationHint);
    internal static readonly FieldDef PublicOrder = new(L.Fields.PublicOrder, RecordKind.County, GameBuildFacts.Fields.CountyFieldsPublicOrderOffset, FieldKind.Byte, 0, 100);
    internal static readonly FieldDef Commerce = new(L.Fields.Commerce, RecordKind.County, GameBuildFacts.Fields.CountyFieldsCommerceOffset, FieldKind.UInt16, 0, 1000, L.Fields.DevelopmentHint);
    internal static readonly FieldDef Agriculture = new(L.Fields.Agriculture, RecordKind.County, GameBuildFacts.Fields.CountyFieldsAgricultureOffset, FieldKind.UInt16, 0, 1000, L.Fields.DevelopmentHint);
    internal static readonly FieldDef Barracks = new(L.Fields.Barracks, RecordKind.County, GameBuildFacts.Fields.CountyFieldsBarracksOffset, FieldKind.UInt16, 0, 1000, L.Fields.DevelopmentHint);

    internal static readonly FieldDef[] All = [Population, PublicOrder, Commerce, Agriculture, Barracks];
    internal static readonly FieldDef[] Development = [Commerce, Agriculture, Barracks];

    internal const int NameOffset = 0x10;
    internal const int HomeCityOffset = 0x34;
    internal const int CurrentCityOffset = 0x35;
    internal const int CenterTileOffset = 0x3A;   // CAreaData's centre hex, used by CGateData's owner getter

    // Development where the levels 2~5 begin.
    internal static readonly int[] LevelSteps = [100, 300, 600, 1000];

    internal static int Level(long development) => 1 + LevelSteps.Count(step => development >= step);
}

// Officer (장수) record (0x1F0 bytes). The Korean build stores names in a shorter block than
// the Chinese PK build, so every field after the names sits 0x38 bytes earlier.
internal static class OfficerFields
{
    internal static readonly FieldDef Loyalty = new(L.Fields.Loyalty, RecordKind.Officer, GameBuildFacts.Fields.OfficerFieldsLoyaltyOffset, FieldKind.Byte, 0, 255, L.Fields.LoyaltyHint);
    // Officer_AddMerit (0x169530) reads/writes this unsigned word and caps it at 0xFFFF.
    internal static readonly FieldDef Merit = new(L.Fields.Merit, RecordKind.Officer, GameBuildFacts.Fields.OfficerFieldsMeritOffset, FieldKind.UInt16, 0, ushort.MaxValue);
    internal static readonly FieldDef Leadership = new(L.Fields.Leadership, RecordKind.Officer, GameBuildFacts.Fields.OfficerFieldsLeadershipOffset, FieldKind.Byte, 0, 255, L.Fields.AbilityHint);
    internal static readonly FieldDef Strength = new(L.Fields.Strength, RecordKind.Officer, GameBuildFacts.Fields.OfficerFieldsStrengthOffset, FieldKind.Byte, 0, 255, L.Fields.AbilityHint);
    internal static readonly FieldDef Intelligence = new(L.Fields.Intelligence, RecordKind.Officer, GameBuildFacts.Fields.OfficerFieldsIntelligenceOffset, FieldKind.Byte, 0, 255, L.Fields.AbilityHint);
    internal static readonly FieldDef Politics = new(L.Fields.Politics, RecordKind.Officer, GameBuildFacts.Fields.OfficerFieldsPoliticsOffset, FieldKind.Byte, 0, 255, L.Fields.AbilityHint);
    internal static readonly FieldDef Charm = new(L.Fields.Charm, RecordKind.Officer, GameBuildFacts.Fields.OfficerFieldsCharmOffset, FieldKind.Byte, 0, 255, L.Fields.AbilityHint);
    internal static readonly FieldDef Birth = new(L.Fields.Birth, RecordKind.Officer, GameBuildFacts.Fields.OfficerFieldsBirthOffset, FieldKind.UInt16, 1, 999);
    // The year an officer comes into play, right before the birth year. Read on 2026-10-02 (a game in 251):
    // every 미등장 officer held a later year and nearly every officer in play an earlier one, 19 years after
    // the birth year at the median.
    internal static readonly FieldDef Debut = new(L.Fields.Debut, RecordKind.Officer, GameBuildFacts.Fields.OfficerFieldsDebutOffset, FieldKind.UInt16, 1, 999, L.Fields.DebutHint);
    internal static readonly FieldDef Death = new(L.Fields.Death, RecordKind.Officer, GameBuildFacts.Fields.OfficerFieldsDeathOffset, FieldKind.UInt16, 1, 999, L.Fields.DeathHint);

    internal static readonly FieldDef[] Abilities = [Leadership, Strength, Intelligence, Politics, Charm];
    internal static readonly FieldDef[] Years = [Birth, Debut, Death];
    internal static readonly FieldDef[] All = [Loyalty, Merit, Leadership, Strength, Intelligence, Politics, Charm, Birth, Debut, Death];

    // The experience behind each ability (+0x104, four bytes each, in the abilities' order). An ability is
    // the point its experience has reached: at value v the experience is v³ + 5000·v + 5000·max(v − 70, 0)
    // (v² for 매력) and it grows from there towards the next point (the formula is from
    // github.com/PaysageDVII7/San14PaysageMod; all 4,305 abilities of the officers in play fitted it on
    // 2026-10-02). So an edited ability is written with the experience of its new value (ExperienceFor).
    private static readonly FieldDef[] Experience = Abilities
        .Select((ability, index) => new FieldDef(L.Fields.Experience(ability.Label), RecordKind.Officer, 0x104 + index * 4, FieldKind.Int32, 0, int.MaxValue)).ToArray();

    private static long ExperienceAt(long value, bool charm, bool growth = false) => AbilityProgress.Threshold(value, charm, growth);

    // The experience to write with an ability's new value: that value's own, plus what the officer had
    // gained towards his next point (kept below the point after the new value).
    internal static FieldChange ExperienceFor(ReadOnlySpan<byte> record, FieldDef ability, long value, bool growth = false)
    {
        FieldDef experience = Experience[Array.IndexOf(Abilities, ability)];
        bool charm = ability == Charm;
        if (growth && value > 200) throw new ArgumentOutOfRangeException(nameof(value), "200 성장 모드에서는 능력치를 200까지 입력할 수 있습니다.");
        long gained = Math.Max(0, experience.Read(record) - ExperienceAt(ability.Read(record), charm, growth));
        long room = ExperienceAt(value + 1, charm, growth) - ExperienceAt(value, charm, growth) - 1;
        return new FieldChange(experience, ExperienceAt(value, charm, growth) + Math.Min(gained, room));
    }

    // Whether an ability is the point its experience has reached (GameChecks: the record is read where it should be).
    internal static bool FitsExperience(ReadOnlySpan<byte> record, FieldDef ability, bool growth = false)
    {
        bool charm = ability == Charm;
        long value = ability.Read(record), experience = Experience[Array.IndexOf(Abilities, ability)].Read(record);
        return experience >= ExperienceAt(value, charm, growth) && (growth && value == 200 || experience < ExperienceAt(value + 1, charm, growth));
    }

    internal static bool IsExperience(FieldDef field) => Experience.Contains(field);

    // What the officer card (OfficerDetailCard) edits. Found on 2026-10-02 by reading all 1,001 officers of a
    // running game against the game's own tables and what the game shows (유비 왕도, 조조 패도, 여포 천하무쌍,
    // 관우·장비·유비 one 의형제 group), and written and held for 4 s on a free officer.
    internal static readonly FieldDef Gender = new(L.Fields.Gender, RecordKind.Officer, GameBuildFacts.Fields.isGenderOffset, FieldKind.Byte, 0, 1, L.Fields.GenderHint);
    // An officer ID is not his slot: the DLC's and the player's own officers have IDs from 5000 (229 of 1,435
    // officers over 4095, 5,152 the largest, read on 2026-10-02); 0xFFFF is the game's "nobody".
    internal const int IdMax = ushort.MaxValue - 1;

    // 배우자: an officer ID, kept in both spouses (all but one pair of 1,001 officers pointed back).
    internal static readonly FieldDef Spouse = new(L.Fields.Spouse, RecordKind.Officer, GameBuildFacts.Fields.isSpouseOffset, FieldKind.UInt16, 0, IdMax);
    // 의형제: the group's ID, the ID of one of its officers (유비's for 유비·관우·장비), the same in every member.
    internal static readonly FieldDef Sworn = new(L.Fields.Sworn, RecordKind.Officer, GameBuildFacts.Fields.isSwornOffset, FieldKind.UInt16, 0, IdMax);
    internal static readonly FieldDef Compatibility = new(L.Fields.Compatibility, RecordKind.Officer, GameBuildFacts.Fields.isCompatibilityOffset, FieldKind.UInt16, 0, 149, L.Fields.CompatibilityHint);
    // 진형: the formations he can lead a unit in, bit k for the formation table's k, as the game tests it (0x232350,
    // read 2026-10-03; the card took it for 공적 before). 1,851 officers held bits 1~10 and the tribes' 사이 (14), which
    // the game grants only to the tribe officers (IsTribeOfficer) whatever the bit says.
    internal static readonly FieldDef Formations = new(L.Fields.Formations, RecordKind.Officer, GameBuildFacts.Fields.isFormationsOffset, FieldKind.Int32, 0, int.MaxValue);
    internal const int TribeOfficerFirst = 4101, TribeOfficerLast = 4200;

    internal static bool IsTribeOfficer(int id) => id is >= TribeOfficerFirst and <= TribeOfficerLast;

    internal static bool HasFormation(long formations, int formation) => formation is > 0 and < 32 && (formations >> formation & 1) != 0;

    // The formations the officer card offers him: the land ones (UnitFields.LandFormations), 사이 only to a tribe officer.
    internal static IEnumerable<int> FormationsFor(int id) =>
        UnitFields.LandFormations.Where(f => f != UnitFields.TribeFormation || IsTribeOfficer(id));
    // 고유 전법: an index into the tactics (31~170 are the officers' own, 1~26 the common ones).
    internal static readonly FieldDef UniqueTactic = new(L.Fields.UniqueTactic, RecordKind.Officer, GameBuildFacts.Fields.isUniqueTacticOffset, FieldKind.Byte, 0, 200);
    // 주의 (1 왕도 · 2 패도 · 3 아도 · 4 할거 · 5 명리 · 6 예교) and the officer's 정책 (an index into the policies).
    internal static readonly FieldDef Ideology = new(L.Fields.Ideology, RecordKind.Officer, GameBuildFacts.Fields.isIdeologyOffset, FieldKind.Byte, 1, 6);
    internal static readonly FieldDef Policy = new(L.Fields.Policy, RecordKind.Officer, GameBuildFacts.Fields.isPolicyOffset, FieldKind.Byte, 0, 120);

    // 개성: nine slots of personality IDs from +0x118, filled from the first; the game's officers use up to five.
    internal static readonly FieldDef[] Personalities = Enumerable.Range(0, 9)
        .Select(i => new FieldDef(L.Fields.PersonalitySlot(i + 1), RecordKind.Officer, 0x118 + i * 2, FieldKind.UInt16, 0, 355)).ToArray();
    // What each slot's 개성 has going in battle (read from the game's code, 2026-10-03): a bit a slot for "in effect
    // now" for the unit he leads, lit and cleared by the game within seconds, and for the timed ones (일심, 무황) the
    // count left before the effect ends. Both are saved. The game clears a slot's two when it changes the slot's 개성
    // (OfficerEdits.SlotStateCleared).
    internal static readonly FieldDef PersonalitiesInEffect = new(L.Fields.PersonalitiesInEffect, RecordKind.Officer, GameBuildFacts.Fields.isPersonalitiesInEffectOffset, FieldKind.UInt16, 0, ushort.MaxValue);
    internal static readonly FieldDef[] PersonalityTimeLeft = Enumerable.Range(0, 9)
        .Select(i => new FieldDef(L.Fields.PersonalityTimeLeft(i + 1), RecordKind.Officer, 0x186 + i * 2, FieldKind.UInt16, 0, 720)).ToArray();
    // 습득 전법: up to five tactics, filled from the first: mostly of the 26 common ones, but officers learn others as
    // the game goes on (137 for fifteen officers by 211), so any of the game's tactics (0~200).
    internal static readonly FieldDef[] Tactics = Enumerable.Range(0, 5)
        .Select(i => new FieldDef(L.Fields.TacticSlot(i + 1), RecordKind.Officer, 0x131 + i, FieldKind.Byte, 0, 200)).ToArray();
    // 친애 and 혐오: up to eight officer IDs each, filled from the first, mostly in ascending order. Neither is
    // kept on the other side (434 of the 친애 ties had no tie back).
    internal static readonly FieldDef[] Likes = Enumerable.Range(0, 8)
        .Select(i => new FieldDef(L.Fields.LikeSlot(i + 1), RecordKind.Officer, 0xC0 + i * 2, FieldKind.UInt16, 0, IdMax)).ToArray();
    internal static readonly FieldDef[] Dislikes = Enumerable.Range(0, 8)
        .Select(i => new FieldDef(L.Fields.DislikeSlot(i + 1), RecordKind.Officer, 0xD0 + i * 2, FieldKind.UInt16, 0, IdMax)).ToArray();

    internal const int CommonTactics = 26;

    // Whom an officer serves and where (found 2026-10-02 by watching the game's own moves over 20 months: 재야 officers
    // recruited, officers dismissed, captured, released and recruited from prison). +0xE4 is where he is now: his base
    // while he is in it, other values while he marches, travels or is out on a task.
    internal static readonly FieldDef CorpsField = new(L.Fields.Corps, RecordKind.Officer, GameBuildFacts.Fields.isCorpsFieldOffset, FieldKind.Byte, 0, 255);
    internal static readonly FieldDef BaseField = new(L.Fields.Base, RecordKind.Officer, GameBuildFacts.Fields.isBaseFieldOffset, FieldKind.UInt16, 0, 255);
    internal static readonly FieldDef PlaceField = new(L.Fields.Place, RecordKind.Officer, GameBuildFacts.Fields.isPlaceFieldOffset, FieldKind.UInt16, 0, ushort.MaxValue);
    internal static readonly FieldDef StatusField = new(L.Fields.Status, RecordKind.Officer, GameBuildFacts.Fields.isStatusFieldOffset, FieldKind.Byte, 1, 9);
    // The ruler he left or who let him go, and the months he will not serve that ruler (12 after a dismissal, 2 after
    // a release from prison).
    internal static readonly FieldDef FormerLord = new(L.Fields.FormerLord, RecordKind.Officer, GameBuildFacts.Fields.isFormerLordOffset, FieldKind.UInt16, 0, ushort.MaxValue);
    internal static readonly FieldDef FormerLordMonths = new(L.Fields.FormerLordMonths, RecordKind.Officer, GameBuildFacts.Fields.isFormerLordMonthsOffset, FieldKind.Byte, 0, 255);
    // A prisoner's own force, and the months he has been held.
    internal static readonly FieldDef PrisonerOf = new(L.Fields.PrisonerOf, RecordKind.Officer, GameBuildFacts.Fields.isPrisonerOfOffset, FieldKind.Byte, 0, 255);
    internal static readonly FieldDef Captivity = new(L.Fields.Captivity, RecordKind.Officer, GameBuildFacts.Fields.isCaptivityOffset, FieldKind.Byte, 0, 255);
    // The year he took service (0 for none), and two values the game resets when he takes or leaves service
    // (+0x1BC 2 for a 재야 officer recruited; their meaning is not known).
    internal static readonly FieldDef JoinedYear = new(L.Fields.JoinedYear, RecordKind.Officer, GameBuildFacts.Fields.isJoinedYearOffset, FieldKind.UInt16, 0, 999);
    internal static readonly FieldDef Service = new(L.Fields.Service, RecordKind.Officer, GameBuildFacts.Fields.isServiceOffset, FieldKind.Byte, 0, 255);
    internal static readonly FieldDef ServiceValue = new(L.Fields.ServiceValue, RecordKind.Officer, GameBuildFacts.Fields.isServiceValueOffset, FieldKind.Int32, int.MinValue, int.MaxValue);
    internal const int Dismissed = 12, Released = 2, RecruitedService = 2;
    // 관직: an index into the game's offices (73 없음). No two officers of a force held the same one, and only
    // the officers serving a ruler held any (2026-10-02), so giving an office takes it from whoever had it.
    internal static readonly FieldDef Office = new(L.Fields.Office, RecordKind.Officer, GameBuildFacts.Fields.isOfficeOffset, FieldKind.Byte, 1, 255);
    internal const int NoOffice = 73;
    // 정책 등급 (1~5) of the officer's own 정책 (per San14PaysageMod, its +0x1BA less the Chinese build's 0x38).
    internal static readonly FieldDef PolicyLevel = new(L.Fields.PolicyLevel, RecordKind.Officer, GameBuildFacts.Fields.isPolicyLevelOffset, FieldKind.Byte, 1, 5);

    internal const int IdOffset = 0x10;
    internal const int FaceOffset = 0xA6; // portrait number: face_NNNN in ScreenLayout.rdb
    internal const int SurnameOffset = 0x12;
    internal const int GivenNameOffset = 0x20;
    internal const int CourtesyNameOffset = 0x2E;
    internal const int CorpsOffset = 0xE0;
    internal const int BaseOffset = 0xE2;
    internal const int StatusOffset = 0xE6;

    internal static string StatusName(int status) => status switch
    {
        1 => L.Terms.StatusRuler,
        2 => L.Terms.StatusCorpsLeader,
        3 => L.Terms.StatusGovernor,
        4 => L.Terms.StatusRegular,
        5 => L.Terms.StatusFree,
        6 => L.Terms.StatusPrisoner,
        7 => L.Terms.StatusHidden,
        9 => L.Terms.StatusDead,
        _ => L.Terms.StatusNumber(status)
    };

    // Officers who are on the map now: rulers, corps leaders, governors, generals, free officers, prisoners.
    internal static bool IsActive(int status) => status is >= 1 and <= 6;

    // Officers serving under a ruler (corps leaders, governors, generals): the only ones whose loyalty matters.
    internal static bool IsVassal(int status) => status is >= 2 and <= 4;

    // A force's own officers: its ruler and those serving under him.
    internal static bool IsMember(int status) => status is >= 1 and <= 4;
}

// Force (세력) record (0x268 bytes; no names in it, so the Korean build matches the Chinese PK one).
// +0x10 ruler, +0x14 + (force - 1) a value towards each force (100 towards itself), +0x48 국호 (an index
// into the game's names, 0 none), +0x49 colour (MapView.GamePalette), +0x52 군사 (officer ID, 0 none),
// then the policy slots (+0x54 battle, +0x72 support, +0x90 schemes, +0xAE domestic, +0xCC personnel:
// 7 officer IDs each), per github.com/PaysageDVII7/San14PaysageMod (Program.cs, the force policy dump).
internal static class ForceFields
{
    internal static readonly FieldDef Country = new(L.Fields.Country, RecordKind.Force, GameBuildFacts.Fields.ForceFieldsCountryOffset, FieldKind.Byte, 0, 100);
    internal static readonly FieldDef Color = new(L.Fields.ForceColor, RecordKind.Force, GameBuildFacts.Fields.ForceFieldsColorOffset, FieldKind.Byte, 0, 60);
    internal static readonly FieldDef Strategist = new(L.Fields.Strategist, RecordKind.Force, GameBuildFacts.Fields.ForceFieldsStrategistOffset, FieldKind.UInt16, 0, OfficerFields.IdMax);

    internal const int RulerOffset = 0x10;

    // 친선 (friendship) with each force, one byte per force from +0x14 (force 1) to +0x46 (force 51), 100 with itself.
    // Usually 0~100, but it goes past (110, 120 and 140 between 원소, 유표 and 유장 in 202), so a byte's whole range.
    // The game keeps it twice, once in each force, always equal (checked over 1,275 pairs
    // while the game ran: they rose and fell together), so an edit writes both.
    internal const int FriendshipOffset = 0x14;

    internal static FieldDef Friendship(int other) => new(L.Fields.Friendship(other), RecordKind.Force, FriendshipOffset + other - 1, FieldKind.Byte, 0, MaxFriendship);

    internal const int MaxFriendship = 255;

    // 0x8200C6 (accepted alliance) and 0x6228E9 (native force editor) use 0x2EBCC0 for this bilateral period.
    // Bits 0..6 hold 0..48 months; bit 7 is separate and follows the setter's player flag policy.
    internal const int AgreementOffset = 0xEB;
    internal const int MaxAgreement = 48;

    internal static FieldDef Agreement(int other) => new(L.Fields.Alliance(other), RecordKind.Force,
        AgreementOffset + other - 1, FieldKind.Byte, 0, 255);

    internal static bool IsAgreement(FieldDef field) => field.Record == RecordKind.Force && field.Kind == FieldKind.Byte
        && field.Offset is >= AgreementOffset and < AgreementOffset + 51;

    internal static int AgreementMonths(ForceRecord force, int other) => (int)force[Agreement(other)] & 0x7F;

    // Native alliance candidates (0x6DE201) exclude tribes 46..50. Slot 51 has no ruler.
    internal static bool CanAlly(ForceRecord force) => force.Id is >= 1 and <= GameMemory.MaxPlayerForce && force.Ruler is > 0 and < 0xFFFF;

    // 작위: an index into the game's ranks (1 황제, 2 왕, 3 공, ... 8 주자사, 9 없음; read on 2026-10-02: 여포 1 in a
    // scenario where he is 황제, 이각 4 대사마).
    internal static readonly FieldDef Rank = new(L.Fields.Rank, RecordKind.Force, GameBuildFacts.Fields.ForceFieldsRankOffset, FieldKind.Byte, 1, 12);

    // 정책: five fields of policies (전투, 지원, 모략, 내정, 인사), each seven officers who carry them out (their own
    // 정책, officer +0x14D), then the days each still needs before it works (0 when it works), then a level. Per
    // github.com/PaysageDVII7/San14PaysageMod; checked on 2026-10-02 (조조: 하후돈 삼군강화 and 하후연 안행강화 in 전투,
    // 10 days to go; 서황 시설개발 in 지원, working). The ruler's and the 군사's own policies work without a slot.
    internal sealed record PolicyField(string Name, int Officers, int Days, int Level);

    internal static readonly PolicyField[] PolicyFields =
        [new(L.Terms.PolicyBattle, 0x54, 0x62, 0x70), new(L.Terms.PolicySupport, 0x72, 0x80, 0x8E), new(L.Terms.PolicyScheme, 0x90, 0x9E, 0xAC), new(L.Terms.PolicyDomestic, 0xAE, 0xBC, 0xCA), new(L.Terms.PolicyPersonnel, 0xCC, 0xDA, 0xE8)];

    internal const int PolicySlots = 7;

    internal static FieldDef PolicyOfficer(int field, int slot) =>
        new(L.Fields.PolicySlot(PolicyFields[field].Name, slot + 1), RecordKind.Force, PolicyFields[field].Officers + slot * 2, FieldKind.UInt16, 0, OfficerFields.IdMax);

    internal static FieldDef PolicyDays(int field, int slot) =>
        new(L.Fields.PolicySlotDays(PolicyFields[field].Name, slot + 1), RecordKind.Force, PolicyFields[field].Days + slot * 2, FieldKind.UInt16, 0, 999);

    // The field a policy belongs to: nine numbers each from 1 (1~9 전투, 10~18 지원, 19~27 모략, 28~36 내정,
    // 37~45 인사; the unused ones are named 정책), then 46~50 one for each field ("모든 전투정책 효과발휘", ...).
    internal static int PolicyFieldOf(int policy) => policy switch
    {
        >= 1 and <= 45 => (policy - 1) / 9,
        >= 46 and <= 50 => policy - 46,
        _ => -1
    };
}

// 명품 (item) record (0x80 bytes, base + 0x77888 holds 202 pointers to them; read on 2026-10-02 against the
// game: 적토마 and 방천화극 with 여포, 사륜거 with 제갈량, 청룡언월도 with 관우). An item's holder is an officer
// ID (0 none; five items held 3001, whose meaning is not known); the officer record has no list of its own.
internal static class ItemFields
{
    internal static readonly FieldDef Owner = new(L.Fields.ItemOwner, RecordKind.Item, GameBuildFacts.Fields.ItemFieldsOwnerOffset, FieldKind.UInt16, 0, ushort.MaxValue);
    // Where an item not yet found lies (a county's index, 0 once someone holds it).
    internal static readonly FieldDef Place = new(L.Fields.Place, RecordKind.Item, GameBuildFacts.Fields.ItemFieldsPlaceOffset, FieldKind.UInt16, 0, ushort.MaxValue);
    // 1 found (held), 2 not found yet, 3 not in play yet (the meaning of 0, 4 and 6 is not known).
    internal static readonly FieldDef State = new(L.Fields.ItemState, RecordKind.Item, GameBuildFacts.Fields.ItemFieldsStateOffset, FieldKind.Byte, 0, 255);
    internal const int Found = 1, Hidden = 2;

    internal const int NameOffset = 0x10, NameChars = 7;
    // The ability it adds (1~5: 통솔·무력·지력·정치·매력, 0 none) and by how much (the game's go up to 10: 참사검
    // 무력 10, 칠성보도 7). Saved with the game (2026-10-02: a bonus written, saved and loaded came back), unlike most
    // of the game's tables. +0x44 is what kind of item it is (11 the horses, 1 the swords; names not found), and
    // +0x47~+0x49 are not known.
    internal const int AbilityOffset = 0x45, BonusOffset = 0x46;
    // The abilities by number, named as the officer's (OfficerFields.Abilities).
    internal static readonly string[] AbilityNames = ["", .. OfficerFields.Abilities.Select(f => f.Label)];
    internal static readonly FieldDef Ability = new(L.Fields.ItemAbility, RecordKind.Item, AbilityOffset, FieldKind.Byte, 0, 5,
        string.Join(" · ", AbilityNames.Select((name, i) => $"{i} {(i == 0 ? L.Terms.None : name)}")));
    internal static readonly FieldDef Bonus = new(L.Fields.ItemBonus, RecordKind.Item, BonusOffset, FieldKind.Byte, 0, 100, L.Fields.ItemBonusHint);

    internal static string AbilityName(int ability) => ability > 0 && ability < AbilityNames.Length ? AbilityNames[ability] : "";
}

// 개성 (personality) record (0xE8 bytes; base + 0x7DA88 holds 356 pointers to them, records of their own): the name
// at +0x10, its description at +0x3C, and two effects, each a kind (one of the game's 113, the same kind in every
// personality that has it: 20 군량소비감소 for 절약, 둔전 and 낭비) and a signed value (절약 20, 낭비 -20: the
// percent, the gold, or 1 for an effect that is just on). Saved with the game (2026-10-02: a value written, saved
// and loaded came back over another one in memory), so an edit stays in that save and reaches every officer
// with the personality there. Read on 2026-10-02: 222 named personalities.
internal static class PersonalityFields
{
    internal const int NameOffset = 0x10, NameChars = 6, DescriptionOffset = 0x3C, Size = 0xE8;
    // Whom the effects reach and when (read from the game's code 2026-10-03; PersonalityRules): +0xA4 the condition,
    // which picks the handler that names the units (10 범위 내 적부대 for 효웅, 9 자신부대를 제외한 범위 내
    // 자신세력부대 for 간웅; the PersonalityConditions table), +0xA8 its number where it takes one (오만's 병사 3,000),
    // +0xAC the second condition each of those units must meet (37 on the descriptions that end in "(함선，병기 외)",
    // 1 a 전투 unit), +0xB4 how long (-1 always, 0 while the condition holds, days once attacked). The save keeps the
    // conditions, kinds and values (0x23AB50) but not +0xB4 or the description, which the game's files give at load.
    internal const int ConditionOffset = 0xA4, ConditionValueOffset = 0xA8, FilterOffset = 0xAC, DurationOffset = 0xB4;
    internal const int NoCondition = 0, OwnUnit = 37;
    internal static readonly FieldDef Condition = new(L.Fields.PersonalityCondition, RecordKind.Personality, ConditionOffset, FieldKind.Byte, 0, 0x64);
    internal static readonly FieldDef ConditionValue =
        new(L.Fields.PersonalityConditionValue, RecordKind.Personality, ConditionValueOffset, FieldKind.Int32, 0, PersonalityRules.MaxTroops);
    internal static readonly FieldDef Filter = new(L.Fields.PersonalityFilter, RecordKind.Personality, FilterOffset, FieldKind.Byte, 0, 0x64);
    // The game reads a kind as a byte below 0x7F (and saves the byte).
    internal static readonly FieldDef[] Kinds =
    [
        new(L.Fields.Effect1, RecordKind.Personality, 0xB6, FieldKind.UInt16, 0, PersonalityRules.LastKind),
        new(L.Fields.Effect2, RecordKind.Personality, 0xBA, FieldKind.UInt16, 0, PersonalityRules.LastKind)
    ];
    internal static readonly FieldDef[] Values =
        [new(L.Fields.Effect1Value, RecordKind.Personality, 0xB8, FieldKind.Int16, short.MinValue, short.MaxValue),
         new(L.Fields.Effect2Value, RecordKind.Personality, 0xBC, FieldKind.Int16, short.MinValue, short.MaxValue)];
}

// The play record (the game's "Play Record Data", saved as prdataN.s14 beside the saves, not in them): the player's
// achievements, the scenarios and tutorials cleared, the 전기제패 scores and which 고대무장 may appear. One object
// in the exe's own memory (exe + 0x2784AB0 on the verified build; built 0x3EAF90, loaded 0x3ED77D), which the game
// writes out whole at every save and every clear (0x3EB930). It has no class: it opens "O41S" and its version (7 in
// this build; read 2026-10-03), which is what tells it.
internal static class PlayRecordFields
{
    internal const ulong Header = 0x0000_0007_5331_344F;
    internal const int HeaderLength = 8;
    // Up to the end of the 전기제패 ranks: one byte for each of 300 전기, 0 (S) to 4 (not cleared).
    internal const int RanksOffset = 0x7B4, RankCount = 300, Size = RanksOffset + RankCount, NoRank = 4;
    // Tutorials cleared, a bit each (0x3EF860).
    internal const int TutorialsOffset = 0x24;
    // Which 고대무장 may appear, a bit each (AncientOfficers.Bit), in two words (the game reads it as one of 64 bits).
    internal static readonly FieldDef Unlocks1 = new(L.Fields.AncientUnlocks1, RecordKind.PlayRecord, GameBuildFacts.Fields.PlayRecordFieldsUnlocks1Offset, FieldKind.Int32, int.MinValue, int.MaxValue);
    internal static readonly FieldDef Unlocks2 = new(L.Fields.AncientUnlocks2, RecordKind.PlayRecord, GameBuildFacts.Fields.PlayRecordFieldsUnlocks2Offset, FieldKind.Int32, int.MinValue, int.MaxValue);
    // Raised by each of the game's setters along with its change (0x3EF450 sets it to 1), so a write raises it too.
    internal static readonly FieldDef Changed = new(L.Fields.PlayRecordChanged, RecordKind.PlayRecord, GameBuildFacts.Fields.PlayRecordFieldsChangedOffset, FieldKind.Byte, 0, 1);
}

// The fields that tie a record to others: whom an officer serves and where, who governs a base, who holds an office,
// a 명품 or a 정책 slot. A write that changes one of them was planned from the game as it was read (a transfer clears
// the slots he held, a new 태수 demotes the old one), so it goes through only while every one of them in that record
// still holds what was read. Plain values someone typed (금, 충성, 능력) are written over whatever the game has.
internal static class LinkedFields
{
    private static readonly FieldDef[] Officer =
    [
        OfficerFields.CorpsField, OfficerFields.BaseField, OfficerFields.PlaceField, OfficerFields.StatusField, OfficerFields.Office,
        OfficerFields.Spouse, OfficerFields.Sworn, OfficerFields.FormerLord, OfficerFields.FormerLordMonths, OfficerFields.PrisonerOf,
        OfficerFields.Captivity, OfficerFields.JoinedYear, OfficerFields.Service, OfficerFields.ServiceValue
    ];

    private static readonly FieldDef[] Force =
    [
        ForceFields.Strategist,
        .. Enumerable.Range(0, ForceFields.PolicyFields.Length).SelectMany(field => Enumerable.Range(0, ForceFields.PolicySlots)
            .SelectMany(slot => new[] { ForceFields.PolicyOfficer(field, slot), ForceFields.PolicyDays(field, slot) }))
    ];

    private static readonly FieldDef[] City = [CityFields.Governor], Gate = [CityFields.GateGovernor];
    private static readonly FieldDef[] Item = [ItemFields.Owner, ItemFields.Place, ItemFields.State];

    internal static IReadOnlyList<FieldDef> Of(RecordKind kind) => kind switch
    {
        RecordKind.Officer => Officer,
        RecordKind.Force => Force,
        RecordKind.City => City,
        RecordKind.Gate => Gate,
        RecordKind.Item => Item,
        _ => []
    };

    internal static bool Contains(FieldDef field) => ForceFields.IsAgreement(field) || Of(field.Record).Contains(field);
}

// Unit (부대) record (0x200 bytes): armies and transports on the map. A slot is free when its kind
// and commander are 0. Offsets match github.com/PaysageDVII7/San14PaysageMod (no names in this record,
// so the Korean build has the same layout) and were checked against the running Korean game.
internal static class UnitFields
{
    internal static readonly FieldDef Troops = new(L.Fields.Troops, RecordKind.Unit, GameBuildFacts.Fields.UnitFieldsTroopsOffset, FieldKind.UInt16, 1, 60_000, L.Fields.UnitTroopsHint);
    internal static readonly FieldDef Wounded = new(L.Fields.Wounded, RecordKind.Unit, GameBuildFacts.Fields.UnitFieldsWoundedOffset, FieldKind.UInt16, 0, 60_000);
    internal static readonly FieldDef Morale = new(L.Fields.Morale, RecordKind.Unit, GameBuildFacts.Fields.UnitFieldsMoraleOffset, FieldKind.Byte, 0, 150, L.Fields.UnitMoraleHint);

    internal static readonly FieldDef[] All = [Troops, Wounded, Morale];

    internal const int KindOffset = 0x10;
    internal const int ArmyKind = 1, TransportKind = 2;
    internal const int CommanderOffset = 0x12;
    internal const int TileOffset = 0x2A;
    // Where the unit is going (found 2026-10-01 by watching units march): +0x38 says what +0x3A is: 5 a base
    // ID (81 of 84 steps went nearer), 27 another unit's slot (78 of 78), 9 a hex (a unit standing still has
    // its own hex there), 11 a building's index (a unit beside the wall it went for), 6 a force's number
    // (units of one force all naming another force; whether to help or to fight it is not known). +0x48 is the next hex on its way
    // (48400 when none). The game keeps no longer route in the record; it takes one step at a time.
    internal const int TargetKindOffset = 0x38, TargetOffset = 0x3A, NextTileOffset = 0x48;
    internal const int BaseTarget = 5, ForceTarget = 6, TileTarget = 9, BuildingTarget = 11, UnitTarget = 27;

    // 진형 (formation): an index into the game's formation table, read from memory (2026-10-01, records of
    // stride 0xC8 next to the building table). Armies on the map held 1~10, every transport 19 (수송). This is the one
    // on land; on water a unit fights in +0x1C, which the game sets from its force. The game's setter (0x241550) takes
    // any land formation without asking the commander: units of the game's own held one their commander lacks, and a
    // unit written from 방원 to 추행 went 13 → 31 a step at once and kept it (2026-10-03).
    internal const int FormationOffset = 0x1B;
    internal static readonly FieldDef Formation = new(L.Fields.Formation, RecordKind.Unit, FormationOffset, FieldKind.Byte, 1, 20);
    // The land formations (the formation table's +0x7E is 1, read 2026-10-03): 1~10 and the tribes' 사이 (14). 수송 (19)
    // is the transports' own; 11~13, 17, 18 and 20 are on water, 15 and 16 unused.
    internal static readonly int[] LandFormations = [.. Enumerable.Range(1, 10), 14];
    internal const int TribeFormation = 14;

    // Whether a unit may be given a formation: an army, a land formation, 사이 only under a tribe officer.
    internal static bool MayUseFormation(int kind, int commander, long formation) =>
        kind == ArmyKind && LandFormations.Contains((int)formation) && (formation != TribeFormation || OfficerFields.IsTribeOfficer(commander));

    // 능력 변화: what tactics and facilities raise or lower for a while (the game's setter 0x2EC3D0, read 2026-10-03), for
    // 기동, 공군 (against units), 공성 (against bases), 방어 and 파성 in this order: a level of -10~10 at +0x4A.. (5% a level:
    // 기동 +10 took a unit from 22 to 34 a step) and the time left at +0x50.., 0~240. The game counts every unit's time
    // down together (0x455AA0 → 0x302200), about once in 6 hours of game time (29 in 174 hours, 24 in 160), and sets
    // the level back to 0 when it reaches 0; a facility or tactic still in reach sets it again. A sixth pair (+0x4F,
    // +0x55) has no name in the game's messages and is left alone.
    internal const int BoostLevelMax = 10, BoostTimeMax = 240, BoostPercent = 5, HoursPerCount = 6;
    private static readonly string[] BoostNames = [L.Terms.BoostMobility, L.Terms.BoostAttack, L.Terms.BoostSiege, L.Terms.BoostDefense, L.Terms.BoostBreach];
    internal static readonly FieldDef[] BoostLevels = BoostNames
        .Select((name, i) => new FieldDef(L.Fields.BoostLevel(name), RecordKind.Unit, 0x4A + i, FieldKind.SByte, -BoostLevelMax, BoostLevelMax, L.Fields.BoostLevelHint(BoostPercent))).ToArray();
    internal static readonly FieldDef[] BoostTimes = BoostNames
        .Select((name, i) => new FieldDef(L.Fields.TimeLeft(name), RecordKind.Unit, 0x50 + i, FieldKind.Byte, 0, BoostTimeMax, L.Fields.TimeLeftHint(HoursPerCount))).ToArray();
    internal static string BoostName(int boost) => BoostNames[boost];

    // 상태 이상: the time left of 혼란, 저지 and 도발 at +0x23.. (0~120, named by the messages the game shows when one
    // ends), counted down as the 능력 변화. 저지 3 held a marching unit on its hex. A 도발 also keeps whom it answers
    // (+0x3C kind, +0x3E number), which the editor cannot choose, so it is only lowered.
    internal const int AilmentMax = 120, Provoked = 2;
    private static readonly string[] AilmentNames = [L.Terms.Confused, L.Terms.Blocked, L.Terms.Provoked];
    internal static readonly FieldDef[] Ailments = AilmentNames
        .Select((name, i) => new FieldDef(L.Fields.TimeLeft(name), RecordKind.Unit, 0x23 + i, FieldKind.Byte, 0, AilmentMax, L.Fields.TimeLeftHint(HoursPerCount))).ToArray();
    internal static string AilmentName(int ailment) => AilmentNames[ailment];

    // What the unit card shows in boxes besides UnitFields.All.
    internal static readonly FieldDef[] Timed = [.. BoostLevels, .. BoostTimes, .. Ailments];

    private static readonly string[] FormationNames =
        ["", .. Enumerable.Range(1, 20).Select(id => L.Table("Formations", id))];

    internal static string FormationName(int formation) => formation > 0 && formation < FormationNames.Length ? FormationNames[formation] : "";
    // The order (+0x37), read from units with known targets: 1 move to a hex, 2 go to an own base (what
    // transports do), 4 attack a base, 7 attack a unit, 11 go back to an own base; others (13, 14, ...) unknown.
    internal const int OrderOffset = 0x37;

    internal enum Order { Move, Transport, Attack, Return }

    internal static Order OrderOf(int value) => value switch
    {
        2 => Order.Transport,
        4 or 7 => Order.Attack,
        11 => Order.Return,
        _ => Order.Move
    };

    internal static string OrderName(Order order) => order switch
    {
        Order.Transport => L.Terms.OrderTransport,
        Order.Attack => L.Terms.OrderAttack,
        Order.Return => L.Terms.OrderReturn,
        _ => L.Terms.OrderMove
    };
    internal const long MoraleCap = 130;

    internal static string KindName(int kind) => kind switch
    {
        ArmyKind => L.Terms.UnitArmy,
        TransportKind => L.Terms.UnitTransport,
        _ => L.Terms.UnitKindNumber(kind)
    };
}

// A whole number as typed into a table's cell or a card's box. Thousands separators are allowed, and
// full-width figures (the input method's 전자 mode, or pasted text) count as the usual ones.
internal static class TypedNumber
{
    internal static bool TryParse(string? text, out long value)
    {
        // The full-width forms of "!" to "~" (U+FF01 to U+FF5E) are those characters plus 0xFEE0; U+3000 is the
        // full-width space.
        string plain = string.Concat((text ?? "").Select(c => c is >= (char)0xFF01 and <= (char)0xFF5E ? (char)(c - 0xFEE0) : c == (char)0x3000 ? ' ' : c));
        return long.TryParse(plain.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value);
    }
}

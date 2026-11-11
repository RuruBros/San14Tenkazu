using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;

namespace San14Tenkazu;

// Product-owned definitions only. User preferences and discovery caches never register a verified build.
internal sealed class GameBuildProfile
{
    internal string Id { get; }
    internal string Sha256 { get; }
    internal string ExeSha256 { get; }
    internal string LayoutId { get; }
    internal string NativeContractId { get; }
    internal string NativeIdentity => $"{Id}:{Sha256}:{NativeContractId}";
    internal ImmutableArray<(ulong Rva, int Size, string Hash)> SeasonTargets { get; }
    internal ImmutableArray<(ulong Rva, int Size, string Hash)> EnvironmentTargets { get; }
    internal ulong WorldPointerRva { get; }
    internal ulong SceneManagerRva { get; }
    internal ulong SceneDriverClassRva { get; }
    internal ulong UpdateRva { get; }
    internal ulong UpdateSlotRva { get; }
    private readonly IReadOnlyDictionary<string, string> resources;
    internal GameBuildProfile(string resourceName, string expectedSha256)
    {
        using var stream = typeof(GameBuildProfile).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException("Embedded build profile is missing");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        Sha256 = Hash(bytes.ToArray());
        if (!Sha256.Equals(expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Embedded build profile differs from compiled facts");
        using var document = JsonDocument.Parse(bytes.ToArray()); var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported build profile schema");
        Id = root.GetProperty("id").GetString()!; ExeSha256 = root.GetProperty("exeSha256").GetString()!;
        LayoutId = root.GetProperty("layoutId").GetString()!; NativeContractId = root.GetProperty("nativeContractId").GetString()!;
        resources = root.GetProperty("resources").EnumerateArray().ToImmutableDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("sha256").GetString()!);
        ImmutableArray<(ulong, int, string)> Targets(string kind) => root.GetProperty("guards").GetProperty(kind).EnumerateArray()
            .Select(g => (g.GetProperty("rva").GetUInt64(), g.GetProperty("size").GetInt32(), g.GetProperty("sha256").GetString()!)).ToImmutableArray();
        SeasonTargets = Targets("Season"); EnvironmentTargets = Targets("Environment");
        ulong Fact(string group, string name) => root.GetProperty("groups").GetProperty(group).GetProperty(name).GetProperty("value").GetUInt64();
        WorldPointerRva = Fact("Memory", "BasePointerRva");
        SceneManagerRva = Fact("Scene", "ManagerRva");
        SceneDriverClassRva = Fact("Scene", "DriverClassRva");
        UpdateRva = Fact("Engine", "UpdateRva");
        UpdateSlotRva = Fact("Engine", "UpdateSlotRva");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal byte[] ReadResource(string name)
    {
        if (!resources.TryGetValue(name, out var expected)) throw new InvalidDataException("Resource is outside this build profile");
        using var stream = typeof(GameBuildProfile).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("Build profile resource is missing: " + name);
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        return VerifyResource(name, bytes.ToArray());
    }
    internal byte[] VerifyResource(string name, byte[] bytes)
    {
        if (!resources.TryGetValue(name, out var expected) || Hash(bytes) != expected)
            throw new InvalidDataException("Build profile resource differs: " + name);
        return bytes;
    }
    internal void VerifyNativeManifest(JsonElement manifest)
    {
        foreach (var (name, value) in new[] { ("gameProfileId", Id), ("gameProfileSha256", Sha256), ("nativeContractId", NativeContractId) })
            if (!manifest.TryGetProperty(name, out var actual) || actual.ValueKind != JsonValueKind.String || actual.GetString() != value)
                throw new InvalidDataException("Native build profile differs: " + name);
    }
    internal void VerifyNativeBinary(byte[] file)
    {
        uint rva = NativeExports.Rva(file, "San14BuildProfileIdentity");
        using var stream = new MemoryStream(file); using var pe = new PEReader(stream);
        byte[] expected = Encoding.ASCII.GetBytes(NativeIdentity + '\0');
        if (!pe.GetSectionData(checked((int)rva)).GetContent(0, expected.Length).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Native compiled build profile differs");
    }
}

internal sealed record GameDataProfile(string ExeSha256, ulong WorldPointerRva, GameSessionLayout Session,
    ImmutableDictionary<FieldDef, GameDataGuard> Fields, GameAbilityTable? AbilityTable, ImmutableArray<GameDataGuard> RuleGuards,
    ulong MilitaryCapsRva = 0, ulong PlayRecordRva = 0)
{
    internal bool Supports(FieldDef field) => Fields.ContainsKey(field);
    internal bool Supports(params RecordKind[] kinds) => Fields.Keys.Any(field => kinds.Contains(field.Record));
}

internal sealed record GameDataGuard(ulong Rva, int Length, string Sha256,
    ImmutableArray<GameDataGuard> Dependencies = default)
{
    internal IEnumerable<GameDataGuard> RequiredCode => Dependencies.IsDefaultOrEmpty
        ? [this] : new[] { this }.Concat(Dependencies.SelectMany(guard => guard.RequiredCode));
}

internal sealed record GameAbilityTable(ulong ManagerPointerRva, ulong RecordClassRva, ImmutableArray<GameDataGuard> Guards);

internal static class GameBuildProfiles
{
    internal static GameBuildProfile Current { get; } = new("BuildProfiles.current.json", GameBuildFacts.Sha256);
    internal static GameBuildProfile NovemberNative { get; } = new("BuildProfiles.november2026.json", GameNativeFactsNovember2026.Sha256);
    private static readonly ImmutableArray<GameBuildProfile> supported = [Current];
    private static readonly GameDataGuard NovemberAbilityGain = new(0x2BC0D0, 832,
        "45990E4FB792CC27DFAC9C405E43C405F8CD480F3FCF1C3EE54E430D7A3B639B");
    // Data editors and Native features have independent admission evidence.
    private static readonly GameDataProfile November2026 = new(
        GameNativeFactsNovember2026.ExeSha256, 0x2780F60,
        new(0x1B65100, 0x110B4, "488D054540B5014883C4305BC3", 0x5726F0,
            0x1400A90, 0x1400A18, 0x13E7E98, 0x13D9950), NovemberDataFields(),
        new(0x2780F50, 0x13AAC18,
        [
            new(0x337C40, 8, "321A4B0FA6980C861E4B32D06CACB50AF70A7C9B2F75EF9E693FB4EDDC0CB65E"),
            new(0x2442F0, 16, "C39BF7CCE4F8CE7DD01076A9512ADBB722B177E4BFFC3FCA112DE3FAB8BCDA7D"),
            NovemberAbilityGain
        ]), GameRuleTableProfile.NovemberGuards, MilitaryCapsRva: 0x1A4B3B8, PlayRecordRva: 0x2787B00);

    private static ImmutableDictionary<FieldDef, GameDataGuard> NovemberDataFields()
    {
        // Independently traced serializer instructions and operand widths for this EXE.
        var fields = ImmutableDictionary.CreateBuilder<FieldDef, GameDataGuard>();
        void Register(GameDataGuard guard, IEnumerable<FieldDef> targets)
        {
            foreach (var target in targets) fields.Add(target, guard);
        }
        void Add(ulong rva, int length, string hash, params FieldDef[] targets)
        {
            Register(new(rva, length, hash), targets);
        }
        Add(0x2391C0, 920, "A748C5EF9A0A3B9FDB75CF02C0FD64980071ABEE392FD59381542D2A8C4FD750",
            CityFields.Gold, CityFields.Food, CityFields.Troops, CityFields.Wounded, GateFields.Troops,
            CityFields.Governor, CityFields.GateGovernor, CityFields.Morale, GateFields.Morale, GateFields.BaseDurability);
        Add(0x238710, 506, "29705F0D28DEED5A48A096C6573BAFFFCEEF2D38C3ED9C4F185F928C0BF2F76A", CityFields.Scale);
        Add(0x237090, 1156, "72A8224065DDB40933525A80EDDC6F53920BF3014935EACE0A37B877614B3741", CountyFields.All);
        Add(0x23A8E0, 377, "5E8994DA57B6AB264B84F89911C1B26AFCFB3AB572F1DA0A5710F35591D18C07",
            CityFields.Durability, GateFields.Durability, FacilityFields.Durability);
        Register(new(0x23AA60, 6813, "6DE4452C9DD81C683BE1EA0E705F50E17441506B7372DED2225C4E57645CF696",
            [new(0x1D49A0, 92, "5A89BD3743222FD6547D286C348B8C9761712125D9F3A7D32882ADD93C6E902A"),
             new(0x204270, 116, "96B895BA7E9451FDCD641AE97BAAB56FC939A0624C1FC510CC929599CF822D33"),
             new(0x204370, 117, "D7BA267254FBD352746D5406EE3B4448C6025A6FC8BD45700BAE39FA96172F67"),
             new(0x204470, 117, "158BB8E394FEEE0C92520755511BBFF480CF1AEA609CB83DA2801F3DC9955D65"),
             new(0x204570, 117, "A4E796B14D62DFC2BBA15BB3910EEFCF72F773D229B2C10E59D4938392B6CB76"),
             new(0x204670, 117, "A4917C003FBC178A91815E21B713FFA0DF2D4095A268A1B21C60EC5D057088F8")]),
            [OfficerFields.Loyalty, OfficerFields.Merit, .. OfficerFields.Years, .. OfficerFields.Abilities,
                .. OfficerFields.Abilities.Select(OfficerFields.ExperienceOf), .. OfficerFields.Personalities,
                .. OfficerFields.Likes, .. OfficerFields.Dislikes, OfficerFields.CorpsField, OfficerFields.BaseField,
                OfficerFields.PlaceField, OfficerFields.StatusField, OfficerFields.Office, OfficerFields.FormerLord,
                OfficerFields.FormerLordMonths, OfficerFields.PrisonerOf, OfficerFields.Captivity,
                OfficerFields.JoinedYear, OfficerFields.Service, OfficerFields.ServiceValue,
                OfficerFields.Gender, OfficerFields.Compatibility, OfficerFields.Ideology, OfficerFields.Policy,
                OfficerFields.PolicyLevel, OfficerFields.Spouse, OfficerFields.Sworn, OfficerFields.UniqueTactic,
                OfficerFields.Formations, .. OfficerFields.Tactics, .. AiTraits.Fields]);
        Register(new(0x237520, 2398, "F653A54EDCD729F3120A9FE3145071FEAD30CC86B9A7A8D670F4FE63AE7CFED6",
            [new(0x220450, 116, "7D78012E0DD1308CC839343D1D500EE3CC8332240AE6FEA4411DDF41B432F660"),
             new(0x220550, 116, "5865F083C6E27EF5691963EF1A26A10754C9BB6D8C937C6B0963541F0098A12A"),
             new(0x220850, 117, "921B10E63EF2A04124B53E3FA7376B2A2031733EA262F27CC147AAF49BE511DD"),
             new(0x2209D0, 117, "536417EA646F3C4506D6ADFF8EA1AD10FA2DDAA7CFCEA539E655CB3BCCF8CA11")]),
            [.. UnitFields.All, UnitFields.Formation, .. UnitFields.Timed]);
        var force = new GameDataGuard(0x239560, 1835, "B3118C2F41C645AC58852E0352B85DE1B2E4C94CD756D1DDC1A671A79E337B46");
        Register(force, [ForceFields.Country, ForceFields.Color, ForceFields.Strategist, ForceFields.Rank,
            .. Enumerable.Range(1, GameMemory.MaxPlayerForce).Select(ForceFields.Agreement)]);
        // The parent supplies each array's base; its nested serializer supplies widths and counts.
        Register(force with { Dependencies = [new(0x2206D0, 116,
            "C938D7AD846FF5028636A94823F4050B0D853DCFE1B763B3E7BB7C1C324A3F5A")] },
            Enumerable.Range(1, 51).Select(ForceFields.Friendship));
        Register(force with { Dependencies = [new(0x2211E0, 308,
            "C05B21A5915618C3901A6ACE4D9E2C457C0E7EEC26A99B2DB35DDF9BF7B6DE31")] },
            Enumerable.Range(0, ForceFields.PolicyFields.Length).SelectMany(field =>
                Enumerable.Range(0, ForceFields.PolicySlots).SelectMany(slot =>
                    new[] { ForceFields.PolicyOfficer(field, slot), ForceFields.PolicyDays(field, slot) })));
        Add(0x325F80, 207, "2D40E0B9E25D5AF64D7D421E794AF544F3291F728DBBEA6731A20A5C0FFC33A7",
            ItemFields.Owner, ItemFields.Place, ItemFields.State);
        Add(0x23A030, 759, "D67EF7EF87F58A3FC65DCD2EC054C983C2662E544A178EE801375A8FE783A784", ItemFields.Ability, ItemFields.Bonus);
        Add(0x23C500, 610, "91C3B2464C0BDC0FB7CCD0468AB324107B61B223DFA5D0706784B65928E2E91A",
            [PersonalityFields.Condition, PersonalityFields.ConditionValue, PersonalityFields.Filter,
                .. PersonalityFields.Kinds, .. PersonalityFields.Values]);
        Register(new(0x238D40, 1149, "408E0BA6805EC45E8E27C4525F85CAE199557F29D6000879C3F68615E3369D2D",
            [new(0x232C50, 108, "86AF854C8E3E24E1D991C188CA82A259E0419CD5A2772190117866A7944E7A35")]),
            [CorpsFields.MilitaryPoints]);
        Register(new(0x3F1970, 116, "F26710E71D543D5746446B5C7467D01B412F32FF0884F120EF5B868153831440",
            [new(0x3ED4B0, 342, "7D8950EA0A01F373E9564C06BFCEC658D4613C24630CB0D4CE9ABA7409AF98AE"),
             new(0x3EC510, 129, "7D95BE6A261A2A9208E9F44131CF30913D6128F36815B5DA7461F028CA9FA8DB"),
             new(0x3ED950, 94, "2497E10E3862DBFB62C0DA2A23362C4E5FD67D5A817AF3E1FB1314DCF979B3A4")]),
            [PlayRecordFields.Unlocks1, PlayRecordFields.Unlocks2, PlayRecordFields.Changed]);
        return fields.ToImmutable();
    }
    internal static GameBuildProfile? Find(string? exeSha256) => supported.SingleOrDefault(p => string.Equals(exeSha256, p.ExeSha256, StringComparison.OrdinalIgnoreCase));
    internal static GameDataProfile? FindData(string? exeSha256) =>
        string.Equals(exeSha256, November2026.ExeSha256, StringComparison.OrdinalIgnoreCase) ? November2026 : null;
    internal static GameBuildProfile Require(GameIdentity identity) => identity.Verified && Find(identity.ExeHash) is { } profile
        ? profile : throw new InvalidDataException("A registered verified game build profile is required");
    internal static GameBuildProfile? FindNative(GameIdentity identity) => identity.Verified ? Find(identity.ExeHash)
        : string.Equals(identity.ExeHash, NovemberNative.ExeSha256, StringComparison.OrdinalIgnoreCase) ? NovemberNative : null;
    internal static GameBuildProfile RequireNative(GameIdentity identity) => FindNative(identity)
        ?? throw new InvalidDataException("A registered Native game build profile is required");
}

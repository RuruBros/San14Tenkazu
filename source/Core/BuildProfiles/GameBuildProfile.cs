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
    ImmutableDictionary<FieldDef, GameDataGuard> Fields, GameAbilityTable? AbilityTable, ImmutableArray<GameDataGuard> RuleGuards)
{
    internal bool Supports(FieldDef field) => Fields.ContainsKey(field);
    internal bool Supports(params RecordKind[] kinds) => Fields.Keys.Any(field => kinds.Contains(field.Record));
}

internal sealed record GameDataGuard(ulong Rva, int Length, string Sha256);

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
        ]), GameRuleTableProfile.NovemberGuards);

    private static ImmutableDictionary<FieldDef, GameDataGuard> NovemberDataFields()
    {
        // Independently traced serializer instructions and operand widths for this EXE.
        var fields = ImmutableDictionary.CreateBuilder<FieldDef, GameDataGuard>();
        void Add(ulong rva, int length, string hash, params FieldDef[] targets)
        {
            var guard = new GameDataGuard(rva, length, hash);
            foreach (var target in targets) fields.Add(target, guard);
        }
        Add(0x2391C0, 920, "A748C5EF9A0A3B9FDB75CF02C0FD64980071ABEE392FD59381542D2A8C4FD750",
            CityFields.Gold, CityFields.Food, CityFields.Troops, CityFields.Wounded, GateFields.Troops);
        Add(0x238710, 506, "29705F0D28DEED5A48A096C6573BAFFFCEEF2D38C3ED9C4F185F928C0BF2F76A", CityFields.Scale);
        Add(0x237090, 1156, "72A8224065DDB40933525A80EDDC6F53920BF3014935EACE0A37B877614B3741", CountyFields.All);
        Add(0x23A8E0, 377, "5E8994DA57B6AB264B84F89911C1B26AFCFB3AB572F1DA0A5710F35591D18C07",
            CityFields.Durability, GateFields.Durability, FacilityFields.Durability);
        Add(0x23AA60, 6813, "6DE4452C9DD81C683BE1EA0E705F50E17441506B7372DED2225C4E57645CF696",
            [OfficerFields.Loyalty, OfficerFields.Merit, .. OfficerFields.Years, .. OfficerFields.Abilities,
                .. OfficerFields.Abilities.Select(OfficerFields.ExperienceOf), .. OfficerFields.Personalities]);
        Add(0x237520, 0x4A0, "7073A5472942842BB341AD3C3C2C574C899ECD0619DB4391FFA38ABA396CAC7C", UnitFields.All);
        Add(0x239560, 1835, "B3118C2F41C645AC58852E0352B85DE1B2E4C94CD756D1DDC1A671A79E337B46",
            [ForceFields.Country, ForceFields.Color, .. Enumerable.Range(1, GameMemory.MaxPlayerForce).Select(ForceFields.Agreement)]);
        Add(0x325F80, 207, "2D40E0B9E25D5AF64D7D421E794AF544F3291F728DBBEA6731A20A5C0FFC33A7",
            ItemFields.Owner, ItemFields.Place, ItemFields.State);
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

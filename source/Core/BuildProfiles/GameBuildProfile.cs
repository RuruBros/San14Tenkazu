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

internal static class GameBuildProfiles
{
    internal static GameBuildProfile Current { get; } = new("BuildProfiles.current.json", GameBuildFacts.Sha256);
    private static readonly ImmutableArray<GameBuildProfile> supported = [Current];
    internal static GameBuildProfile? Find(string? exeSha256) => supported.SingleOrDefault(p => string.Equals(exeSha256, p.ExeSha256, StringComparison.OrdinalIgnoreCase));
    internal static GameBuildProfile Require(GameIdentity identity) => identity.Verified && Find(identity.ExeHash) is { } profile
        ? profile : throw new InvalidDataException("A registered verified game build profile is required");
}

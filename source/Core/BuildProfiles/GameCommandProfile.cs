using System.Security.Cryptography;
using System.Text.Json;

namespace San14Tenkazu;

internal sealed record GameCommandTarget(ulong Rva, string Start, int Length, string Sha256)
{
    internal byte[] Prefix { get; } = Convert.FromHexString(Start);

    internal bool Matches(byte[] bytes) =>
        bytes.Length == Length && Convert.ToHexString(SHA256.HashData(bytes)) == Sha256;
}

internal sealed record GameCommandProfile(string ExeHash, Dictionary<ulong, GameCommandTarget> Functions,
    Dictionary<ulong, ulong> Globals)
{
    private static readonly Lazy<GameCommandProfile> November = new(Load);

    internal static GameCommandProfile? Find(GameIdentity identity) =>
        identity.DataProfile is not null && GameBuildProfiles.FindNative(identity) == GameBuildProfiles.NovemberNative
            ? November.Value : null;

    internal static bool Supports(GameIdentity identity, ulong legacyRva) =>
        identity.Verified || Find(identity)?.Functions.ContainsKey(legacyRva) == true;

    internal GameCommandTarget Target(ulong legacyRva) => Functions.TryGetValue(legacyRva, out var target)
        ? target : throw new InvalidOperationException(L.Calls.WhyUnverified);

    internal ulong Global(ulong legacyRva) => Globals.TryGetValue(legacyRva, out var rva)
        ? rva : throw new InvalidOperationException(L.Calls.WhyUnverified);

    private static GameCommandProfile Load()
    {
        using var stream = typeof(GameCommandProfile).Assembly.GetManifestResourceStream("BuildProfiles.NovemberCommands.json")
            ?? throw new InvalidDataException(L.Compatibility.Unavailable);
        using var content = new MemoryStream();
        stream.CopyTo(content);
        byte[] bytes = content.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != "E00B641FDBC379251938A6DEA1EC95F06D39769628A0D5C192A9893CAABBE52A")
            throw new InvalidDataException(L.Compatibility.Unavailable);
        var profile = JsonSerializer.Deserialize<GameCommandProfile>(bytes) ?? throw new InvalidDataException(L.Compatibility.Unavailable);
        if (profile.ExeHash != GameBuildProfiles.NovemberNative.ExeSha256 ||
            profile.Functions.Values.Select(target => target.Rva).Distinct().Count() != profile.Functions.Count)
            throw new InvalidDataException(L.Compatibility.Unavailable);
        return profile;
    }
}

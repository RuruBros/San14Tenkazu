using System.Security.Cryptography;
using System.Text.Json;

namespace San14Tenkazu;

internal sealed record RuntimeRuleBuildProfile(string ExeHash, uint ExecutableLimit, RuntimePatch[] Patches,
    RuntimeTraitList.Profile Traits)
{
    private static readonly Lazy<RuntimeRuleBuildProfile> November = new(LoadNovember);

    internal static RuntimeRuleBuildProfile? Find(GameIdentity identity) =>
        identity.DataProfile is not null &&
        string.Equals(identity.ExeHash, GameBuildProfiles.NovemberNative.ExeSha256, StringComparison.OrdinalIgnoreCase)
            ? November.Value : null;

    internal static bool Supports(GameIdentity identity) =>
        identity.Verified || Find(identity) is not null;

    private static RuntimeRuleBuildProfile LoadNovember()
    {
        using var resource = typeof(RuntimeRuleBuildProfile).Assembly.GetManifestResourceStream("RuntimeRules.November2026.json")
            ?? throw new InvalidDataException(L.Compatibility.Unavailable);
        using var stream = new MemoryStream();
        resource.CopyTo(stream);
        byte[] bytes = stream.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != "CDDAA5A7FDA688B0EFA45CB6F6249D9D61CCA2452AA506CC932C9F178F63CA5A")
            throw new InvalidDataException(L.Compatibility.Unavailable);
        var result = JsonSerializer.Deserialize<RuntimeRuleBuildProfile>(bytes)!;
        if (result.ExeHash != GameBuildProfiles.NovemberNative.ExeSha256)
            throw new InvalidDataException(L.Compatibility.Unavailable);
        RuntimeTraitList.ValidateDefinition(result.Traits.Layout);
        return result;
    }
}

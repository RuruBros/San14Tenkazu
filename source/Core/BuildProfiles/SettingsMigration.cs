using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace San14Tenkazu;

internal sealed record BuildSemanticContract(string Build, string RuntimeSettings, string RuleSettings);
internal sealed record SettingsMigrationRoute(string Source, string Target, bool Runtime, bool Rules);

// Only authored, reviewed routes count. A shared layout ID, user permission, or search
// candidate alone never establishes that two builds give a saved option the same meaning.
internal sealed class SettingsMigrationPolicy(IEnumerable<BuildSemanticContract> definitions, IEnumerable<SettingsMigrationRoute> routes)
{
    private readonly ImmutableDictionary<string, BuildSemanticContract> contracts = definitions.ToImmutableDictionary(d => d.Build, StringComparer.OrdinalIgnoreCase);
    private readonly ImmutableArray<SettingsMigrationRoute> reviewed = routes.ToImmutableArray();
    internal static SettingsMigrationPolicy Product { get; } = new(
        [new(GameBuildFacts.ExeSha256, "runtime-settings-v1", RuleProfile.CurrentSchema)], []);
    internal void Require(string source, string target, bool runtime)
    {
        if (!contracts.TryGetValue(source, out var from) || !contracts.TryGetValue(target, out var to)
            || (runtime ? from.RuntimeSettings != to.RuntimeSettings : from.RuleSettings != to.RuleSettings)
            || !source.Equals(target, StringComparison.OrdinalIgnoreCase) && !reviewed.Any(r => r.Source.Equals(source, StringComparison.OrdinalIgnoreCase)
                && r.Target.Equals(target, StringComparison.OrdinalIgnoreCase) && (runtime ? r.Runtime : r.Rules)))
            throw new InvalidDataException(L.Compatibility.MigrationUnsupported);
    }
}

internal sealed class SettingsImportSource
{
    internal string PathName { get; }
    private readonly byte[] bytes;
    internal string Sha256 => Convert.ToHexString(SHA256.HashData(bytes));
    private SettingsImportSource(string path, byte[] value) { PathName = Path.GetFullPath(path); bytes = value; }
    internal static SettingsImportSource Read(string path) => new(path, File.ReadAllBytes(path));
    internal T Parse<T>()
    {
        using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("Writes", out _) || root.TryGetProperty("Phase", out _)
            || root.TryGetProperty("TraitListAllocation", out _)) throw new InvalidDataException(L.Compatibility.MigrationUnsupported);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException(L.Compatibility.MigrationUnsupported);
    }
    internal FileStream Guard()
    {
        var stream = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using var copy = new MemoryStream(); stream.CopyTo(copy);
            if (!copy.ToArray().AsSpan().SequenceEqual(bytes)) throw new IOException(L.Rules.ProfileMoved);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    internal void Archive(string directory) => SettingsHistory.Keep(directory, bytes);
}

internal static class SettingsHistory
{
    internal static void Keep(string directory, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(bytes)) + ".json");
        if (File.Exists(path)) { if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new IOException("Settings archive hash/content differs"); return; }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw; }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class SettingsMigration
{
    internal static RuntimeRulePreference Runtime(RuntimeRulePreference source, string target, SettingsMigrationPolicy? policy = null)
    {
        source.Validate(); (policy ?? SettingsMigrationPolicy.Product).Require(source.Build, target, true);
        // No process ID, pointer, allocation ownership, prepared receipt, or experience data is copied.
        return new(1, target, source.Settings, source.ConvertExperience);
    }
    internal static RuleProfile Rules(RuleProfile source, RuleProfile targetBaseline, SettingsMigrationPolicy? policy = null)
    {
        source = source.Upgrade(); source.Validate(); targetBaseline.Validate();
        (policy ?? SettingsMigrationPolicy.Product).Require(source.Build, targetBaseline.Build, false);
        foreach (var table in source.Catalogs!)
        {
            var target = targetBaseline.Table(table.Table);
            if (target is null || table.Schema != target.Schema || table.Fingerprint != target.Fingerprint || !table.Records.SequenceEqual(target.Records))
                throw new InvalidDataException(L.Rules.Incompatible);
        }
        var importedTables = source.Catalogs.Select(c => c.Table).ToHashSet();
        var overrides = targetBaseline.Overrides.Where(o => !importedTables.Contains(o.Key.Table))
            .Concat(source.Overrides.Where(o => o.Value != targetBaseline.Default(o.Key))).ToArray();
        var result = targetBaseline with { Overrides = overrides, AutoApply = false };
        result.Validate(); return result;
    }
}

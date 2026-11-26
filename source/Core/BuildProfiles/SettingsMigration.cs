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
        [new(GameBuildFacts.ExeSha256, "runtime-settings-v1", RuleProfile.CurrentSchema),
         new(GameNativeFactsNovember2026.ExeSha256, "runtime-settings-v1", RuleProfile.CurrentSchema)],
        [new(GameBuildFacts.ExeSha256, GameNativeFactsNovember2026.ExeSha256, true, true),
         new(GameNativeFactsNovember2026.ExeSha256, GameBuildFacts.ExeSha256, true, true)]);
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

internal sealed record RuleImportConflict(RuleKey Key, int Original, int TargetDefault, int Current, int Incoming);
internal sealed record RuleImportPlan(RuleProfile Target, RuleChange[] Changes, RuleImportConflict[] Conflicts, RuleKey[] Unavailable,
    RuleImportCompatibility Compatibility)
{
    internal RuleProfile Resolve(IReadOnlySet<RuleKey>? takeIncoming = null)
    {
        if (Unavailable.Length != 0 && (takeIncoming is null || Changes.Length == 0 && takeIncoming.Count == 0))
        {
            var held = Unavailable.Where(Compatibility.RangeHeld).Select(k => $"#{k.Record} {FormationRules.Field(k.Field).Label}").ToArray();
            if (held.Length > 0) throw new InvalidDataException(L.EditRanges.ImportHold(string.Join(", ", held)));
            throw new InvalidDataException(L.FileComparison.CannotImport(string.Join(", ", Unavailable.Select(k => $"{k.Table} #{k.Record}").Distinct())));
        }
        if (takeIncoming is not null && takeIncoming.Except(Conflicts.Select(c => c.Key)).Any())
            throw new InvalidDataException(L.Rules.Incompatible);
        var selected = Conflicts.Where(c => takeIncoming?.Contains(c.Key) == true)
            .Select(c => new RuleChange(c.Key, c.Current, c.Incoming));
        var edits = Changes.Concat(selected).ToArray();
        var result = Target.WithChanges(edits) with { AutoApply = false };
        foreach (var edit in edits)
            if (!Compatibility.DependenciesMatch(edit.Key, result.Desired))
                throw new InvalidDataException(L.FileComparison.DependentChoice(edit.Key.Table, edit.Key.Record, edit.Key.Field));
        return result;
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
    internal static RuleImportPlan PlanRules(RuleProfile source, RuleProfile targetBaseline, SettingsMigrationPolicy? policy = null)
    {
        source = source.Upgrade(); source.Validate(); targetBaseline.Validate();
        (policy ?? SettingsMigrationPolicy.Product).Require(source.Build, targetBaseline.Build, false);
        var compatibility = new RuleImportCompatibility(source, targetBaseline);
        var candidates = source.Overrides.Where(compatibility.Eligible).ToDictionary(o => o.Key, o => o.Value);
        compatibility.KeepCompatible(candidates, targetBaseline.Desired);
        var changes = new List<RuleChange>();
        var conflicts = new List<RuleImportConflict>();
        var unavailable = new List<RuleKey>();
        foreach (var incoming in source.Overrides)
        {
            RuleKey key = incoming.Key;
            if (!candidates.ContainsKey(key)) { unavailable.Add(key); continue; }
            int original = source.Default(key), current = targetBaseline.Desired(key);
            if (current == incoming.Value) continue;
            // A target override is explicit intent, even when it happens to equal the source default.
            if (current == original && current == targetBaseline.Default(key) && compatibility.DependenciesMatch(key, targetBaseline.Desired))
                changes.Add(new(key, current, incoming.Value));
            else
                conflicts.Add(new(key, original, targetBaseline.Default(key), current, incoming.Value));
        }
        return new(targetBaseline, [.. changes], [.. conflicts], [.. unavailable], compatibility);
    }
    internal static RuleProfile Rules(RuleProfile source, RuleProfile targetBaseline, SettingsMigrationPolicy? policy = null) =>
        PlanRules(source, targetBaseline, policy).Resolve();
}

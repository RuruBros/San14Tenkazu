using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace San14Tenkazu;

// Stable semantic keys only. A process address is deliberately not part of the saved profile.
internal sealed record RuleKey(string Table, int Record, string Field);
internal sealed record RuleDefault(RuleKey Key, int Value);
internal sealed record RuleOverride(RuleKey Key, int Value);
internal sealed record RuleChange(RuleKey Key, int Before, int After);
internal sealed record RuleCatalog(string Table, string Schema, string Fingerprint, int[] Records);
internal sealed record RuleProfile(int Version, string Build, string Schema, string Catalog, bool AutoApply,
    RuleDefault[] Defaults, RuleOverride[] Overrides, RuleCatalog[]? Catalogs = null)
{
    internal const int CurrentVersion = 2;
    internal const string CurrentSchema = "rules-v2";

    // Profiles replace these arrays on table additions and edits. Key the indexes by the array
    // itself so a record 'with' copy cannot retain another profile's defaults or overrides.
    // The weak keys also release old indexes once profiles (including Undo entries) expire.
    private static readonly ConditionalWeakTable<RuleDefault[], Dictionary<RuleKey, int>> defaultIndexes = new();
    private static readonly ConditionalWeakTable<RuleOverride[], Dictionary<RuleKey, int>> overrideIndexes = new();

    internal static RuleProfile Create(string build, string catalog, IEnumerable<RuleDefault> defaults) =>
        new(CurrentVersion, build, CurrentSchema, catalog, true, defaults.ToArray(), [],
            [new("formation", "formation-v1", catalog, FormationRules.EditableIds)]);

    // Version 1 had one catalog. Preserve its verified defaults, overrides and auto flag;
    // persist the upgrade only with a guarded save, never by recapturing formation values.
    internal RuleProfile Upgrade() => Version == 1 && Schema == "formation-v1" && Catalogs is null
        ? this with { Version = CurrentVersion, Schema = CurrentSchema,
            Catalogs = [new("formation", "formation-v1", Catalog, FormationRules.EditableIds)] } : this;

    internal RuleCatalog? Table(string table) => Catalogs?.SingleOrDefault(c => c.Table == table);
    internal static bool ValidKey(RuleKey? key) => FormationRules.ValidKey(key) || TacticRules.ValidKey(key) || PolicyRules.ValidKey(key) || OfficeRules.ValidKey(key) || RankRules.Spec.ValidKey(key) || DoctrineRules.Spec.ValidKey(key);
    internal static int Maximum(RuleKey key) => key.Table switch { "formation" => FormationRules.Field(key.Field).Maximum, "tactic" => TacticRules.Field(key.Field).Maximum, "office" => OfficeRules.Field(key.Field).Maximum, "rank" => RankRules.Spec.Field(key.Field).Maximum, "doctrine" => DoctrineRules.Spec.Field(key.Field).Maximum, _ => PolicyRules.Field(key).Maximum };
    private static int Minimum(RuleKey key) => key.Table == "doctrine" ? DoctrineRules.Spec.Field(key.Field).Minimum : 0;
    private static int StorageMax(RuleKey key) => key is {Table:"doctrine",Field:"effect"} ? short.MaxValue : key.Table == "policyeffect" || key is { Table: "office" or "rank", Field: "capacity" } ? ushort.MaxValue : byte.MaxValue;
    private static int FieldCount(string table) => table switch { "formation" => FormationRules.Fields.Length, "tactic" => TacticRules.Fields.Length, "office" => OfficeRules.Fields.Length, "rank" => RankRules.Spec.Fields.Length, "doctrine" => DoctrineRules.Spec.Fields.Length, _ => PolicyRules.Fields(table).Length };
    internal RuleProfile AddPlain(PlainRuleSnapshot snapshot)
    {
        if (Table(snapshot.Spec.Table) is not null) { snapshot.Check(this,Build); return this; }
        var result=this with {Catalogs=[.. Catalogs!,new(snapshot.Spec.Table,snapshot.Spec.Table+"-v1",snapshot.Catalog,snapshot.Records.Select(r => r.Id).ToArray())],
            Defaults=[.. Defaults,.. snapshot.Values.Select(v => new RuleDefault(v.Key,v.Value))]};
        result.Validate(); return result;
    }
    internal RuleProfile AddOffices(OfficeSnapshot snapshot)
    {
        if (Table("office") is not null) { snapshot.Check(this, Build); return this; }
        if (snapshot.Records.Any(r => !r.MirrorsAgree)) throw new InvalidDataException(L.Rules.WriteFailed);
        var result = this with { Catalogs = [.. Catalogs!, new("office", "office-v1", snapshot.Catalog, snapshot.Records.Select(r => r.Id).ToArray())],
            Defaults = [.. Defaults, .. snapshot.Values.Select(v => new RuleDefault(v.Key, v.Value))] };
        result.Validate(); return result;
    }
    internal RuleProfile AddPolicies(PolicySnapshot snapshot)
    {
        if (Table("policy") is not null || Table("policyeffect") is not null) { snapshot.Check(this, Build); return this; }
        if (snapshot.Records.Any(r => !r.MirrorsAgree)) throw new InvalidDataException(L.Rules.WriteFailed);
        var result = this with { Catalogs = [.. Catalogs!, .. snapshot.Catalogs], Defaults = [.. Defaults, .. snapshot.Values.Select(v => new RuleDefault(v.Key, v.Value))] };
        result.Validate(); return result;
    }
    internal RuleProfile AddTactics(TacticSnapshot snapshot)
    {
        if (Table("tactic") is not null) { snapshot.Check(this, Build); return this; }
        if (snapshot.Records.Any(r => !r.MirrorsAgree)) throw new InvalidDataException(L.Rules.WriteFailed);
        var result = this with { Catalogs = [.. Catalogs!, new("tactic", "tactic-v1", snapshot.Catalog, snapshot.Records.Select(r => r.Id).ToArray())],
            Defaults = [.. Defaults, .. snapshot.Values.Select(v => new RuleDefault(v.Key, v.Value))] };
        result.Validate();
        return result;
    }

    internal void Validate()
    {
        if (Version != CurrentVersion || Schema != CurrentSchema || Build is not { Length: 64 } || !Build.All(Uri.IsHexDigit)
            || Catalog is not { Length: 64 } || !Catalog.All(Uri.IsHexDigit) || Defaults is null || Overrides is null || Catalogs is null)
            throw new InvalidDataException(L.Rules.BadProfile);
        var tables = new HashSet<string>();
        foreach (RuleCatalog c in Catalogs)
            if (c is null || !tables.Add(c.Table) || c.Fingerprint is not { Length: 64 } || !c.Fingerprint.All(Uri.IsHexDigit)
                || c.Records is null || c.Records.Length == 0 || c.Records.Distinct().Count() != c.Records.Length
                || c.Table == "formation" && (c.Schema != "formation-v1" || c.Fingerprint != Catalog || !c.Records.SequenceEqual(FormationRules.EditableIds))
                || c.Table == "tactic" && (c.Schema != "tactic-v1" || c.Records.Any(id => id is < 1 or > 200))
                || c.Table == "policy" && (c.Schema != "policy-v1" || c.Records.Any(id => id is < 1 or > 120))
                || c.Table == "policyeffect" && (c.Schema != "policyeffect-v1" || c.Records.Any(id => id is < 1 or > 100))
                || c.Table == "office" && (c.Schema != "office-v1" || c.Records.Any(id => id is < 1 or > 255))
                || c.Table == "rank" && (c.Schema != "rank-v1" || c.Records.Any(id => id is < 1 or > 12))
                || c.Table == "doctrine" && (c.Schema != "doctrine-v1" || c.Records.Any(id => id is < 1 or > 79))
                || c.Table is not ("formation" or "tactic" or "policy" or "policyeffect" or "office" or "rank" or "doctrine")) throw new InvalidDataException(L.Rules.BadProfile);
        if (!tables.Contains("formation")) throw new InvalidDataException(L.Rules.BadProfile);
        if (tables.Contains("policy") != tables.Contains("policyeffect")) throw new InvalidDataException(L.Rules.BadProfile);
        var keys = new HashSet<RuleKey>();
        foreach (RuleDefault entry in Defaults)
            if (entry is null || !ValidKey(entry.Key) || !keys.Add(entry.Key) || Table(entry.Key.Table)?.Records.Contains(entry.Key.Record) is not true
                || entry.Value < Minimum(entry.Key) || entry.Value > StorageMax(entry.Key))
                throw new InvalidDataException(L.Rules.BadProfile);
        if (Defaults.Length != Catalogs.Sum(c => c.Records.Length * FieldCount(c.Table)))
            throw new InvalidDataException(L.Rules.BadProfile);
        var changed = new HashSet<RuleKey>();
        foreach (RuleOverride entry in Overrides)
            if (entry is null || !keys.Contains(entry.Key) || !changed.Add(entry.Key)
                || entry.Value < Minimum(entry.Key) || entry.Value > Maximum(entry.Key) || entry.Key.Table == "tactic" && entry.Key.Field == "ability" && entry.Value is not (1 or 2)
                || entry.Key is { Table: "office" or "rank", Field: "grade" } && entry.Value < 1
                || entry.Value == Default(entry.Key))
                throw new InvalidDataException(L.Rules.BadProfile);
        foreach (int record in Overrides.Where(o => o.Key.Table=="doctrine").Select(o => o.Key.Record).Distinct())
            if (!DoctrineRules.ValidCosts(field => Desired(new("doctrine",record,field)))) throw new InvalidDataException(L.Rules.BadProfile);
    }

    internal int Default(RuleKey key) => defaultIndexes.GetValue(Defaults, static entries => entries.ToDictionary(d => d.Key, d => d.Value))[key];
    internal int Desired(RuleKey key) => overrideIndexes.GetValue(Overrides, static entries => entries.ToDictionary(o => o.Key, o => o.Value))
        .TryGetValue(key, out int value) ? value : Default(key);
    internal bool SameAs(RuleProfile other) => Version == other.Version && Build == other.Build && Schema == other.Schema && Catalog == other.Catalog
        && AutoApply == other.AutoApply && Defaults.SequenceEqual(other.Defaults) && Overrides.SequenceEqual(other.Overrides)
        && Catalogs is not null && other.Catalogs is not null && Catalogs.Length == other.Catalogs.Length
        && Catalogs.Zip(other.Catalogs).All(pair => pair.First.Table == pair.Second.Table && pair.First.Schema == pair.Second.Schema
            && pair.First.Fingerprint == pair.Second.Fingerprint && pair.First.Records.SequenceEqual(pair.Second.Records));

    internal RuleProfile WithChanges(IEnumerable<RuleChange> changes, bool restoreDefaults = false)
    {
        var values = Overrides.ToDictionary(o => o.Key, o => o.Value);
        foreach (RuleChange change in changes)
        {
            if (!ValidKey(change.Key)) throw new InvalidDataException(L.Rules.BadProfile);
            int baseline = Default(change.Key);
            if (restoreDefaults || change.After == baseline) values.Remove(change.Key);
            else values[change.Key] = change.After;
        }
        RuleProfile result = this with { Overrides = values.OrderBy(v => v.Key.Table, StringComparer.Ordinal)
            .ThenBy(v => v.Key.Record).ThenBy(v => v.Key.Field, StringComparer.Ordinal).Select(v => new RuleOverride(v.Key, v.Value)).ToArray() };
        result.Validate();
        return result;
    }

    internal (RuleChange[] Apply, RuleKey[] Conflicts) Reapply(IReadOnlyDictionary<RuleKey, int> current)
    {
        var apply = new List<RuleChange>();
        var conflicts = new List<RuleKey>();
        foreach (RuleOverride value in Overrides)
        {
            if (!current.TryGetValue(value.Key, out int now)) { conflicts.Add(value.Key); continue; }
            if (now == value.Value) continue;
            if (now == Default(value.Key)) apply.Add(new(value.Key, now, value.Value));
            else conflicts.Add(value.Key);
        }
        return ([.. apply], [.. conflicts]);
    }
}

internal sealed record RuleProfileRead(RuleProfile? Profile, string Stamp);

// Optimistic file guard and a same-directory atomic rename. Malformed/incompatible files are never replaced.
internal sealed class RuleProfileFile(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private bool opened;
    private static string Stamp(byte[]? bytes) => bytes is null ? "" : Convert.ToHexString(SHA256.HashData(bytes));
    internal RuleProfileRead Read()
    {
        if (!File.Exists(path))
        {
            if (opened) throw new InvalidOperationException(L.Rules.ProfileMoved);
            return new(null, "");
        }
        byte[] bytes = File.ReadAllBytes(path);
        RuleProfile profile;
        try { profile = (JsonSerializer.Deserialize<RuleProfile>(bytes) ?? throw new InvalidDataException(L.Rules.BadProfile)).Upgrade(); }
        catch (JsonException ex) { throw new InvalidDataException(L.Rules.BadProfile, ex); }
        profile.Validate();
        opened = true;
        return new(profile, Stamp(bytes));
    }

    internal RuleProfileRead Save(RuleProfile profile, string expectedStamp)
    {
        profile.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Serializes all writers using this implementation, including another editor process.
        using var guard = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        byte[]? before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (opened && before is null || Stamp(before) != expectedStamp) throw new InvalidOperationException(L.Rules.ProfileMoved);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(profile, Json);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
            opened = true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(profile, Stamp(bytes));
    }

    // File and game form one user operation. If persistence fails after the memory write, restore memory;
    // rollback is independently guarded by the caller against table changes and third-party edits.
    internal RuleProfileRead Commit(RuleProfile profile, string expectedStamp, Action apply, Action rollback)
    {
        if (Read().Stamp != expectedStamp) throw new InvalidOperationException(L.Rules.ProfileMoved);
        apply();
        try { return Save(profile, expectedStamp); }
        catch (Exception failure)
        {
            try { rollback(); }
            catch (Exception recovery) { throw new AggregateException(L.Rules.RollbackFailed, failure, recovery); }
            throw;
        }
    }
}

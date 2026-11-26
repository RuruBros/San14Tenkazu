using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace San14Tenkazu;

// Stable semantic keys only. A process address is deliberately not part of the saved profile.
internal sealed record RuleKey(string Table, int Record, string Field);
internal sealed record RuleDefault(RuleKey Key, int Value);
internal sealed record RuleOverride(RuleKey Key, int Value);
internal sealed record RuleChange(RuleKey Key, int Before, int After);
internal sealed record RuleCatalog(string Table, string Schema, string Fingerprint, int[] Records, string? Identities = null)
{
    internal int IdentityWidth => Table switch { "tactic" => 12, "policy" => 13, "policyeffect" => 23, _ => 0 };

    internal bool VerifiedIdentities()
    {
        if (Identities is null) return true;
        int count = Table switch { "tactic" => 200, "policy" => 120, "policyeffect" => 100, _ => 0 };
        if (count == 0 || Identities.Length != count * IdentityWidth * 2 || !Identities.All(Uri.IsHexDigit)) return false;
        var bytes = Convert.FromHexString(Identities);
        using var canonical = new MemoryStream();
        for (int id = 1; id <= count; id++)
        {
            int at = (id - 1) * IdentityWidth;
            if (bytes[at] != id) return false;
            canonical.Write(bytes, at, Math.Min(13, IdentityWidth));
        }
        return Convert.ToHexString(SHA256.HashData(canonical.ToArray())) == Fingerprint;
    }
}
internal sealed record RuleProfile(int Version, string Build, string Schema, string Catalog, bool AutoApply,
    RuleDefault[] Defaults, RuleOverride[] Overrides, RuleCatalog[]? Catalogs = null, string? FormationIdentities = null)
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
        if (Table("policy") is not null || Table("policyeffect") is not null)
        {
            snapshot.Check(this, Build);
            var profile = this;
            foreach (var catalog in snapshot.Catalogs) profile = profile.WithCatalogIdentities(catalog.Table, catalog.Identities);
            return profile;
        }
        if (snapshot.Records.Any(r => !r.MirrorsAgree)) throw new InvalidDataException(L.Rules.WriteFailed);
        var result = this with { Catalogs = [.. Catalogs!, .. snapshot.Catalogs], Defaults = [.. Defaults, .. snapshot.Values.Select(v => new RuleDefault(v.Key, v.Value))] };
        result.Validate(); return result;
    }
    internal RuleProfile AddTactics(TacticSnapshot snapshot)
    {
        if (Table("tactic") is not null) { snapshot.Check(this, Build); return WithCatalogIdentities("tactic", snapshot.Identities); }
        if (snapshot.Records.Any(r => !r.MirrorsAgree)) throw new InvalidDataException(L.Rules.WriteFailed);
        var result = this with { Catalogs = [.. Catalogs!, new("tactic", "tactic-v1", snapshot.Catalog, snapshot.Records.Select(r => r.Id).ToArray(), snapshot.Identities)],
            Defaults = [.. Defaults, .. snapshot.Values.Select(v => new RuleDefault(v.Key, v.Value))] };
        result.Validate();
        return result;
    }

    private RuleProfile WithCatalogIdentities(string table, string? identities)
    {
        if (Table(table)?.Identities != null || identities == null) return this;
        return this with { Catalogs = Catalogs!.Select(c => c.Table == table ? c with { Identities = identities } : c).ToArray() };
    }

    internal void Validate() => Validate(fileProjection: false);

    // A disk projection can omit an unrecognized table. Saved/live profiles always require formation and the policy pair.
    internal void ValidateFileProjection() => Validate(fileProjection: true);

    private void Validate(bool fileProjection)
    {
        if (Version != CurrentVersion || Schema != CurrentSchema || Build is not { Length: 64 } || !Build.All(Uri.IsHexDigit)
            || Catalog is not { Length: 64 } || !Catalog.All(Uri.IsHexDigit) || Defaults is null || Overrides is null || Catalogs is null)
            throw new InvalidDataException(L.Rules.BadProfile);
        if (FormationIdentities is string identities)
        {
            if (identities.Length != FormationRules.EditableIds.Length * FormationRules.IdentityHexChars || !identities.All(Uri.IsHexDigit))
                throw new InvalidDataException(L.Rules.BadProfile);
            byte[] bytes = Convert.FromHexString(identities);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != Catalog || FormationRules.EditableIds.Any(id => bytes[(id - 1) * FormationRules.IdentityBytes] != id))
                throw new InvalidDataException(L.Rules.BadProfile);
        }
        var tables = new HashSet<string>();
        foreach (RuleCatalog c in Catalogs)
            if (c is null || !tables.Add(c.Table) || c.Fingerprint is not { Length: 64 } || !c.Fingerprint.All(Uri.IsHexDigit) || !c.VerifiedIdentities()
                || c.Records is null || c.Records.Length == 0 || c.Records.Distinct().Count() != c.Records.Length
                || c.Table == "formation" && (c.Schema != "formation-v1" || c.Fingerprint != Catalog || !c.Records.SequenceEqual(FormationRules.EditableIds))
                || c.Table == "tactic" && (c.Schema != "tactic-v1" || c.Records.Any(id => id is < 1 or > 200))
                || c.Table == "policy" && (c.Schema != "policy-v1" || c.Records.Any(id => id is < 1 or > 120))
                || c.Table == "policyeffect" && (c.Schema != "policyeffect-v1" || c.Records.Any(id => id is < 1 or > 100))
                || c.Table == "office" && (c.Schema != "office-v1" || c.Records.Any(id => id is < 1 or > 255))
                || c.Table == "rank" && (c.Schema != "rank-v1" || c.Records.Any(id => id is < 1 or > 12))
                || c.Table == "doctrine" && (c.Schema != "doctrine-v1" || c.Records.Any(id => id is < 1 or > 79))
                || c.Table is not ("formation" or "tactic" or "policy" or "policyeffect" or "office" or "rank" or "doctrine")) throw new InvalidDataException(L.Rules.BadProfile);
        if (fileProjection)
        {
            if (tables.Count == 0 || Overrides.Length != 0) throw new InvalidDataException(L.Rules.BadProfile);
        }
        else if (!tables.Contains("formation") || tables.Contains("policy") != tables.Contains("policyeffect"))
            throw new InvalidDataException(L.Rules.BadProfile);
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
                || entry.Value < Minimum(entry.Key) || entry.Value > (EditorValueRanges.IsFormationCombat(entry.Key) ? StorageMax(entry.Key) : Maximum(entry.Key))
                || entry.Key.Table == "tactic" && entry.Key.Field == "ability" && entry.Value is not (1 or 2)
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
        && FormationIdentities == other.FormationIdentities && AutoApply == other.AutoApply && Defaults.SequenceEqual(other.Defaults) && Overrides.SequenceEqual(other.Overrides)
        && Catalogs is not null && other.Catalogs is not null && Catalogs.Length == other.Catalogs.Length
        && Catalogs.Zip(other.Catalogs).All(pair => pair.First.Table == pair.Second.Table && pair.First.Schema == pair.Second.Schema
            && pair.First.Fingerprint == pair.Second.Fingerprint && pair.First.Identities == pair.Second.Identities && pair.First.Records.SequenceEqual(pair.Second.Records));

    internal RuleProfile WithChanges(IEnumerable<RuleChange> changes, bool restoreDefaults = false)
    {
        var values = Overrides.ToDictionary(o => o.Key, o => o.Value);
        foreach (RuleChange change in changes)
        {
            if (!ValidKey(change.Key)) throw new InvalidDataException(L.Rules.BadProfile);
            if (EditorValueRanges.PreserveFormation(this, change.Key, change.Before) || EditorValueRanges.UnconfirmedFormation(change.Key, change.After))
                throw new InvalidDataException(L.EditRanges.FormationHold);
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
            if (EditorValueRanges.PreserveFormation(this, value.Key, now)) { conflicts.Add(value.Key); continue; }
            if (now == Default(value.Key)) apply.Add(new(value.Key, now, value.Value));
            else conflicts.Add(value.Key);
        }
        if (apply.Any(c => c.Key.Table is "tactic" or "policyeffect"))
        {
            var candidates = apply.ToDictionary(c => c.Key, c => c.After);
            new RuleImportCompatibility(this, this).KeepCompatible(candidates, k => current.GetValueOrDefault(k, -1));
            conflicts.AddRange(apply.Where(c => !candidates.ContainsKey(c.Key)).Select(c => c.Key));
            apply.RemoveAll(c => !candidates.ContainsKey(c.Key));
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
    internal string FileName => Path.GetFileName(path);
    internal string StorageDirectory => Path.GetDirectoryName(Path.GetFullPath(path))!;
    internal RuleProfileFile ForBuild(GameDataProfile build) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $"rule_profiles.{build.ExeSha256}.json"));
    private string DatasetPath(string digest)
    {
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) throw new InvalidDataException(L.Rules.Incompatible);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
            $"{Path.GetFileNameWithoutExtension(path)}.{digest.ToUpperInvariant()}.json");
    }

    private static bool ScopedFileSelected(string scoped) => File.Exists(scoped) || File.Exists(scoped + ".lock");

    internal RuleProfileRead? UnboundLegacy(string digest) =>
        ScopedFileSelected(DatasetPath(digest)) || File.Exists(path + ".dataset") || !File.Exists(path) ? null : Read();

    internal RuleProfileFile ForDataset(string digest, RuleProfileRead? reuseLegacy)
    {
        string scoped = DatasetPath(digest);
        digest = digest.ToUpperInvariant();
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string owner = path + ".dataset";
        // Share the legacy writer lock so a concurrent import/edit cannot change what we just matched.
        using var guard = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(owner))
        {
            string recorded = File.ReadAllText(owner);
            if (recorded.Length != 64 || !recorded.All(Uri.IsHexDigit)) throw new InvalidDataException(L.Rules.BadProfile);
            if (!recorded.Equals(digest, StringComparison.OrdinalIgnoreCase))
            {
                if (reuseLegacy is not null) throw new IOException(L.Rules.ProfileMoved);
                return new(scoped);
            }
            if (!File.Exists(path)) throw new InvalidOperationException(L.Rules.ProfileMoved);
            return this;
        }
        if (ScopedFileSelected(scoped))
        {
            if (reuseLegacy is not null) throw new IOException(L.Rules.ProfileMoved);
            return new(scoped);
        }
        if (reuseLegacy is null)
        {
            // Reserve the scoped choice before the first verified baseline is written.
            // Another editor cannot claim the legacy file for this hash in between.
            using var reservation = new FileStream(scoped + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            return new(scoped);
        }
        if (Read().Stamp != reuseLegacy.Stamp || reuseLegacy.Profile is null)
            throw new IOException(L.Rules.ProfileMoved);
        string temporary = owner + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, digest);
            File.Move(temporary, owner);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return this;
    }
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

    internal RuleProfileRead Save(RuleProfile profile, string expectedStamp, bool preservePrevious = false)
    {
        profile.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Serializes all writers using this implementation, including another editor process.
        using var guard = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        byte[]? before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (opened && before is null || Stamp(before) != expectedStamp) throw new InvalidOperationException(L.Rules.ProfileMoved);
        if (preservePrevious && before is not null) SettingsHistory.Keep(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "settings-history"), before);
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

    internal RuleProfileRead Import(SettingsImportSource source, RuleProfile targetBaseline, string expectedStamp,
        SettingsMigrationPolicy? policy = null, IReadOnlySet<RuleKey>? takeIncoming = null)
    {
        if (Path.GetFullPath(path).Equals(source.PathName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.Compatibility.MigrationUnsupported);
        using var sourceGuard = source.Guard();
        var current = Read();
        if (current.Stamp != expectedStamp || current.Profile is null || !current.Profile.SameAs(targetBaseline)) throw new IOException(L.Rules.ProfileMoved);
        var migrated = SettingsMigration.PlanRules(source.Parse<RuleProfile>(), targetBaseline, policy).Resolve(takeIncoming);
        source.Archive(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "settings-history"));
        return Save(migrated, expectedStamp, preservePrevious: true);
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

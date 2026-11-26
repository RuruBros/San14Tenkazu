using System.Security.Cryptography;

namespace San14Tenkazu;

// Both profiles have passed their schema and catalog fingerprint checks before comparing semantic keys.
internal sealed class RuleImportCompatibility(RuleProfile source, RuleProfile target)
{
    // Old profiles saved only the hash of the complete catalog. Recover exactly the bytes covered by
    // that hash when an independently verified official snapshot supplies its preimage.
    private static readonly Lazy<Dictionary<(string Table, string Hash), string>> officialIdentities = new(() =>
    {
        var result = new Dictionary<(string, string), string>();
        foreach (var reference in OfficialFormation.Known)
        {
            foreach (var (table, rows) in reference.Tables!)
            {
                using var catalog = new MemoryStream();
                for (int id = 1; id < rows.Length; id++)
                {
                    byte[] name = Convert.FromHexString(rows[id].NameBytes);
                    catalog.WriteByte((byte)id);
                    catalog.Write(name, 0, table == "tactic" ? 10 : 12);
                    if (table == "tactic") catalog.WriteByte(rows[id].Kind);
                }
                Remember(table, catalog);
            }
            using var formations = new MemoryStream();
            foreach (var row in reference.Rows)
            {
                formations.WriteByte((byte)row.Id);
                formations.Write(Convert.FromHexString(row.NameBytes));
                formations.WriteByte(row.Kind);
            }
            Remember("formation", formations);
        }
        return result;

        void Remember(string table, MemoryStream stream)
        {
            byte[] bytes = stream.ToArray();
            result[(table, Convert.ToHexString(SHA256.HashData(bytes)))] = Convert.ToHexString(bytes);
        }
    });
    private readonly bool allCatalogsMatch = source.Catalogs!.All(c => target.Table(c.Table) is { } other
        && c.Schema == other.Schema && c.Fingerprint == other.Fingerprint && c.Records.SequenceEqual(other.Records));

    internal bool SameRow(string table, int id)
    {
        var a = source.Table(table);
        var b = target.Table(table);
        if (a == null || b == null || a.Schema != b.Schema || !a.Records.Contains(id) || !b.Records.Contains(id)) return false;
        if (ReferenceEquals(source, target)) return true;
        string? old = table == "formation" ? source.FormationIdentities : a.Identities;
        string? current = table == "formation" ? target.FormationIdentities : b.Identities;
        int full = table == "formation" ? FormationRules.IdentityHexChars : a.IdentityWidth * 2;
        int legacy = table == "policyeffect" ? 26 : full;
        int left = old == null ? legacy : full, right = current == null ? legacy : full;
        old ??= officialIdentities.Value.GetValueOrDefault((table, a.Fingerprint));
        current ??= officialIdentities.Value.GetValueOrDefault((table, b.Fingerprint));
        if (old == null || current == null)
        {
            if (a.Fingerprint != b.Fingerprint) return false;
            if (table != "policyeffect") return true;
            var available = old ?? current;
            return available != null && HasTerminator(available.AsSpan((id - 1) * (old == null ? right : left), legacy));
        }
        int width = Math.Min(left, right);
        var first = old.AsSpan((id - 1) * left, width);
        var second = current.AsSpan((id - 1) * right, width);
        if (width == 0 || !first.Equals(second, StringComparison.OrdinalIgnoreCase)) return false;
        if (width == full) return true;
        // A truncated legacy effect name establishes identity only if its UTF-16 terminator is present.
        return HasTerminator(first);

        static bool HasTerminator(ReadOnlySpan<char> identity)
        {
            for (int at = 2; at + 4 <= identity.Length; at += 4)
                if (identity.Slice(at, 4).SequenceEqual("0000")) return true;
            return false;
        }
    }

    internal bool RangeHeld(RuleKey key) => EditorValueRanges.IsFormationCombat(key)
        && (EditorValueRanges.PreserveFormation(source, key, source.Desired(key))
            || EditorValueRanges.PreserveFormation(target, key, target.Desired(key)));

    internal bool Eligible(RuleOverride edit)
    {
        var key = edit.Key;
        if (key.Table is not ("formation" or "tactic" or "policy" or "policyeffect")) return allCatalogsMatch;
        if (!SameRow(key.Table, key.Record)) return false;
        if (RangeHeld(key)) return false;
        // A slot stores an effect ID. Zero disconnects it. Preserve the meanings of both ends of the edit.
        if (key.Table != "policy") return true;
        int previous = source.Default(key);
        return (edit.Value == 0 || SameRow("policyeffect", edit.Value))
            && (previous == 0 || SameRow("policyeffect", previous));
    }

    internal bool DependenciesMatch(RuleKey key, Func<RuleKey, int> finalValue)
    {
        if (key.Table == "tactic")
        {
            var field = TacticRules.Field(key.Field);
            if (field.Slot < 0) return true;
            if (field.Id.StartsWith("kind", StringComparison.Ordinal))
            {
                foreach (var other in TacticRules.Fields.Where(f => f.Slot == field.Slot && f.Id != field.Id))
                {
                    var dependent = key with { Field = other.Id };
                    if (source.Desired(dependent) != finalValue(dependent)) return false;
                }
                return true;
            }
            var kind = key with { Field = $"kind{field.Slot + 1}" };
            return source.Desired(kind) == finalValue(kind);
        }
        if (key.Table != "policyeffect") return true;
        // A level affects every policy using this effect. Check the intended links in the combined profile,
        // including links imported with this very level, before carrying it across a changed policy catalog.
        if (source.Table("policy") is not { } old || target.Table("policy") is not { } current) return false;
        foreach (int policy in old.Records.Union(current.Records))
        foreach (var slot in PolicyRules.Effects)
        {
            var link = PolicyRules.Key(policy, slot);
            bool was = old.Records.Contains(policy) && source.Desired(link) == key.Record;
            int value = current.Records.Contains(policy) ? finalValue(link) : 0;
            if (value < 0) return false;
            bool now = value == key.Record;
            if (was != now || was && !SameRow("policy", policy)) return false;
        }
        return true;
    }

    internal void KeepCompatible(Dictionary<RuleKey, int> candidates, Func<RuleKey, int> existing)
    {
        int Combined(RuleKey key) => candidates.TryGetValue(key, out int value) ? value : existing(key);
        while (true)
        {
            var remove = candidates.Keys.Where(k => !DependenciesMatch(k, Combined)).ToArray();
            if (remove.Length == 0) return;
            foreach (var key in remove) candidates.Remove(key);
        }
    }
}

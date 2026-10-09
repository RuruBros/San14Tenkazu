using System.Text.Json;

namespace San14Tenkazu;

internal enum AiSupplyFailure { Return, Hold }
internal enum AiExtensionScope { AiAndDelegated, AiOnly }

internal sealed record AiExtensionSettings(bool SupplyProtection = false,
    AiSupplyFailure SupplyFailure = AiSupplyFailure.Return, AiExtensionScope Scope = AiExtensionScope.AiAndDelegated)
{
    internal static readonly AiExtensionSettings Default = new();
    internal void Validate()
    {
        if (!Enum.IsDefined(SupplyFailure) || !Enum.IsDefined(Scope)) throw new InvalidDataException(L.AiExtensions.InvalidSettings);
    }
    internal ulong Flags
    {
        get { Validate(); return (SupplyProtection ? 1UL : 0) | (SupplyFailure == AiSupplyFailure.Hold ? 2UL : 0) | (Scope == AiExtensionScope.AiAndDelegated ? 4UL : 0); }
    }
    internal static AiExtensionSettings FromFlags(ulong flags)
    {
        if ((flags & ~7UL) != 0) throw new InvalidDataException(L.AiExtensions.InvalidSettings);
        return new((flags & 1) != 0, (flags & 2) != 0 ? AiSupplyFailure.Hold : AiSupplyFailure.Return,
            (flags & 4) != 0 ? AiExtensionScope.AiAndDelegated : AiExtensionScope.AiOnly);
    }
}

internal sealed record AiExtensionStatus(AiExtensionSettings Settings, ulong Checks = 0, ulong RecoveryPlans = 0,
    ulong Returns = 0, ulong Holds = 0, ulong LastUnit = 0, ulong LastOutcome = 0, ulong Entries = 0);

// Versioned and bound to the actual behaviour-tree file. Unknown/corrupt settings never turn a condition on.
internal sealed class AiExtensionSettingsFile(string path)
{
    private sealed record Saved(int Version, string Stamp, AiExtensionSettings Settings);
    internal AiExtensionSettings Load(string stamp)
    {
        if (!File.Exists(path)) return AiExtensionSettings.Default;
        try
        {
            Saved? saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path));
            if (saved is null || saved.Version != 1 || saved.Stamp != stamp || saved.Settings is null)
                throw new InvalidDataException(L.AiExtensions.InvalidSettings);
            saved.Settings.Validate();
            return saved.Settings;
        }
        catch (JsonException ex) { throw new InvalidDataException(L.AiExtensions.InvalidSettings, ex); }
    }
    internal void Save(string stamp, AiExtensionSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Saved(1, stamp, settings), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

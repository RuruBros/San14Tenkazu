using System.Text.Json;

namespace San14Tenkazu;

// User intent persists across processes; allocation ownership remains in per-process receipts.
internal sealed record RuntimeRulePreference(int Version, string Build, RuntimeRuleSettings Settings, bool ConvertExperience)
{
    internal void Validate()
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(Build) || Settings is null)
            throw new InvalidDataException("개성·능력치 저장 설정 형식이 잘못됐습니다.");
        Settings.Validate();
    }
    internal void CheckBuild(string build)
    {
        Validate();
        if (!Build.Equals(build, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("개성·능력치 설정을 저장한 게임 빌드와 현재 빌드가 다릅니다.");
    }
}

internal sealed class RuntimeRulePreferenceFile(string directory)
{
    internal string PathName => Path.Combine(directory, "settings.json");
    internal string ReceiptPath(GameIdentity identity) => Path.Combine(directory, $"{identity.ProcessId}-{identity.StartTimeUtc.Ticks}.json");
    internal RuntimeRulePreference? Read()
    {
        if (!File.Exists(PathName)) return null;
        var value = JsonSerializer.Deserialize<RuntimeRulePreference>(File.ReadAllText(PathName))
            ?? throw new InvalidDataException("개성·능력치 저장 설정이 비어 있습니다.");
        value.Validate(); return value;
    }
    internal string Stamp() => File.Exists(PathName) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(PathName))) : "";
    internal void Save(RuntimeRulePreference value, bool preservePrevious = false, string? expectedStamp = null)
    {
        value.Validate(); Directory.CreateDirectory(directory);
        using var guard = new FileStream(PathName+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose);
        byte[]? before=File.Exists(PathName)?File.ReadAllBytes(PathName):null;
        string stamp=before is null?"":Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before));
        if(expectedStamp is not null && stamp!=expectedStamp)throw new IOException(L.Rules.ProfileMoved);
        if (preservePrevious && before is not null) SettingsHistory.Keep(Path.Combine(directory, "settings-history"), before);
        string temporary = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            File.Move(temporary, PathName, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal RuntimeRulePreference? AdoptCommitted(GameIdentity identity, RuntimeRuleSettings? current)
    {
        var existing = Read(); if (existing is not null) return existing;
        var receipt = new RuntimeRuleJournal(ReceiptPath(identity)).Read();
        if (receipt is null || !identity.Verified || !receipt.Owns(identity) || receipt.Phase != "committed" || receipt.After != current) return null;
        var value = new RuntimeRulePreference(1, identity.ExeHash, receipt.After, true);
        Save(value); return value;
    }
}

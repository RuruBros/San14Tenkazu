using System.Text.Json;

namespace San14Tenkazu;

internal sealed record RuntimeRuleReceipt(int ProcessId, DateTime StartTimeUtc, ulong ModuleBase, string Build,
    RuntimeRuleSettings Before, RuntimeRuleSettings After, string Phase, RuntimeRuleWrite[] Writes, ulong TraitListAllocation = 0)
{
    internal bool Owns(GameIdentity game) => ProcessId == game.ProcessId && StartTimeUtc == game.StartTimeUtc
        && ModuleBase == game.ModuleBase && Build == game.ExeHash;
    internal RuntimeRuleSettings ExpectedFor(GameIdentity game) => !Owns(game) ? RuntimeRuleSettings.Original : Phase switch
    {
        "committed" => After,
        "rolled-back" => Before,
        _ => throw new InvalidOperationException(L.RuntimeErrors.RecoveryIncomplete)
    };
}

internal sealed class RuntimeRuleJournal(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal RuntimeRuleReceipt? Read() => File.Exists(path)
        ? JsonSerializer.Deserialize<RuntimeRuleReceipt>(File.ReadAllText(path), Json) ?? throw new InvalidDataException(L.RuntimeErrors.EmptyRecoveryRecord) : null;
    internal void Save(RuntimeRuleReceipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, receipt, Json);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

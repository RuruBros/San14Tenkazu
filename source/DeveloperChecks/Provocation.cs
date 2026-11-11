#if LOCAL_DEVELOPER
using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu;

internal static class DeveloperProvocationCli
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true };
    internal static int Run(string[] args)
    {
        string Required(string key) => args.SingleOrDefault(a => a.StartsWith("--" + key + "=", StringComparison.Ordinal))?[(key.Length + 3)..]
            ?? throw new ArgumentException("Required --" + key);
        string mode = args[0];
        if (mode is not ("provocation-apply" or "provocation-finish" or "provocation-restore")) throw new ArgumentException("Unknown provocation phase");
        if (mode == "provocation-finish" && !args.Contains("--confirmed=true", StringComparer.Ordinal))
            throw new ArgumentException("User confirmation is required before decreasing or undoing the held state");
        int pid = int.Parse(Required("pid"));
        string output = Path.GetFullPath(Required("output"));
        if (Path.Exists(output)) throw new IOException("Use a new evidence directory");
        Directory.CreateDirectory(output);
        using var file = new FileStream(Path.Combine(output, "evidence.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var log = new StreamWriter(file) { AutoFlush = true };
        void Record(object row) { log.WriteLine(JsonSerializer.Serialize(row, Json)); file.Flush(true); }
        void Save<T>(string name, T value)
        {
            using var stream = new FileStream(Path.Combine(output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, value, Json); stream.Flush(true);
        }
        var identity = GameMemory.FindGame();
        if (identity.ProcessId != pid) throw new InvalidOperationException("PID changed");
        Save("runner.json", new { utc = DateTime.UtcNow, mode, identity.ProcessId, identity.StartTimeUtc, identity.ExeHash,
            coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(GameMemory).Assembly.Location))), screenAccess = false });
        try
        {
            if (mode == "provocation-apply")
            {
                var plan = JsonSerializer.Deserialize<GameMemory.ProvocationPlan>(File.ReadAllText(Required("plan")), Json)!;
                Save("plan.json", plan);
                var receipt = GameMemory.BeginDeveloperProvocation(identity, GameMemory.ReadCurrentLayout(identity), plan,
                    value => Save("receipt.json", value), Record);
                Save("result.json", new { state = "held-for-user-confirmation", duration = Convert.FromHexString(plan.HeldHex)[0x25],
                    targetKind = 27, targetNumber = plan.SourceSlot, plan.SourceName, receipt = Path.Combine(output, "receipt.json") });
                Console.WriteLine("HELD for user confirmation; receipt=" + Path.Combine(output, "receipt.json"));
            }
            else
            {
                var receipt = JsonSerializer.Deserialize<GameMemory.ProvocationReceipt>(File.ReadAllText(Required("receipt")), Json)!;
                if (receipt.Identity != identity) throw new InvalidOperationException("Receipt process identity changed");
                var result = GameMemory.FinishDeveloperProvocation(receipt, decrease: mode == "provocation-finish", Record);
                Save("result.json", result);
                Console.WriteLine(JsonSerializer.Serialize(result, Json));
                return result.Restored && result.SnapshotDifferences == 0 ? 0 : 2;
            }
            return 0;
        }
        catch (Exception error) { try { Record(new { stage = "failed", error = error.ToString() }); } catch { } throw; }
    }
}
#endif

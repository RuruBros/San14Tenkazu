using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using San14Tenkazu;

internal static class DeveloperChecksProgram
{
    private static readonly JsonSerializerOptions Json = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true };

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--build-check" && bool.TryParse(args[1], out bool expectedFlavor))
        {
#if LOCAL_DEVELOPER
            const bool compiled = true;
#else
            const bool compiled = false;
#endif
            string[] methods = ["PrepareCompleteEditorTrials", "PlanCompleteEditorTrials", "RoundTripCompleteEditorFields", "RunCompleteEditTrial", "PrepareSeededProvocationTrial", "BeginDeveloperProvocation", "FinishDeveloperProvocation"];
            bool matches = compiled == expectedFlavor && methods.All(name => typeof(GameMemory)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Any(m => m.Name == name) == expectedFlavor);
            Console.WriteLine(JsonSerializer.Serialize(new { compiled, expected = expectedFlavor, matches, gameAccess = false, screenAccess = false }, Json));
            return matches ? 0 : 1;
        }
#if LOCAL_DEVELOPER
        try
        {
            string mode = args.FirstOrDefault() ?? "";
            if (mode.StartsWith("provocation-", StringComparison.Ordinal)) return DeveloperProvocationCli.Run(args);
            if (mode is not ("preview" or "run")) throw new ArgumentException("Use preview or run; neither mode opens a window.");
            string Required(string key) => args.FirstOrDefault(a => a.StartsWith("--" + key + "=", StringComparison.Ordinal))?[(key.Length + 3)..]
                ?? throw new ArgumentException("Required --" + key);
            int pid = int.Parse(Required("pid"));
            string output = Path.GetFullPath(Required("output"));
            if (Path.Exists(output)) throw new IOException("Use a new output directory; preserve previous evidence.");
            Directory.CreateDirectory(output);
            var identity = GameMemory.FindGame();
            if (identity.ProcessId != pid) throw new InvalidOperationException("Observed PID differs from the explicitly requested PID.");
            var layout = GameMemory.ReadCurrentLayout(identity);
            var prepared = GameMemory.PrepareCompleteEditorTrials(identity, layout);
            string? seededCommander = args.FirstOrDefault(a => a.StartsWith("--seed-provoke-commander=", StringComparison.Ordinal))?[25..];
            if (seededCommander is not null)
                prepared = GameMemory.PrepareSeededProvocationTrial(prepared, int.Parse(seededCommander));
            var manifest = JsonSerializer.SerializeToNode(GameMemory.CompleteEditorManifest(prepared), Json)!;
            using (var file = new FileStream(Path.Combine(output, "targets.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { JsonSerializer.Serialize(file, manifest, Json); file.Flush(true); }
            var data = prepared.Snapshot;
            File.WriteAllText(Path.Combine(output, "availability.json"), JsonSerializer.Serialize(new
            {
                gameAccess = true, screenAccess = false, data.PlayerForce, data.Date, data.Hour,
                gates = data.GatesOf(data.PlayerForce).OrderBy(g => g.Id).Select(g => new
                {
                    g.Id, g.Name, g.Governor, governorName = data.OfficerName(g.Governor),
                    officers = data.Officers.Where(o => o.BaseId == g.Id || o[OfficerFields.PlaceField] == g.Id).Select(o => new
                    {
                        o.Id, o.Name, o.Status, o.BaseId, place = o[OfficerFields.PlaceField],
                        force = data.ForceOfCorps(o.Corps), differentGovernor = o.Id != g.Governor,
                        mayGovern = GateEdits.MayGovern(data, g, o.Id)
                    })
                })
            }, Json));
            File.WriteAllText(Path.Combine(output, "runner.json"), JsonSerializer.Serialize(new
            {
                utc = DateTime.UtcNow, mode, identity.ProcessId, identity.StartTimeUtc, identity.ExeHash,
                coreModuleVersionId = typeof(GameMemory).Module.ModuleVersionId,
                coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(GameMemory).Assembly.Location))),
                coreAssembly = typeof(GameMemory).Assembly.Location, commandLine = args, screenAccess = false
            }, Json));
            Console.WriteLine($"Prepared PID={pid}; trials={prepared.Plan.Trials.Length}; missing={prepared.Plan.Missing.Length}; {output}");
            if (mode == "preview") return 0;
            var expected = JsonNode.Parse(File.ReadAllText(Required("expected-manifest")))!;
            foreach (string key in new[] { "processId", "startTimeUtc", "exeHash", "playerForce", "date", "hour", "runtimeRules", "trials", "missing" })
                if (!JsonNode.DeepEquals(expected[key], manifest[key])) throw new InvalidOperationException("Preview intent changed: " + key);
            string? keys = args.FirstOrDefault(a => a.StartsWith("--keys=", StringComparison.Ordinal))?[7..];
            if (keys is not null)
            {
                var selected = keys.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
                if (selected.Count == 0 || selected.Any(key => prepared.Plan.Trials.All(t => t.Key != key)))
                    throw new ArgumentException("Requested trial key is missing or unavailable.");
                prepared = prepared with { Plan = new(prepared.Plan.Trials.Where(t => selected.Contains(t.Key)).ToArray(), prepared.Plan.Missing) };
            }
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler stop = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += stop;
            string stopFile = Path.Combine(output, "stop.request");
            using var monitorCancellation = new CancellationTokenSource();
            var monitor = Task.Run(async () =>
            {
                try
                {
                    while (!monitorCancellation.IsCancellationRequested)
                    {
                        if (File.Exists(stopFile)) { cancellation.Cancel(); return; }
                        await Task.Delay(100, monitorCancellation.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
            using var stream = new FileStream(Path.Combine(output, "roundtrip.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var log = new StreamWriter(stream) { AutoFlush = true };
            void Record(object row)
            {
                log.WriteLine(JsonSerializer.Serialize(row, Json)); stream.Flush(true);
                if (row is GameMemory.CompleteTrialEvidence evidence && evidence.Stage is "restored-byte-exact" or "recovery-failed")
                    Console.WriteLine(evidence.Stage + " " + evidence.Key);
            }
            try
            {
                var result = GameMemory.RoundTripCompleteEditorFields(prepared, Record, cancellation.Token);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, Json));
                Console.WriteLine(JsonSerializer.Serialize(result, Json));
                return result.Cancelled || result.SnapshotDifferences != 0 ? 2 : 0;
            }
            catch (Exception error)
            {
                try { Record(new { stage = "failed", error = error.ToString() }); } catch { }
                throw;
            }
            finally
            {
                Console.CancelKeyPress -= stop; monitorCancellation.Cancel(); monitor.GetAwaiter().GetResult();
            }
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
#else
        Console.Error.WriteLine("This runner requires an explicitly authorized local developer build.");
        return 3;
#endif
    }
}

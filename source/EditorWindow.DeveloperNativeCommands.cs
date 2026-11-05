#if LOCAL_DEVELOPER
using System.Security.Cryptography;
using System.Text.Json;
using W = System.Windows;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private sealed class NativeCommandSuite(string journal, GameIdentity game)
    {
        private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
        private long recordedLength = new FileInfo(journal).Length;
        internal string Journal => journal;
        internal GameIdentity Game => game;
        internal int Completed;
        internal bool AwaitingReload, Executing;
        internal GameSnapshot? Armed;
        internal int CameraTile;
        internal NativeCommandKind Kind => NativeCommandSequence[Completed];
        internal void Record(object row)
        {
            using var stream = new FileStream(journal, FileMode.Open, FileAccess.Write, FileShare.Read);
            if (stream.Length != recordedLength) throw new IOException(L.Errors.StateMoved);
            stream.Position = recordedLength;
            using (var output = new StreamWriter(stream, leaveOpen: true)) output.WriteLine(JsonSerializer.Serialize(row, Json));
            recordedLength = stream.Position;
        }
    }

    private static readonly NativeCommandKind[] NativeCommandSequence =
        [NativeCommandKind.Camera, NativeCommandKind.Calendar, NativeCommandKind.Item, NativeCommandKind.Alliance,
         NativeCommandKind.Give, NativeCommandKind.Capture, NativeCommandKind.Transfer, NativeCommandKind.Revival,
         NativeCommandKind.Independence, NativeCommandKind.Surrender, NativeCommandKind.Demo];
    private NativeCommandSuite? nativeCommandSuite;
    private (GameIdentity Game, DateTime Utc)? nativeCommandTitle;

    private static string NativeCommandLabel(NativeCommandKind kind) => kind switch
    {
        NativeCommandKind.Camera => L.DeveloperUi.NativeCamera,
        NativeCommandKind.Calendar => L.Calendar.Title,
        NativeCommandKind.Item => L.DeveloperUi.NativeItem,
        NativeCommandKind.Alliance => L.DeveloperUi.NativeAlliance,
        NativeCommandKind.Give => L.Give.Button,
        NativeCommandKind.Capture => L.DeveloperUi.NativeCapture,
        NativeCommandKind.Transfer => L.DeveloperUi.NativeTransfer,
        NativeCommandKind.Revival => L.DeveloperUi.NativeRevival,
        NativeCommandKind.Independence => L.Independence.Button,
        NativeCommandKind.Surrender => L.Surrender.Button,
        _ => L.DeveloperUi.NativeDemo
    };

    private void UpdateNativeCommandSuiteControls(DisasterTestWindow window, bool available)
    {
        var suite = nativeCommandSuite;
        window.NativeCommandSuite.Text = suite?.Armed is not null ? L.DeveloperUi.NativeSuiteCancel
            : suite is not null && game == suite.Game ? L.DeveloperUi.NativeSuiteContinue : L.DeveloperUi.NativeSuite;
        window.NativeCommandSuite.IsEnabled = available && !refreshing && (suite?.Armed is not null ||
            (suite is not { AwaitingReload: true } || game != suite.Game) &&
            game is { } identity && GameCommandProfile.Find(identity) is not null &&
            snapshot?.Session is { IsPlaying: true } && layout is not null && !HasPendingEdits && !RulesPending);
    }

    private void ObserveNativeSuiteSession(GameIdentity identity, GameSessionObservation session)
    {
        if (session.Phase != GameSessionPhase.Title) return;
        nativeCommandTitle = (identity, DateTime.UtcNow);
        if (nativeCommandSuite is not { } suite || suite.Game != identity) return;
        suite.Armed = null;
        suite.AwaitingReload = false;
    }

    private static NativeCommandSuite? FindNativeCommandContinuation(string directory, GameIdentity game, DateTime titleUtc)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "native-commands-*.jsonl").OrderDescending(StringComparer.Ordinal))
        {
            using var lines = File.ReadLines(path).GetEnumerator();
            if (!lines.MoveNext()) continue;
            try
            {
                using var header = JsonDocument.Parse(lines.Current);
                var start = header.RootElement;
                if (start.GetProperty("stage").GetString() != "started" ||
                    start.GetProperty("ProcessId").GetInt32() != game.ProcessId ||
                    start.GetProperty("ExeHash").GetString() != game.ExeHash ||
                    start.GetProperty("StartTimeUtc").GetDateTime() != game.StartTimeUtc) continue;
                if (File.GetLastWriteTimeUtc(path) >= titleUtc || !start.GetProperty("commands").EnumerateArray()
                    .Select(value => value.GetString()).SequenceEqual(NativeCommandSequence.Select(kind => kind.ToString()))) return null;

                int completed = 0;
                bool inFlight = false;
                while (lines.MoveNext())
                {
                    using var entry = JsonDocument.Parse(lines.Current);
                    var row = entry.RootElement;
                    string? stage = row.GetProperty("stage").GetString();
                    if (completed == NativeCommandSequence.Length ||
                        row.GetProperty("command").GetString() != NativeCommandSequence[completed].ToString()) return null;
                    switch (stage)
                    {
                        case "armed" or "disarmed" when !inFlight: break;
                        case "before" when !inFlight: inFlight = true; break;
                        case "after" when inFlight: inFlight = false; completed++; break;
                        case "failed" when inFlight: inFlight = false; break;
                        default: return null;
                    }
                }
                return !inFlight && completed > 0 && completed < NativeCommandSequence.Length
                    ? new NativeCommandSuite(path, game) { Completed = completed } : null;
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }
        return null;
    }

    private async Task StartNativeCommandSuiteStepAsync()
    {
        if (developerMenu is not { } window || busy || developerChanging) return;
        if (nativeCommandSuite is { Armed: not null } armed)
        {
            armed.Armed = null;
            armed.Record(new { stage = "disarmed", command = armed.Kind.ToString() });
            window.AppendCompatibility(L.DeveloperUi.NativeSuiteDisarmed);
            UpdateDeveloperControls();
            return;
        }
        if (game is not { } identity || GameCommandProfile.Find(identity) is null || layout is not { } found ||
            HasPendingEdits || RulesPending ||
            nativeCommandSuite is { AwaitingReload: true } awaiting && awaiting.Game == identity) return;
        developerChanging = true;
        SetBusy(true, scanning: false);
        try
        {
            if (nativeCommandSuite is null || nativeCommandSuite.Game != identity)
            {
                if (W.MessageBox.Show(window, L.DeveloperUi.NativeSuiteConfirm, L.DeveloperUi.NativeSuite,
                    W.MessageBoxButton.YesNo, W.MessageBoxImage.Warning, W.MessageBoxResult.No) != W.MessageBoxResult.Yes) return;
                string directory = Path.Combine(seasonStorageRoot, "compatibility");
                Directory.CreateDirectory(directory);
                nativeCommandSuite = nativeCommandTitle is { } title && title.Game == identity
                    ? await Task.Run(() => FindNativeCommandContinuation(directory, identity, title.Utc)) : null;
                if (nativeCommandSuite is null)
                {
                    string journal = Path.Combine(directory, $"native-commands-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jsonl");
                    using (new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
                    nativeCommandSuite = new(journal, identity);
                    nativeCommandSuite.Record(new { stage = "started", identity.ProcessId, identity.ExeHash, identity.StartTimeUtc,
                        commands = NativeCommandSequence.Select(kind => kind.ToString()).ToArray() });
                }
                window.AppendCompatibility(nativeCommandSuite.Journal);
            }
            var suite = nativeCommandSuite;
            var fresh = await Task.Run(() => ReadNativeTrialSnapshot(identity, found, requirePause: true));
            suite.Record(new { stage = "armed", command = suite.Kind.ToString(), fresh.Date, fresh.Hour, fresh.PlayerForce });
            suite.Armed = fresh;
            window.AppendCompatibility(L.DeveloperUi.NativeSuiteStep(suite.Completed + 1, NativeCommandSequence.Length,
                NativeCommandLabel(suite.Kind)));
        }
        catch (Exception error) { window.AppendCompatibility(L.DeveloperUi.ExecutionFailed + error.Message); }
        finally
        {
            developerChanging = false;
            SetBusy(false, scanning: false);
            UpdateDeveloperControls();
        }
    }

    private static GameSnapshot ReadNativeTrialSnapshot(GameIdentity identity, GameLayout found, bool requirePause)
    {
        if (!found.SameStorage(GameMemory.ReadCurrentLayout(identity))) throw new InvalidOperationException(L.Errors.StateMoved);
        if (requirePause && GameMemory.ReadPace(identity, found) != GamePace.Paused)
            throw new InvalidOperationException(L.DeveloperUi.PauseRequired);
        var data = GameMemory.ReadSnapshot(identity, found);
        if (data.Session is not { IsPlaying: true, Entries.Length: >= 3 } || data.Date is null)
            throw new InvalidOperationException(L.Errors.StateMoved);
        return data;
    }

    private async Task<NativeCommandSuite?> BeginNativeCommandTrialAsync(NativeCommandKind? kind, GameIdentity identity,
        GameLayout found, object target, bool single = true, bool restoreGivenBaseValues = false)
    {
        if (nativeCommandSuite is not { Armed: { } baseline } suite) return null;
        if (suite.Executing && suite.Kind == NativeCommandKind.Give && kind is null && restoreGivenBaseValues) return null;
        if (suite.Executing || kind != suite.Kind || !single)
            throw new InvalidOperationException(L.DeveloperUi.NativeSuiteWrongCommand(NativeCommandLabel(suite.Kind)));
        var fresh = await Task.Run(() => ReadNativeTrialSnapshot(identity, found, requirePause: true));
        if (game != identity || suite.Game != identity || !ReferenceEquals(suite.Armed, baseline) ||
            baseline.Session!.Entries[2].Address != fresh.Session!.Entries[2].Address ||
            fresh.Date != baseline.Date || fresh.Hour != baseline.Hour || fresh.PlayerForce != baseline.PlayerForce)
            throw new InvalidOperationException(L.Errors.StateMoved);
        GameMemory.NativeTrialCamera? camera = null;
        if (kind == NativeCommandKind.Camera && target is int tile)
        {
            camera = await Task.Run(() => GameMemory.ReadNativeTrialCamera(identity, tile, requireIdle: true));
            if (camera.AtTarget) throw new InvalidOperationException(L.DeveloperUi.NativeCameraOtherTile);
            suite.CameraTile = tile;
        }
        suite.Record(new { stage = "before", command = kind.ToString(), target, state = NativeTrialState(fresh), camera });
        suite.Armed = fresh;
        suite.Executing = true;
        return suite;
    }

    private async Task FinishNativeCommandTrialAsync(NativeCommandSuite suite, GameIdentity identity, GameLayout found)
    {
        var before = suite.Armed ?? throw new InvalidOperationException(L.Errors.StateMoved);
        var kind = suite.Kind;
        GameMemory.NativeTrialCamera? camera = null;
        if (kind == NativeCommandKind.Camera)
        {
            camera = await Task.Run(() =>
            {
                var wait = System.Diagnostics.Stopwatch.StartNew();
                GameMemory.NativeTrialCamera current;
                do
                {
                    Thread.Sleep(100);
                    // The camera can hold new requests while the current flight is in progress.
                    current = GameMemory.ReadNativeTrialCamera(identity, suite.CameraTile, requireIdle: false);
                } while (!current.AtTarget && wait.ElapsedMilliseconds < 2500);
                return current;
            });
            if (!camera.AtTarget)
                throw new InvalidOperationException(L.DeveloperUi.NativeCameraUnconfirmed(camera.X, camera.Z, camera.TargetX, camera.TargetZ));
        }
        var after = await Task.Run(() => ReadNativeTrialSnapshot(identity, found, requirePause: false));
        if (before.Session!.Entries[2].Address != after.Session!.Entries[2].Address ||
            (kind != NativeCommandKind.Calendar && after.Date != before.Date) || after.Hour != before.Hour ||
            (kind != NativeCommandKind.Demo && after.PlayerForce != before.PlayerForce))
            throw new InvalidOperationException(L.Errors.StateMoved);
        suite.Record(new { stage = "after", command = kind.ToString(), state = NativeTrialState(after), camera });
        if (suite.Completed + 1 == NativeCommandSequence.Length)
            suite.Record(new { stage = "all-confirmed", passed = suite.Completed + 1 });
        suite.Armed = null;
        suite.Executing = false;
        suite.Completed++;
        if (suite.Completed == NativeCommandSequence.Length)
        {
            nativeCommandSuite = null;
            developerMenu?.AppendCompatibility(L.DeveloperUi.NativeSuiteDone(suite.Completed));
        }
        else
        {
            suite.AwaitingReload = kind != NativeCommandKind.Camera;
            developerMenu?.AppendCompatibility(suite.AwaitingReload ? L.DeveloperUi.NativeSuiteReload : L.DeveloperUi.NativeSuiteNext);
        }
        UpdateDeveloperControls();
    }

    private void FailNativeCommandTrial(NativeCommandSuite suite, Exception error)
    {
        suite.Armed = null;
        suite.Executing = false;
        suite.AwaitingReload = suite.Kind != NativeCommandKind.Camera;
        suite.Record(new { stage = "failed", command = suite.Kind.ToString(), error = error.ToString() });
        developerMenu?.AppendCompatibility(L.DeveloperUi.ExecutionFailed + error.Message + " " + suite.Journal);
    }

    private static object NativeTrialState(GameSnapshot data)
    {
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        return new
        {
            data.Date, data.Hour, data.PlayerForce, data.IsDemoPlay,
            session = data.Session!.Entries[2].Address,
            cities = data.CityIds.Order().Select(id => new { id, owner = data.ForceOfCity(id), hash = Hash(data.Cities[id].Data) }).ToArray(),
            gates = data.Gates.OrderBy(gate => gate.Id).Select(gate => new { gate.Id, owner = data.ForceOfGate(gate), hash = Hash(gate.Data) }).ToArray(),
            forces = data.ForceRecords.Values.OrderBy(force => force.Id).Select(force => new { force.Id, force.Ruler, hash = Hash(force.Data) }).ToArray(),
            officers = data.Officers.OrderBy(officer => officer.Id).Select(officer => new { officer.Id, officer.Corps, officer.BaseId, officer.Status, hash = Hash(officer.Data) }).ToArray(),
            units = data.Units.OrderBy(unit => unit.Slot).Select(unit => new { unit.Slot, unit.Tile, hash = Hash(unit.Data) }).ToArray(),
            items = data.Items.OrderBy(item => item.Index).Select(item => new { item.Index, item.Owner, item.State, hash = Hash(item.Data) }).ToArray()
        };
    }
}
#endif

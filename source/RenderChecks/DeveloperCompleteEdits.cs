#if LOCAL_DEVELOPER
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;

internal static partial class RenderChecks
{
    private static void CheckCompleteEditorTrials()
    {
        var data = EditorCoverageFixture.Snapshot();
        // A lower-ID always-on trait must not hide the holder-restart branch in real saves.
        var originalPersonality = data.Personalities[0];
        var alwaysBytes = (byte[])originalPersonality.Data.Clone();
        BitConverter.GetBytes((short)-1).CopyTo(alwaysBytes, PersonalityFields.DurationOffset);
        EditorCoverageFixture.Put(alwaysBytes, PersonalityFields.Condition, 0);
        var whileMet = originalPersonality with { Index = 2, Address = originalPersonality.Address + 0x10000 };
        typeof(GameSnapshot).GetProperty("Personalities", Hidden)!.SetValue(data,
            new[] { originalPersonality with { Data = alwaysBytes }, whileMet });
        foreach (int id in new[] { 5, 6 })
        {
            EditorCoverageFixture.Put(data.OfficerById[id].Data, OfficerFields.Personalities[0], 2);
            EditorCoverageFixture.Put(data.OfficerById[id].Data, OfficerFields.Personalities[1], 1);
            EditorCoverageFixture.Put(data.OfficerById[id].Data, OfficerFields.PersonalitiesInEffect, 1);
        }
        EditorCoverageFixture.Put(data.OfficerById[3].Data, OfficerFields.ServiceValue, 0xABCD);
        EditorCoverageFixture.Put(data.OfficerById[3].Data, OfficerFields.Merit, 12345);
        var names = new[] { new GameName(1, "one", ""), new GameName(2, "two", "") };
        var catalog = new GameCatalog(names, names, names, names, names, names, names);
        var plan = GameMemory.PlanCompleteEditorTrials(data, catalog);
        Require(plan.Trials.Length > 60, "Complete editor planner did not cover the owned fixture");
        Require(plan.Trials.Select(t => t.Key).Distinct().Count() == plan.Trials.Length, "Trial keys must be unique");
        Require(plan.Missing.Any(m => m.Key == "transfer-Recruit") && plan.Missing.Any(m => m.Key == "transfer-Release"),
            "Missing free/prisoner targets must remain unverified");
        var profile = GameBuildProfiles.FindData(GameNativeFactsNovember2026.ExeSha256)!;
        var suiteEvidence = new List<GameMemory.CompleteTrialEvidence>();
        void Apply(GameSnapshot current, IReadOnlyList<RecordEdit> edits, IRecordMemory memory)
        {
            ProfiledEdits.RequireFields(profile, edits); ProfiledEdits.RequireCurrentRecords(current, edits);
            GameMemory.Apply(memory, TestGame.Classes, edits);
        }
        foreach (var trial in plan.Trials)
        {
            var memory = new FakeMemory(data); var evidence = new List<GameMemory.CompleteTrialEvidence>();
            GameMemory.RunCompleteEditTrial(data, TestGame.Classes, trial, memory, Apply, () => { }, evidence.Add);
            Require(evidence.First().Stage == "prepared" && evidence.Last().Stage == "restored-byte-exact", "Trial evidence lacks bounds: " + trial.Key);
            Require(evidence.All(e => e.Records.All(r => r.Expected == r.Actual)), "Trial readback diverged: " + trial.Key);
            Require(evidence.Last().Records.All(r => r.Before == r.Actual), "Original/neighbor records changed: " + trial.Key);
            suiteEvidence.AddRange(evidence);
        }
        string fixturePath = Path.Combine("source", "RenderChecks", "artifacts", "complete-editor-trials.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true };
        using (var log = new StreamWriter(fixturePath))
        {
            void Write(object row) => log.WriteLine(JsonSerializer.Serialize(row, options));
            var identity = new GameIdentity(0, DateTime.UnixEpoch, 0x140000000, GameNativeFactsNovember2026.ExeSha256, false, TestGame.Classes);
            Write(GameMemory.CompleteEditorManifest(new(identity, null!, data, catalog, plan), gameAccess: false));
            var images = suiteEvidence.Where(e => e.Stage == "prepared").SelectMany(e => e.Records).DistinctBy(r => r.Address).ToArray();
            foreach (var image in images) Write(new { stage = "suite-before", image.Address, image.Label, image.Kind, image.Sha256 });
            foreach (var evidence in suiteEvidence) Write(evidence);
            foreach (var image in images) Write(new { stage = "suite-after", image.Address, image.Label, matches = true,
                beforeSha256 = image.Sha256, afterSha256 = image.Sha256 });
            Write(new { stage = "incomplete", passed = plan.Trials.Length, missing = plan.Missing.Length, cancelled = false, snapshotDifferences = 0 });
        }
        var personality = plan.Trials.Single(t => t.Key == $"personality-{PersonalityFields.Condition.Offset:X}");
        Require(personality.Steps.SelectMany(s => s).Any(e => e.Address == whileMet.Address && e.Restarts?.Count == 2),
            "Condition trial must prefer an actually lit while-met trait over a lower-ID always-on trait");
        {
            var evidence = new List<GameMemory.CompleteTrialEvidence>();
            GameMemory.RunCompleteEditTrial(data, TestGame.Classes, personality, new FakeMemory(data), Apply, () => { }, evidence.Add);
            var undo = evidence.Single(e => e.Stage == "ordinary-undo-readback");
            Require(new[] { 5, 6 }.All(id => undo.Records.Any(r => r.Address == data.OfficerById[id].Address && r.Before != r.Actual)),
                "Ordinary Undo must report reset bits on holders of both forces separately from trial restoration");
        }
        var city = plan.Trials.Single(t => t.Key == $"city-{CityFields.Morale.Offset:X}");
        foreach (string failure in new[] { "prepared-log", "changed-log", "partial-write", "partial-linked-write", "unplanned-write", "guard-changed", "recovery-write", "foreign-neighbor", "stop-request" })
        {
            var trial = failure == "partial-linked-write" ? personality : city;
            var memory = new CompleteTrialFailureMemory(new FakeMemory(data), failure);
            var rows = new List<GameMemory.CompleteTrialEvidence>(); int guards = 0;
            using var cancellation = new CancellationTokenSource();
            Exception? error = null;
            try
            {
                GameMemory.RunCompleteEditTrial(data, TestGame.Classes, trial, memory,
                    (current, edits, journal) =>
                    {
                        if (failure == "unplanned-write") journal.Write(edits[0].Address + (ulong)edits[0].Changes[0].Field.Offset, [1, 2, 3, 4]);
                        Apply(current, edits, journal);
                        if (failure == "stop-request") cancellation.Cancel();
                        if (failure == "foreign-neighbor") memory.Inner.Set(data.Cities[1].Address,
                            CityFields.Morale with { Offset = CityFields.Morale.Offset + 1 }, 0xA6);
                    }, () =>
                    {
                        if (++guards > 1 && failure == "guard-changed") throw new IOException("injected changed session");
                    }, row =>
                    {
                        rows.Add(row);
                        if (failure == "prepared-log" && row.Stage == "prepared" || failure == "changed-log" && row.Stage == "changed-readback")
                            throw new IOException("injected evidence failure");
                    });
            }
            catch (Exception found) { error = found; }
            Require(failure == "stop-request" ? error is null && cancellation.IsCancellationRequested : error is not null,
                "Injected trial failure was not observed: " + failure);
            if (failure is "guard-changed" or "recovery-write" or "foreign-neighbor")
            {
                Require(rows.All(r => r.Stage != "restored-byte-exact") && rows.Any(r => r.Stage == "recovery-failed"),
                    "Incomplete recovery must never report success: " + failure);
            }
            else
            {
                foreach (var image in rows[0].Records)
                    Require(Convert.ToHexString(memory.Inner.Read(image.Address, image.Before.Length / 2)) == image.Before,
                        "Failure/stop did not restore baseline: " + failure);
            }
            if (failure == "prepared-log" || failure == "unplanned-write") Require(memory.Writes == 0, "Preparation failure must never reach memory");
        }
        // Already-unlocked records still exercise the product unlock step after a temporary bit clear.
        var unlocked = data.With([new(RecordKind.PlayRecord, data.Play!.Address, data.Play.Data, "unlocked",
            [new(PlayRecordFields.Unlocks1, -1), new(PlayRecordFields.Unlocks2, -1)])]);
        foreach (var trial in GameMemory.PlanCompleteEditorTrials(unlocked, catalog).Trials.Where(t => t.Key.StartsWith("ancient-")))
        {
            Require(trial.Steps.Count == 2, "Already-unlocked bit must have an explicit setup step");
            var rows = new List<GameMemory.CompleteTrialEvidence>();
            GameMemory.RunCompleteEditTrial(unlocked, TestGame.Classes, trial, new FakeMemory(unlocked), Apply, () => { }, rows.Add);
            Require(rows.Any(r => r.Stage == "apply-0-readback") && rows.Any(r => r.Stage == "apply-1-readback"), "Unlock setup/readback evidence missing");
            Require(rows.Last().Records.All(r => r.Before == r.Actual), "Unlock setup/dirty byte did not restore exactly");
        }
        Console.WriteLine($"PASS complete editor trials: {plan.Trials.Length} plans, {plan.Missing.Length} explicit missing cases; failure/stop/implicit recovery isolated; gameAccess=false");
        CheckCompleteEditorMenu(data, catalog, plan);
        CheckSeededProvocationTrial(data, catalog, Apply);
    }

    private static void CheckSeededProvocationTrial(GameSnapshot data, GameCatalog catalog,
        Action<GameSnapshot, IReadOnlyList<RecordEdit>, IRecordMemory> apply)
    {
        var unit = data.Units.First(u => data.ForceOfUnit(u) == data.PlayerForce);
        var field = UnitFields.Ailments[UnitFields.Provoked];
        var cleared = data.With([new(RecordKind.Unit, unit.Address, unit.Data, "seed fixture", [new(field, 0)])]);
        var fixtureIdentity = new GameIdentity(0, DateTime.UnixEpoch, 0, GameNativeFactsNovember2026.ExeSha256, false, TestGame.Classes);
        var preparation = new GameMemory.CompleteEditorPreparation(fixtureIdentity, null!, cleared, catalog, new([], []));
        var prepared = GameMemory.PrepareSeededProvocationTrial(preparation, unit.Commander);
        var trial = prepared.Plan.Trials.Single();
        Require(trial.SeededProvocation && trial.Steps.Count == 2 && trial.Steps.All(step => step.Count == 1 &&
            step[0].Address == unit.Address && step[0].Changes.Count == 1 && step[0].Changes[0].Field == field),
            "Seeded trial must explicitly declare only the single duration byte");
        var rows = new List<GameMemory.CompleteTrialEvidence>();
        var memory = new FakeMemory(cleared);
        GameMemory.RunCompleteEditTrial(cleared, TestGame.Classes, trial, memory, (current, edits, journal) =>
        {
            if (field.Read(edits[0].Expected) == 1 && edits[0].Changes[0].Value == 0)
            {
                var product = UnitEdits.Plan(current, unit.Slot, unit.Commander, [new(field, 0)]);
                Require(!product.Refused && product.Edits.Single().Changes.SequenceEqual(edits[0].Changes),
                    "Seeded decrement must use a valid product plan");
            }
            apply(current, edits, journal);
        }, () => { }, rows.Add);
        var image = rows.Last().Records.Single(r => r.Address == unit.Address);
        Require(image.Actual == image.Before && field.Read(Convert.FromHexString(image.Actual)) == 0,
            "Seeded duration/target/neighbor record must return to the original bytes");
        foreach (string failedStage in new[] { "apply-0-readback", "apply-1-readback" })
        {
            var failedMemory = new FakeMemory(cleared); bool observed = false;
            try
            {
                GameMemory.RunCompleteEditTrial(cleared, TestGame.Classes, trial, failedMemory, apply, () => { }, row =>
                { if (row.Stage == failedStage) throw new IOException("injected seeded log failure"); });
            }
            catch (IOException) { observed = true; }
            Require(observed && failedMemory.Read(unit.Address, unit.Data.Length).AsSpan().SequenceEqual(cleared.Units.Single(u => u.Address == unit.Address).Data),
                "Failure after the injected setup/decrement must restore the full original unit");
        }
        using (var file = new StreamWriter(Path.Combine("source", "RenderChecks", "artifacts", "complete-editor-seeded-trials.jsonl"), false))
        {
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true };
            void Write(object value) => file.WriteLine(JsonSerializer.Serialize(value, options));
            Write(GameMemory.CompleteEditorManifest(prepared, gameAccess: false));
            foreach (var record in rows[0].Records) Write(new { stage = "suite-before", record.Address, record.Sha256 });
            foreach (var row in rows) Write(row);
            foreach (var record in rows[0].Records) Write(new { stage = "suite-after", record.Address, matches = true,
                beforeSha256 = record.Sha256, afterSha256 = record.Sha256 });
            Write(new { stage = "all-restored", passed = 1, missing = 0, cancelled = false, snapshotDifferences = 0 });
        }
        Console.WriteLine("PASS seeded duration: explicit 0->1->0 product decrement, Undo, neighbor/target restoration, failures after both steps; gameAccess=false");
    }

    private static void CheckCompleteEditorMenu(GameSnapshot data, GameCatalog catalog, GameMemory.CompleteEditPlan plan)
    {
        var unit = data.Units.First(u => data.ForceOfUnit(u) == data.PlayerForce);
        data = data.With([new(RecordKind.Unit, unit.Address, unit.Data, "menu fixture", [new(UnitFields.Ailments[UnitFields.Provoked], 0)])]);
        var editor = OfflineEditor();
        new System.Windows.Interop.WindowInteropHelper(editor).EnsureHandle();
        void Set(string name, object? value) => typeof(EditorWindow).GetField(name, Hidden)!.SetValue(editor, value);
        void Update() => typeof(EditorWindow).GetMethod("UpdateDeveloperControls", Hidden)!.Invoke(editor, null);
        var tables = new GameTables(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var layout = new GameLayout(tables, 0, 0, 0, 0, 0, 0, 0, 0, [], [], 0, 0, 0);
        var identity = new GameIdentity(0, DateTime.UnixEpoch, 0x140000000, GameNativeFactsNovember2026.ExeSha256, false,
            TestGame.Classes with { DoctrinePanel = 0x7015 });
        DisasterTestWindow? menu = null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            Set("game", identity); Set("layout", layout); Set("snapshot", data);
            typeof(EditorWindow).GetMethod("OpenDeveloperTests", Hidden)!.Invoke(editor, null);
            menu = (DisasterTestWindow)typeof(EditorWindow).GetField("developerMenu", Hidden)!.GetValue(editor)!;
            menu.Tabs.SelectedIndex = 5; menu.UpdateLayout();
            Require(menu.CompleteEditsPreview.IsEnabled && !menu.CompleteEditsRun.IsEnabled && !menu.CompleteEditsStop.IsEnabled,
                "Preview must precede writes; no trial may be started offline accidentally");
            Set("completeEditsPreparation", new GameMemory.CompleteEditorPreparation(identity, layout, data, catalog, plan)); Update();
            Require(menu.CompleteEditsRun.IsEnabled, "Prepared same-session trial must be available");
            Require(menu.CompleteProvokeCommander is null && menu.CompleteProvokeUnits.Items.Count > 1,
                "Synthetic setup must require an explicit player-unit choice");
            menu.CompleteProvokeUnits.SelectedIndex = 1;
            Require(menu.CompleteProvokeCommander is not null && !menu.CompleteEditsRun.IsEnabled,
                "Changing the injected fixture target must invalidate the reviewed preparation");
            menu.CompleteProvokeUnits.SelectedIndex = 0;
            Set("completeEditsPreparation", new GameMemory.CompleteEditorPreparation(identity, layout, data, catalog, plan)); Update();
            var city = (CityEditorCard)typeof(EditorWindow).GetField("cityEditor", Hidden)!.GetValue(editor)!;
            var draft = city.TableDrafts.Get(new CityTarget(1), CityFields.Troops, 100); draft.Set(101); Update();
            Require(!menu.CompleteEditsPreview.IsEnabled && !menu.CompleteEditsRun.IsEnabled,
                "A hidden pending card must block the live trial");
            city.TableDrafts.Clear();
            Set("completeEditsCancellation", cancellation); Set("developerChanging", true); Set("busy", true); Update();
            Require(menu.CompleteEditsStop.IsEnabled && !menu.CompleteEditsRun.IsEnabled && !menu.CompleteEditsPreview.IsEnabled,
                "Busy trial must retain its stop action");
            ((Action)typeof(PlateButton).GetField("Click", Hidden)!.GetValue(menu.CompleteEditsStop)!).Invoke();
            Require(cancellation.IsCancellationRequested, "Stop must request cancellation without interrupting recovery");
            menu.Close(); Require(menu.IsVisible, "Closing during a trial must wait for recovery");
            Require(draft.IsChanged && draft.Value == 101, "Trial state checks must not edit user drafts");
        }
        finally
        {
            Set("completeEditsCancellation", null); Set("developerChanging", false); Set("busy", false);
            Set("game", null); Set("snapshot", null); Set("layout", null);
            menu?.Close();
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            editor.Close();
        }
        Console.WriteLine("PASS complete editor WPF preview-before-run, hidden pending, busy stop, close cancellation, retained drafts; gameAccess=false");
    }

    private sealed class CompleteTrialFailureMemory(FakeMemory memory, string failure) : IRecordMemory
    {
        internal readonly FakeMemory Inner = memory;
        internal int Writes;
        public byte[] Read(ulong address, int length) => Inner.Read(address, length);
        public void Write(ulong address, byte[] bytes)
        {
            Writes++;
            if (failure == "recovery-write" && Writes > 1) throw new IOException("injected recovery write failure");
            if ((failure == "partial-write" && Writes == 1) || (failure == "partial-linked-write" && Writes == 2))
            {
                Inner.Write(address, bytes[..Math.Max(1, bytes.Length / 2)]);
                throw new IOException("injected partial write");
            }
            Inner.Write(address, bytes);
        }
    }
}
#endif

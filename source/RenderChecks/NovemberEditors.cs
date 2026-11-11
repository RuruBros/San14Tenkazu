using System.IO;
using System.Reflection;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;

internal static partial class RenderChecks
{
    private static void CheckNovemberEditors(string output)
    {
        Directory.CreateDirectory(output);
        var passed = new List<string>();
        void Check(bool condition, string message) { Require(condition, message); passed.Add(message); }
        static object? Call(object target, string method, params object?[] args) =>
            target.GetType().GetMethod(method, Hidden)!.Invoke(target, args);
        static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, Hidden)!.GetValue(target)!;
        static IEnumerable<W.DependencyObject> Descendants(W.DependencyObject root)
        {
            yield return root;
            foreach (var child in W.LogicalTreeHelper.GetChildren(root).OfType<W.DependencyObject>())
                foreach (var nested in Descendants(child)) yield return nested;
        }
        var identity = new GameIdentity(123, DateTime.UnixEpoch, 0x140000000, GameNativeFactsNovember2026.ExeSha256,
            false, TestGame.Classes with { DoctrinePanel = 0x7015 });
        var data = EditorCoverageFixture.Snapshot();
        var storage = Path.GetFullPath(Path.Combine(output, "storage"));
        var editor = OfflineEditor(() => false, storage, storage, storage);
        try
        {
            void Set(string field, object? value) => typeof(EditorWindow).GetField(field, Hidden)!.SetValue(editor, value);
            Set("game", identity); Set("snapshot", data); Set("writesAllowed", true);
            Set("checks", GameChecks.Run(data, GameCatalog.Empty));
            var force = Field<ForceEditorCard>(editor, "forceEditor");
            force.Open((ForceEditorCard.View)Call(editor, "ForceView", data, 1)!);
            Call(editor, "UpdateControls");
            foreach (string name in new[] { "cityEditor", "gateEditor", "facilityEditor", "officerEditor", "unitEditor",
                "forceEditor", "itemEditor", "personalityEditor" })
            {
                var card = Field<EditorCard>(editor, name);
                card.SetDataProfile(identity.DataProfile);
                var apply = (PlateButton)typeof(EditorCard).GetField("apply", Hidden)!.GetValue(card)!;
                Check(apply.IsEnabled, "profile-apply-available-" + name);
                foreach (var input in Descendants(card).OfType<WC.TextBox>().Where(box => box.Tag is FieldDef))
                {
                    var field = (FieldDef)input.Tag;
                    Check(identity.DataProfile!.Supports(field) && !input.IsReadOnly, $"writable-{name}-{field.Record}-{field.Offset:X}");
                }
            }
            Check(force.CitiesTable.EditingEnabled && force.GatesTable.EditingEnabled && force.UnitsTable.EditingEnabled,
                "force-nested-tables-remain-editable");
            Check(Field<PlateButton>(force, "heal").IsEnabled && Field<PlateButton>(force, "morale").IsEnabled,
                "unit-fill-buttons-use-registered-unit-fields");
            Check(Field<PlateButton>(editor, "unlockButton").IsEnabled, "play-record-enables-unlock");
            var members = Field<OfficerTables>(force, "members");
            Check(Field<bool>(members, "fillsEnabled"), "loyalty-fill-available");
            Set("checks", GameChecks.Run(data, GameCatalog.Empty).Append(new GameChecks.Check(GameChecks.Unit, "invalid-unit", 0, 10, 1)).ToList());
            Call(editor, "UpdateControls");
            Check(!Field<PlateButton>(force, "heal").IsEnabled && Field<bool>(members, "fillsEnabled"),
                "unit-self-check-does-not-disable-independent-loyalty-fill");
            Set("checks", GameChecks.Run(data, GameCatalog.Empty));

            var formation = new FormationFixture().Snapshot();
            var baseline = formation.Baseline(identity.ExeHash);
            var target = new RuleProfileFile(Path.Combine(storage, "rule_profiles.json")).ForBuild(identity.DataProfile!);
            var kept = target.Save(baseline, "");
            Set("ruleSnapshot", formation); Set("ruleKept", kept);
            Call(editor, "UpdateControls");
            Check(Field<PlateButton>(editor, "rulesImportButton").IsEnabled, "november-rule-import-enabled");
            string legacyPath = Path.Combine(storage, "rule_profiles.json");
            File.WriteAllText(legacyPath, "preserve-other-build");
            string sourcePath = Path.Combine(output, "import-source.json");
            var key = baseline.Defaults[0].Key;
            File.WriteAllText(sourcePath, JsonSerializer.Serialize(baseline.WithChanges([new(key, baseline.Default(key), baseline.Default(key) + 1)])));
            Call(editor, "SaveImportedRules", SettingsImportSource.Read(sourcePath), identity, baseline, kept.Stamp);
            Check(target.Read().Profile!.Desired(key) == baseline.Default(key) + 1 && File.ReadAllText(legacyPath) == "preserve-other-build",
                "import-writes-only-november-target-profile");
            Set("busy", true); Call(editor, "UpdateControls");
            Check(!Field<PlateButton>(force, "heal").IsEnabled && !Field<PlateButton>(editor, "unlockButton").IsEnabled,
                "busy-blocks-all-new-actions");
            Set("busy", false); Set("snapshot", null); Call(editor, "UpdateControls");
            Check(!Field<PlateButton>(editor, "unlockButton").IsEnabled && !Field<PlateButton>(editor, "rulesImportButton").IsEnabled,
                "disconnected-actions-stay-blocked");
        }
        finally { editor.Close(); }
        var startIdentity = identity with { StartTimeUtc = DateTime.UtcNow };
        var state = RuntimeRuleSettings.Original;
        int launches = 0, writes = 0;
        var startupStorage = Path.Combine(storage, "startup");
        var preferences = new RuntimeRulePreferenceFile(Path.Combine(startupStorage, "runtime-rules"));
        var access = new GamePreparationAccess(() => false,
            () => { launches++; startIdentity = startIdentity with { StartTimeUtc = DateTime.UtcNow }; },
            () => startIdentity, _ => state,
            (_, wanted, _) =>
            {
                Require(preferences.ForBuild(identity.ExeHash).Read()?.Settings == wanted, "November intent must be saved before startup preparation");
                writes++; state = wanted;
            }, () => identity.ExeHash);
        var startupEditor = OfflineEditor(() => false, startupStorage, startupStorage, startupStorage, gamePreparationAccess: access);
        var window = new GamePreparationWindow(null) { Build = identity.ExeHash };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            window.Traits.IsChecked = true;
            var task = (Task)Call(startupEditor, "PrepareGameAsync", window)!;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                Require(watch.Elapsed < TimeSpan.FromSeconds(10), "November preparation fixture timed out");
                var frame = new System.Windows.Threading.DispatcherFrame();
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
            Check(launches == 1 && writes == 1 && window.Status.Text == L.Preparation.Ready,
                "november-startup-ui-prepares-and-verifies-once");
            Check(!File.Exists(preferences.PathName) && preferences.ForBuild(identity.ExeHash).Read()!.Build == identity.ExeHash,
                "november-startup-preference-does-not-overwrite-legacy-file");
        }
        finally
        {
            window.Close(); startupEditor.Close(); SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { gameAccess = false, passed }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS November editor WPF {passed.Count} checks; gameAccess=false");
    }
}

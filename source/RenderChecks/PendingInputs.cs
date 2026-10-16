using System.Collections;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckPendingInputs(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        int cases = 0;
        void Check(string scenario, bool passed, object? details = null)
        {
            string line = JsonSerializer.Serialize(new { scenario, passed, details, gameAccess = false });
            log.WriteLine(line); Console.WriteLine(line); Require(passed, scenario); cases++;
        }
        static FieldDef RuleInput(object card) => ((IEnumerable)card.GetType().GetField("fields", Hidden)!.GetValue(card)!)
            .Cast<object>().Select(p => (NumberField)p.GetType().GetProperty("Value")!.GetValue(p)!).First().Field;
        var inputs = new (string Screen, FieldDef Field)[]
        {
            ("officer", OfficerFields.Leadership), ("city", CityFields.Gold), ("county", CountyFields.All[0]),
            ("gate", GateFields.Troops), ("unit", UnitFields.Morale), ("facility", FacilityFields.Durability),
            ("personality", PersonalityFields.Values[0]), ("item", ItemFields.Bonus), ("military", CorpsFields.MilitaryPoints),
            ("formation", RuleInput(new FormationEditorCard())), ("tactic", RuleInput(new TacticEditorCard())),
            ("policy-effect", RuleInput(new PolicyEditorCard("policyeffect"))),
            ("office", RuleInput(new OfficeEditorCard())), ("rank", RuleInput(new PlainRuleEditorCard(RankRules.Spec))),
            ("doctrine", RuleInput(new PlainRuleEditorCard(DoctrineRules.Spec)))
        };
        var host = new W.Window { Width = 400, Height = 200, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        var focusAway = new WC.TextBox();
        try
        {
            host.Show();
            foreach (var input in inputs)
            {
                var number = new NumberField(input.Field);
                long initial = Math.Clamp(10, input.Field.Min, input.Field.Max - 2);
                var panel = new WC.StackPanel(); panel.Children.Add(number.Box); panel.Children.Add(focusAway);
                host.Content = panel; host.UpdateLayout(); number.Show(initial);
                Check(input.Screen + "-show-is-not-an-edit", !number.IsChanged);
                WI.Keyboard.Focus(number.Box);
                Check(input.Screen + "-real-keyboard-focus", number.Box.IsKeyboardFocused);
                number.Follow(initial + 1);
                Check(input.Screen + "-focused-untouched-refresh-is-not-an-edit", !number.IsChanged,
                    new { number.Current, number.Box.Text });
                Check(input.Screen + "-untouched-read-uses-current-game-value", number.TryRead(out long value, out _)
                    && value == initial + 1 && number.Value == initial + 1);
                int shortcutEvents = 0;
                number.Edited += () => shortcutEvents++;
                number.Set(initial);
                Check(input.Screen + "-shortcut-can-select-the-retained-display", number.IsChanged
                    && number.Value == initial && shortcutEvents == 1);
                number.Show(initial); number.Follow(initial + 1);
                WI.Keyboard.Focus(focusAway);
                Check(input.Screen + "-leaving-untouched-input-shows-latest-value", !number.IsChanged && number.Value == initial + 1);
                number.Set(initial + 2); number.Follow(initial);
                Check(input.Screen + "-explicit-edit-survives-refresh", number.IsChanged && number.Value == initial + 2);
                number.Follow(initial + 2);
                Check(input.Screen + "-game-catching-up-clears-edit", !number.IsChanged);
                number.Box.Text = "invalid"; number.Follow(initial);
                Check(input.Screen + "-invalid-user-input-is-preserved", number.IsChanged && !number.IsValid && number.Box.Text == "invalid");
                number.Set(initial);
                Check(input.Screen + "-restoring-current-value-clears-edit", !number.IsChanged);
                panel.Children.Remove(focusAway);
            }
            Console.WriteLine($"Native WPF pending inputs: {cases} cases passed; gameAccess=false");
        }
        finally { host.Close(); }
    }

    private static void ReadPendingCardsGame(string output)
    {
        Directory.CreateDirectory(output);
        GameIdentity game = GameMemory.FindGame();
        var layout = GameMemory.ReadCurrentLayout(game);
        var snapshot = GameMemory.ReadSnapshot(game, layout);
        var catalog = GameMemory.ReadCatalog(game, layout);
        var countries = GameMemory.ReadCountryNames(game, layout);
        var window = OfflineEditor();
        var map = (MapView)typeof(EditorWindow).GetField("map", Hidden)!.GetValue(window)!;
        typeof(EditorWindow).GetField("snapshot", Hidden)!.SetValue(window, snapshot);
        typeof(EditorWindow).GetField("catalog", Hidden)!.SetValue(window, catalog);
        map.OfficerEditor.SetCatalog(catalog); map.ForceEditor.SetNames(countries, catalog);
        var show = typeof(EditorWindow).GetMethod("Show", Hidden)!;
        var refresh = typeof(EditorWindow).GetMethod("RefreshEditor", Hidden)!;
        var open = typeof(EditorWindow).GetProperty("OpenEditor", Hidden)!;
        static IEnumerable<WC.ComboBox> Choices(W.DependencyObject root)
        {
            if (root is WC.ComboBox box) { yield return box; yield break; }
            foreach (var child in W.LogicalTreeHelper.GetChildren(root).OfType<W.DependencyObject>())
                foreach (var choice in Choices(child)) yield return choice;
        }
        IEnumerable<EditorTarget> targets = snapshot.ForceRecords.Values.Where(f => f.Ruler is > 0 and < 65535).Select(f => (EditorTarget)new ForceTarget(f.Id))
            .Concat(snapshot.Cities.Where(c => c.CityId > 0).Select(c => new CityTarget(c.CityId)))
            .Concat(snapshot.Gates.Select(g => new GateTarget(g.Id)))
            .Concat(snapshot.Facilities.Where(f => f.Building is not null).Select(FacilityTarget.Of))
            .Concat(snapshot.Officers.Where(o => o.Id > 0).Select(o => new OfficerTarget(o.Id)))
            .Concat(snapshot.Units.Select(u => new UnitTarget(u.Slot, u.Commander)))
            .Concat(snapshot.Personalities.Where(p => p.Index > 0).Select(p => new PersonalityTarget(p.Index)))
            .Concat(snapshot.Items.Where(i => i.Index > 0).Select(i => new ItemTarget(i.Index)));
        var records = new List<object>(); int failures = 0, wheelChecks = 0;
        try
        {
            foreach (var target in targets)
            {
                try
                {
                    bool shown = (bool)show.Invoke(window, [target])!;
                    var card = (EditorCard)open.GetValue(window)!;
                    int before = card.Pending;
                    int wheelFailures = 0;
                    foreach (var box in Choices(card))
                    {
                        int selected = box.SelectedIndex;
                        foreach (int delta in new[] { -120, 120 })
                        {
                            var wheel = new WI.MouseWheelEventArgs(WI.Mouse.PrimaryDevice, 0, delta) { RoutedEvent = W.UIElement.PreviewMouseWheelEvent };
                            box.RaiseEvent(wheel); wheelChecks++;
                            if (!wheel.Handled || box.SelectedIndex != selected) wheelFailures++;
                        }
                    }
                    refresh.Invoke(window, null);
                    int after = card.Pending;
                    bool passed = shown && before == 0 && after == 0 && wheelFailures == 0;
                    if (!passed) failures++;
                    records.Add(new { target = target.ToString(), shown, before, after, wheelFailures, passed });
                }
                catch (Exception error) { failures++; records.Add(new { target = target.ToString(), passed = false, error = error.InnerException?.Message ?? error.Message }); }
            }
        }
        finally { map.HideEditors(); window.Close(); }
        var result = new { utc = DateTime.UtcNow, game.ProcessId, game.Verified, cases = records.Count, wheelChecks, failures, writes = 0, records };
        File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { result.ProcessId, result.cases, wheelChecks, failures, writes = 0,
            counts = targets.GroupBy(t => t.GetType().Name).Select(g => new { screen = g.Key, count = g.Count() }) }));
        Require(failures == 0, $"{failures} cards created unrequested edits");
    }
}

using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void CheckUnifiedDrafts(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        int cases = 0;
        void Check(string scenario, bool passed)
        {
            string line = JsonSerializer.Serialize(new { scenario, passed, gameAccess = false });
            log.WriteLine(line); Console.WriteLine(line); Require(passed, scenario); cases++;
        }
        static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
        static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, Hidden | System.Reflection.BindingFlags.Static)!.Invoke(o, args);
        static void Layout(W.FrameworkElement root, double width = 1000, double height = 680)
        { root.Measure(new W.Size(width, height)); root.Arrange(new W.Rect(0, 0, width, height)); root.UpdateLayout(); }
        void Capture(W.FrameworkElement root, string name)
        {
            var image = new WMI.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, WM.PixelFormats.Pbgra32);
            image.Render(root); var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        static NumberField Begin(GameTable table, object tag, int column)
        {
            var list = Field<WC.ListBox>(table, "list"); list.ApplyTemplate();
            var row = table.Rows.Single(r => Equals(r.Tag, tag)); list.ScrollIntoView(row); list.UpdateLayout();
            object item = list.ItemContainerGenerator.ContainerFromItem(row)!;
            Require((bool)Call(table, "BeginInput", item, column)!, "Cannot edit fixture row");
            object input = Field<object>(table, "input");
            return (NumberField)input.GetType().GetProperty("Number")!.GetValue(input)!;
        }
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        var fixture = new TestGame().Force(1, 1, 1).City(1, 1).Gate(52, 1).Gate(53, 1)
            .Officer(1, "군주", 1, 1, 1).Officer(11, "장수", 1, 1, 4).With(11, OfficerFields.Leadership, 65).Leading(11);
        GameSnapshot data = fixture.Build();
        var window = OfflineEditor();
        typeof(EditorWindow).GetField("snapshot", Hidden)!.SetValue(window, data);
        Call(window, "FillLists");
        var map = Field<MapView>(window, "map"); var lists = map.Lists;
        var host = new W.Window { Width = 1020, Height = 720, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            var table = Field<GameTable>(window, "officerTable");
            Check("all-editable-list-tables-share-footer-drafts", new[] { "officerTable", "cityTable", "unitTable", "personalityTable" }
                .All(n => Field<GameTable>(window, n).Drafts == lists.TableDrafts));
            lists.ApplyEnabled = true; table.EditingEnabled = true;
            map.ShowList(ListKind.Officers);
            // Host the production list and its footer with genuine WPF input focus.
            ((WC.Panel)WM.VisualTreeHelper.GetParent(lists)).Children.Remove(lists);
            host.Content = lists; lists.Visibility = W.Visibility.Visible; lists.ShowTab((int)ListKind.Officers); host.Show(); Layout(lists);
            int column = Array.FindIndex(Field<GameTable.Column[]>(table, "columns"), c => c.Field == OfficerFields.Leadership);
            NumberField number = Begin(table, 11, column); number.Box.Text = "95";
            Check("typing-without-enter-enables-list-footer", lists.Pending == 1 && Field<PlateButton>(lists, "apply").IsEnabled && number.Box.IsKeyboardFocused);
            Check("pending-list-input-protects-close-and-reload", (bool)typeof(EditorWindow).GetProperty("HasPendingEdits", Hidden)!.GetValue(window)!);
            Call(table, "CancelInput"); Call(table, "SortBy", column); Layout(lists);
            Check("list-sort-preserves-typed-value", Begin(table, 11, column).Box.Text == "95");
            Field<SearchBox>(window, "officerSearch").Text = "no matching fixture row"; Call(window, "FillLists");
            Check("filtered-out-row-remains-pending", table.Rows.Count == 0 && lists.Pending == 1);
            lists.ShowTab((int)ListKind.Cities);
            Check("tab-change-preserves-other-list-drafts", lists.Pending == 1 && Field<PlateButton>(lists, "apply").Badge == 1);
            lists.TableDrafts.Get(new CityTarget(1), CityFields.Gold, 0).Set(12345);
            lists.TableDrafts.Get(new UnitTarget(0, 11), UnitFields.Morale, 0).Set(100);
            var changes = lists.TableDrafts.Changes(out string error)!;
            EditPlan plan = EditorWindow.PlanCardEdits(data, new([], "Lists"), changes);
            var memory = new FakeMemory(data); GameMemory.Apply(memory, TestGame.Classes, plan.Edits);
            Check("all-list-tabs-save-as-one-batch-with-experience", !plan.Refused && plan.Edits.Count == 3 &&
                CityFields.Gold.Read(memory[data.Cities[1].Address]!) == 12345 &&
                OfficerFields.Leadership.Read(memory[data.OfficerById[11].Address]!) == 95 &&
                OfficerFields.FitsExperience(memory[data.OfficerById[11].Address]!, OfficerFields.Leadership));
            GameMemory.Apply(memory, TestGame.Classes, Undo.Of(plan.Edits));
            Check("one-undo-restores-whole-list-batch", plan.Edits.All(e => memory[e.Address]!.SequenceEqual(e.Expected)));
            number.Box.Text = "invalid";
            Check("invalid-filtered-row-blocks-whole-list-batch", lists.TableDrafts.Changes(out error) is null && error.Length > 0 && lists.Pending == 3);
            number.Box.Text = "95";
            Field<SearchBox>(window, "officerSearch").Text = ""; Call(window, "FillLists"); lists.ShowTab((int)ListKind.Officers); Layout(lists);
            Check("returning-to-filtered-row-restores-same-input", Begin(table, 11, column).Box.Text == "95");
            Capture(lists, "list-footer");
            lists.TableDrafts.Clear(); Check("cleared-list-footer-disabled", lists.Pending == 0 && !Field<PlateButton>(lists, "apply").IsEnabled);
            host.Content = null;

            // Production city county rows, including bulk override precedence and invalid input blocking.
            var card = map.CityEditor;
            card.Open((CityEditorCard.View)Call(window, "CityView", data, 1)!);
            var bulk = Field<List<(FieldDef Field, WC.TextBox Box)>>(card, "bulk");
            Check("county-fixture-has-multiple-targets", Field<List<(int Index, NumberField[] Fields, WC.TextBlock Level)>>(card, "counties").Count == 2);
            bulk[0].Box.Text = "1234";
            var city = card.Changes(out error)!;
            Check("bulk-is-pending-and-collected-without-enter", card.Pending == 1 && city.Counties.Count == 2 &&
                city.Counties.All(c => c.Changes.Single().Field == bulk[0].Field && c.Changes.Single().Value == 1234));
            var countyRows = Field<List<(int Index, NumberField[] Fields, WC.TextBlock Level)>>(card, "counties");
            countyRows[0].Fields[0].Set(2000); countyRows[0].Fields[1].Set(50);
            city = card.Changes(out error)!;
            Check("bulk-overrides-same-column-and-preserves-other-county-inputs", city.Counties[0].Changes.Any(c => c.Field == bulk[0].Field && c.Value == 1234) &&
                city.Counties[0].Changes.Any(c => c.Field == countyRows[0].Fields[1].Field && c.Value == 50));
            bulk[1].Box.Text = "invalid";
            Check("invalid-bulk-blocks-entire-city-save", card.Changes(out error) is null && error.Length > 0);
            bulk[1].Box.Clear(); bulk[0].Box.Clear(); city = card.Changes(out error)!;
            Check("blank-bulk-restores-individual-inputs", city.Counties.Single().Changes.Any(c => c.Field == bulk[0].Field && c.Value == 2000));
            Call(card, "Revert"); Check("revert-clears-bulk-and-individual-inputs", card.Pending == 0 && bulk.All(b => b.Box.Text == ""));
            bulk[0].Box.Text = "1234"; card.Open((CityEditorCard.View)Call(window, "CityView", data, 1)!);
            Check("successful-reopen-clears-consumed-bulk", card.Pending == 0 && bulk[0].Box.Text == "");
            ((WC.Panel)WM.VisualTreeHelper.GetParent(card)).Children.Remove(card);
            host.Width = 620; host.Content = card; card.Visibility = W.Visibility.Visible; Call(card, "ShowPage", 1); Layout(card, 600, 680); Capture(card, "county-bulk");
            host.Content = null;

            // Actual AI controls use supplied fixture raw values; the writer is an in-memory spy, never GameMemory.
            string folder = FaceLibrary.FindGameDirectory(null) ?? throw new DirectoryNotFoundException("AI fixture file not installed");
            var trees = AiTreeFile.Read(folder); int writes = 0; bool succeed = false;
            IReadOnlyList<(AiGuideValue Value, int Raw)> last = [];
            var ai = new AiView(trees, () => null, (_, requests) => { writes++; last = requests; return Task.FromResult(succeed); });
            var picker = Field<WC.ComboBox>(ai, "pageList");
            var aiBoxes = Field<Dictionary<AiGuideValue, (WC.TextBox Box, int? Now, int? File)>>(ai, "boxes");
            var first = AiGuide.Pages[0].Sections.SelectMany(s => s.Values).First();
            var second = AiGuide.Pages[1].Sections.SelectMany(s => s.Values).First();
            WC.TextBox Attach(AiGuideValue value)
            {
                var grid = new WC.Grid(); int file = AiEdits.FileRaw(trees, value)!.Value;
                Call(ai, "Box", grid, 0, value, file, (int?)file, false);
                return aiBoxes[value].Box;
            }
            WC.TextBox firstBox = Attach(first); int firstRaw = AiEdits.FileRaw(trees, first)!.Value;
            firstBox.Text = AiText.Number(first.Shown(firstRaw + 1)); picker.SelectedIndex = 1;
            Check("ai-page-change-keeps-first-draft", ai.Pending && aiBoxes[first].Box.Text == AiText.Number(first.Shown(firstRaw + 1)));
            WC.TextBox secondBox = Attach(second); int secondRaw = AiEdits.FileRaw(trees, second)!.Value;
            secondBox.Text = AiText.Number(second.Shown(secondRaw + 1)); picker.SelectedIndex = 0;
            Check("ai-rebuilding-controls-retains-exact-text", Attach(first) == firstBox && firstBox.Text == AiText.Number(first.Shown(firstRaw + 1)));
            secondBox.Text = "invalid"; ((Task)Call(ai, "ApplyAsync")!).GetAwaiter().GetResult();
            Check("hidden-ai-invalid-draft-blocks-all-writes", writes == 0 && ai.Pending && secondBox.Text == "invalid");
            secondBox.Text = AiText.Number(second.Shown(secondRaw + 1));
            ((Task)Call(ai, "ApplyAsync")!).GetAwaiter().GetResult();
            Check("ai-apply-collects-all-pages-in-one-writer-call", writes == 1 && last.Count == 2 && last.Any(r => r.Value == first) && last.Any(r => r.Value == second));
            Check("ai-failed-write-keeps-all-drafts", ai.Pending && firstBox.Text == AiText.Number(first.Shown(firstRaw + 1)));
            picker.SelectedIndex = 0; ((Task)Call(ai, "DefaultsAsync")!).GetAwaiter().GetResult();
            Check("ai-defaults-only-fill-current-page-without-writing", writes == 1 && firstBox.Text == AiText.Number(first.Shown(firstRaw)) &&
                secondBox.Text == AiText.Number(second.Shown(secondRaw + 1)) && ai.Pending);
            firstBox.Text = AiText.Number(first.Shown(firstRaw + 1)); succeed = true;
            ((Task)Call(ai, "ApplyAsync")!).GetAwaiter().GetResult();
            Check("ai-success-clears-only-written-drafts", writes == 2 && !ai.Pending);
            ai.DiscardDrafts(); Check("explicit-session-reset-clears-ai-drafts", !ai.Pending && aiBoxes.Count == 0);
            Console.WriteLine($"PASS unified drafts: {cases} checks; gameAccess=false");
        }
        finally { lists.TableDrafts.Clear(); map.HideEditors(); host.Close(); window.Close(); app.Shutdown(); }
    }
}

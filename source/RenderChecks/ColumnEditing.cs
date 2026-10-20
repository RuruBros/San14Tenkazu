using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    // Real table headers and batch plans, using fixture memory only.
    private static void CheckColumnEditing(string output)
    {
        Directory.CreateDirectory(output);
        using var journal = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        void Check(string scenario, bool passed, object? details = null)
        {
            string line = JsonSerializer.Serialize(new { scenario, passed, details, gameAccess = false });
            journal.WriteLine(line); Console.WriteLine(line); Require(passed, scenario);
        }
        static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Hidden)!.GetValue(obj)!;
        static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Hidden)!.Invoke(obj, args);
        static void Layout(W.FrameworkElement root, double width, double height)
        {
            root.Measure(new W.Size(width, height)); root.Arrange(new W.Rect(0, 0, width, height)); root.UpdateLayout();
        }
        void Capture(W.FrameworkElement root, string name)
        {
            foreach (int dpi in new[] { 96, 144 })
            {
                var bitmap = new WMI.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi / 96),
                    (int)Math.Ceiling(root.ActualHeight * dpi / 96), dpi, dpi, WM.PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, name + "-" + dpi + ".png")); encoder.Save(file);
            }
        }
        static PlateButton[] Buttons(GameTable table) => Field<WC.Canvas>(table, "header").Children.OfType<PlateButton>()
            .Where(button => button.Visibility == W.Visibility.Visible).ToArray();
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        var game = new TestGame().Force(1, 1, 1).Force(2, 2, 2).City(1, 1).City(2, 2).Gate(52, 1).Gate(53, 2)
            .Officer(1, "조조", 1, 1, 1).Officer(2, "유비", 2, 2, 1)
            .Personality(1, "수치개성", "", (5, 20)).Personality(2, "고정개성", "", (6, 1));
        for (int i = 11; i <= 50; i++) game.Officer(i, "장수" + i, i % 2 + 1, i % 2 + 1, 4).With(i, OfficerFields.Leadership, 65);
        game.Leading(11).Leading(12);
        GameSnapshot data = game.Build();
        foreach (UnitRecord unit in data.Units) UnitFields.Morale.Encode(50).CopyTo(unit.Data, UnitFields.Morale.Offset);
        var window = OfflineEditor();
        typeof(EditorWindow).GetField("snapshot", Hidden)!.SetValue(window, data);
        Call(window, "FillLists");
        var map = Field<MapView>(window, "map");
        try
        {
            foreach ((string name, int count) in new[] { ("city", 3), ("officer", 6), ("unit", UnitFields.All.Length), ("personality", 2) })
            {
                // Exercise a displayed table; unopened tabs intentionally have no materialized rows.
                map.ShowList(name switch { "city" => ListKind.Cities, "officer" => ListKind.Officers,
                    "unit" => ListKind.Units, _ => ListKind.Personalities });
                GameTable table = Field<GameTable>(window, name + "Table");
                table.EditingEnabled = true;
                Check(name + "-writer-wired", table.ColumnWriter is not null && Buttons(table).Length == count);
                foreach (double width in new[] { 500.0, 600.0, 1000.0 })
                {
                    Layout(table, width, 180);
                    var columns = Field<GameTable.Column[]>(table, "columns");
                    var header = Field<WC.Canvas>(table, "header");
                    var edges = (double[])Call(header, "Edges")!;
                    var actions = Field<Dictionary<int, PlateButton>>(header, "actions");
                    Check($"{name}-header-fits-{width}", actions.All(pair => pair.Value.ActualWidth == 22 &&
                        pair.Value.TranslatePoint(new W.Point(), header) is var point && point.X >= edges[pair.Key] - .01 &&
                        point.X + pair.Value.ActualWidth <= edges[pair.Key + 1] + .01 && point.Y >= 0 && point.Y + pair.Value.ActualHeight <= header.ActualHeight),
                        new { width, headerHeight = header.ActualHeight, buttons = Buttons(table).Length,
                            actions = actions.Select(pair => new { column = pair.Key, pair.Value.ActualWidth, pair.Value.ActualHeight,
                                position = pair.Value.TranslatePoint(new W.Point(), header), left = edges[pair.Key], right = edges[pair.Key + 1] }).ToArray() });
                    if (width is 500 or 1000) Capture(table, name + "-" + width);
                }
                table.EditingEnabled = false;
                Check(name + "-busy-disabled", Buttons(table).All(button => !button.IsEnabled));
                table.EditingEnabled = true;
            }
            Check("read-only-force-counts-have-no-buttons", Buttons(Field<GameTable>(window, "forceTable")).Length == 0);
            var force = Field<ForceEditorCard>(window, "forceEditor");
            Check("embedded-tables-wired", force.CitiesTable.ColumnWriter is not null && force.GatesTable.ColumnWriter is not null
                && force.UnitsTable.ColumnWriter is not null && force.Members.ColumnWriter is not null
                && Field<CityEditorCard>(window, "cityEditor").Officers.ColumnWriter is not null
                && Field<GateEditorCard>(window, "gateEditor").Officers.ColumnWriter is not null);
            var members = new OfficerTables("") { CellWriter = (_, _, _) => Task.FromResult(true),
                ColumnWriter = (_, _, _) => Task.FromResult(true), CanEditCell = (_, _) => true, FillsEnabled = true };
            members.Show(data.Officers.Take(3).Select(OfficerTables.RowOf).ToList());
            Check("status-group-headers-wired", Field<List<GameTable>>(members, "tables").All(table => Buttons(table).Length == 6 && table.ColumnWriter is not null));
            var personality = Field<GameTable>(window, "personalityTable");
            var allowed = (IReadOnlyList<object>)Call(personality, "ColumnTargets", 2)!;
            Check("non-numeric-personality-effects-excluded", allowed.SequenceEqual(new object[] { 1 }));

            map.ShowList(ListKind.Officers);
            var officers = Field<GameTable>(window, "officerTable");
            Field<SearchBox>(window, "officerSearch").Text = "장수";
            Field<WC.ComboBox>(window, "officerForceBox").SelectedIndex = Field<List<int>>(window, "forceChoices").IndexOf(1) + 1;
            Call(window, "FillLists");
            officers.EditingEnabled = true;
            var targets = (IReadOnlyList<object>)Call(officers, "ColumnTargets", 5)!;
            Check("search-and-force-filter-include-offscreen-rows", targets.Count == 20 && targets.All(tag => (int)tag % 2 == 0)
                && officers.Rows.Count == targets.Count && targets.Count * officers.RowHeight > officers.ActualHeight);
            EditPlan officerPlan = EditorWindow.PlanColumnEdits(data, targets.Select(tag => (EditorTarget)new OfficerTarget((int)tag)).ToList(), OfficerFields.Leadership, 95);
            var memory = new FakeMemory(data);
            var outsideBefore = (byte[])memory[data.OfficerById[11].Address]!.Clone();
            GameMemory.Apply(memory, TestGame.Classes, officerPlan.Edits);
            Check("batch-writes-only-filtered-officers-with-experience", officerPlan.Edits.Count == 20 && targets.All(tag =>
                OfficerFields.Leadership.Read(memory[data.OfficerById[(int)tag].Address]!) == 95
                && OfficerFields.FitsExperience(memory[data.OfficerById[(int)tag].Address]!, OfficerFields.Leadership))
                && memory[data.OfficerById[11].Address]!.SequenceEqual(outsideBefore));
            GameMemory.Apply(memory, TestGame.Classes, Undo.Of(officerPlan.Edits));
            Check("one-batch-undo-restores-entire-records", targets.All(tag => memory[data.OfficerById[(int)tag].Address]!.SequenceEqual(data.OfficerById[(int)tag].Data)));

            foreach ((string name, EditorTarget[] selected, FieldDef field, long value) in new[]
            {
                ("city", new EditorTarget[] { new CityTarget(1), new CityTarget(2) }, CityFields.Gold, 12345L),
                ("gate", new EditorTarget[] { new GateTarget(52), new GateTarget(53) }, GateFields.Morale, 100L),
                ("unit", data.Units.Select(unit => (EditorTarget)new UnitTarget(unit.Slot, unit.Commander)).ToArray(), UnitFields.Morale, 130L)
            })
            {
                EditPlan plan = EditorWindow.PlanColumnEdits(data, selected, field, value);
                var batchMemory = new FakeMemory(data);
                GameMemory.Apply(batchMemory, TestGame.Classes, plan.Edits);
                Check(name + "-batch-uses-regular-plans", !plan.Refused && plan.Edits.Count == selected.Length
                    && plan.Edits.All(edit => field.Read(batchMemory[edit.Address]!) == value));
                GameMemory.Apply(batchMemory, TestGame.Classes, Undo.Of(plan.Edits));
                Check(name + "-batch-undo", plan.Edits.All(edit => batchMemory[edit.Address]!.SequenceEqual(edit.Expected)));
            }
            EditPlan stale = EditorWindow.PlanColumnEdits(data, [new UnitTarget(0, 11), new UnitTarget(1, 999)], UnitFields.Morale, 130);
            Check("stale-unit-aborts-whole-plan", stale.Refused && stale.Edits.Count == 0);
            EditPlan invalid = EditorWindow.PlanColumnEdits(data, [new CityTarget(1), new CityTarget(2)], CityFields.Gold, CityFields.Gold.Max + 1);
            Check("out-of-range-aborts-whole-plan", invalid.Refused && invalid.Edits.Count == 0);
            EditPlan unchanged = EditorWindow.PlanColumnEdits(data, [new OfficerTarget(11), new OfficerTarget(11)], OfficerFields.Leadership, 65);
            Check("unchanged-and-duplicate-targets-write-nothing", !unchanged.Refused && unchanged.Edits.Count == 0);

            var dialog = new ColumnEditDialog(OfficerFields.Leadership, 20, 2);
            Check("blank-input-cannot-apply", dialog.Number.Box.Text == "" && !dialog.ApplyButton.IsEnabled);
            dialog.Number.Box.Text = "999999";
            Check("invalid-input-cannot-apply", !dialog.ApplyButton.IsEnabled);
            dialog.Number.Box.Text = "95";
            Check("valid-input-can-apply", dialog.ApplyButton.IsEnabled);
            var content = (W.FrameworkElement)dialog.Content;
            content.Measure(new W.Size(double.PositiveInfinity, double.PositiveInfinity));
            content.Arrange(new W.Rect(new W.Point(), content.DesiredSize)); content.UpdateLayout(); Capture(content, "dialog");
            dialog.Close();

            // Enter/Space on a real header button opens the modal and dispatches exactly one whole-table batch.
            var clickTable = new GameTable(Field<GameTable.Column[]>(officers, "columns"), officers.RowHeight)
                { CellWriter = (_, _, _) => Task.FromResult(true), EditingEnabled = true };
            clickTable.SetRows(officers.Rows);
            int calls = 0;
            clickTable.ColumnWriter = (tags, field, value) =>
            {
                calls++;
                Check("header-button-dispatches-exact-filtered-batch", tags.SequenceEqual(targets) && field == OfficerFields.Leadership && value == 95);
                return Task.FromResult(true);
            };
            object sortBefore = Field<int>(clickTable, "sortColumn");
            app.Dispatcher.BeginInvoke(WT.DispatcherPriority.Loaded, new Action(() =>
            {
                var modal = app.Windows.OfType<ColumnEditDialog>().Single();
                modal.Opacity = 0; modal.Number.Box.Text = "95";
                ((Action)typeof(PlateButton).GetField("Click", Hidden)!.GetValue(modal.ApplyButton)!)();
            }));
            var button = Buttons(clickTable).Single(action => action.Text == L.Lists.BulkColumn(OfficerFields.Leadership.Label));
            ((Action)typeof(PlateButton).GetField("Click", Hidden)!.GetValue(button)!)();
            Check("header-action-does-not-sort-or-duplicate", calls == 1 && Equals(sortBefore, Field<int>(clickTable, "sortColumn")));
            app.Dispatcher.BeginInvoke(WT.DispatcherPriority.Loaded, new Action(() =>
            {
                var modal = app.Windows.OfType<ColumnEditDialog>().Single(); modal.Opacity = 0; modal.DialogResult = false;
            }));
            ((Action)typeof(PlateButton).GetField("Click", Hidden)!.GetValue(button)!)();
            Check("cancel-does-not-dispatch", calls == 1);
            clickTable.SetRows([]);
            Check("empty-table-disables-actions", Buttons(clickTable).All(action => !action.IsEnabled));
            Console.WriteLine("PASS column editing checks; fixture memory only");
        }
        finally { window.Close(); app.Shutdown(); }
    }
}

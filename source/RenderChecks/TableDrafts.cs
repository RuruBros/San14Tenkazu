using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    // Real WPF focus, table recycling and card plans; fixture memory only, never a game connection.
    private static void CheckTableDrafts(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        int cases = 0;
        void Check(string scenario, bool passed)
        {
            string line = JsonSerializer.Serialize(new { scenario, passed, gameAccess = false });
            log.WriteLine(line); Console.WriteLine(line); Require(passed, scenario); cases++;
        }
        static T Field<T>(object obj, string name)
        {
            for (Type? type = obj.GetType(); type is not null; type = type.BaseType)
                if (type.GetField(name, Hidden) is { } field) return (T)field.GetValue(obj)!;
            throw new InvalidOperationException("Missing fixture field: " + name);
        }
        static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Hidden)!.Invoke(obj, args);
        static void Layout(W.FrameworkElement root)
        {
            root.Measure(new W.Size(720, 600)); root.Arrange(new W.Rect(0, 0, 720, 600)); root.UpdateLayout();
        }
        NumberField Begin(GameTable table, object tag, int column)
        {
            var list = Field<WC.ListBox>(table, "list");
            void State(string stage, Exception? exception=null)
            {
                var input=Field<object?>(table,"input");
                var scroll=Field<WC.ScrollViewer?>(table,"scroll");
                var number=input?.GetType().GetProperty("Number")?.GetValue(input) as NumberField;
                log.WriteLine(JsonSerializer.Serialize(new{diagnostic="table-cell-begin",stage,tag,column,table.IsVisible,
                    inputPresent=input is not null,numberPresent=number is not null,focused=number?.Box.IsKeyboardFocused,
                    keyboardFocus=WI.Keyboard.FocusedElement?.GetType().FullName,list.SelectedIndex,
                    verticalOffset=scroll?.VerticalOffset,horizontalOffset=scroll?.HorizontalOffset,exception=exception?.ToString(),gameAccess=false}));
            }
            State("before-layout");
            list.ScrollIntoView(table.Rows.Single(r => Equals(r.Tag, tag))); list.UpdateLayout();
            object item = list.ItemContainerGenerator.ContainerFromItem(table.Rows.Single(r => Equals(r.Tag, tag)))!;
            State("before-begin");
            try { Require((bool)Call(table, "BeginInput", item, column)!, "Cannot begin fixture cell input"); }
            catch(Exception exception){State("begin-exception",exception);throw;}
            State("after-begin");
            object input = Field<object>(table, "input");
            if(input is null)throw new InvalidOperationException("BeginInput returned true but the input was cancelled; see table-cell-begin diagnostics");
            return (NumberField)input.GetType().GetProperty("Number")!.GetValue(input)!;
        }
        static void Click(PlateButton button) => ((Action)typeof(PlateButton).GetField("Click", Hidden)!.GetValue(button)!)();
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        var builder = new TestGame().Force(1, 1, 1).City(1, 1, 10).Gate(52, 1)
            .Officer(1, "군주", 1, 1, 1).Officer(10, "태수", 1, 1, 3)
            .Officer(11, "장수", 1, 1, 4).With(11, OfficerFields.Leadership, 65)
            .With(11, OfficerFields.Strength, 60).Leading(11);
        GameSnapshot data = builder.Build();
        var window = OfflineEditor(() => false, Path.Combine(output,"storage"), Path.Combine(output,"storage"), Path.Combine(output,"storage"));
        typeof(EditorWindow).GetField("snapshot", Hidden)!.SetValue(window, data);
        var card = Field<CityEditorCard>(window, "cityEditor");
        var host = new W.Window { Width = 740, Height = 640, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            Check("city-opens", Call(window, "Show", new CityTarget(1)) is true);
            var map = Field<MapView>(window, "map");
            var resume = Field<PlateButton>(map.Lists, "resume");
            map.ReturnToList();
            Check("unchanged-detail-has-no-resume-button", resume.Visibility == W.Visibility.Collapsed);
            card.TableDrafts.Get(new OfficerTarget(11), OfficerFields.Leadership, 65).Set(95);
            Check("hidden-table-edit-shows-resume-button", resume.Visibility == W.Visibility.Visible && resume.Badge == 1);
            card.TableDrafts.Get(new OfficerTarget(11), OfficerFields.Leadership, 65).Set(65);
            Check("restoring-table-value-hides-resume-button", resume.Visibility == W.Visibility.Collapsed);
            var cityFields = Field<Dictionary<FieldDef, NumberField>>(card, "fields");
            cityFields[CityFields.Gold].Set(123);
            Check("regular-edit-shows-resume-button", resume.Visibility == W.Visibility.Visible && resume.Badge == 1);
            Call(card, "Revert");
            Check("revert-hides-resume-button", resume.Visibility == W.Visibility.Collapsed);
            card.TableDrafts.Get(new OfficerTarget(11), OfficerFields.Leadership, 65).Set(95);
            map.ShowSection(PaneSection.Data);
            Check("resume-hidden-in-unrelated-section", resume.Visibility == W.Visibility.Collapsed);
            map.ShowSection(PaneSection.Game);
            Check("resume-returns-with-pending-edit-in-original-section", resume.Visibility == W.Visibility.Visible);
            map.ResumeEditor();
            Check("resume-hidden-while-editing", resume.Visibility == W.Visibility.Collapsed && card.Pending == 1);
            Call(window, "Show", new CityTarget(1)); map.ReturnToList();
            Check("reopened-clean-card-has-no-resume-button", resume.Visibility == W.Visibility.Collapsed);
            map.ShowEditor(card);
            var members = card.Officers;
            var tables = Field<List<GameTable>>(members, "tables");
            Check("all-member-groups-share-card-drafts", tables.All(t => t.Drafts == card.TableDrafts));
            foreach (var table in tables) table.EditingEnabled = true;
            var tableUnderTest = tables.Single(t => t.Contains(11));
            // A separate visual host gives the table genuine keyboard focus without showing the editor or connecting.
            members.Children.Remove(tableUnderTest);
            var away = new WC.TextBox();
            var panel = new WC.DockPanel(); WC.DockPanel.SetDock(away, WC.Dock.Bottom);
            panel.Children.Add(away); panel.Children.Add(tableUnderTest); host.Content = panel; host.Show(); Layout(panel);
            int column = 3; // loyalty, then leadership
            NumberField number = Begin(tableUnderTest, 11, column);
            Check("real-cell-keyboard-focus", number.Box.IsKeyboardFocused);
            number.Box.Text = "95";
            Check("typing-updates-card-and-footer-without-enter", card.Pending == 1 &&
                Field<PlateButton>(card, "apply").Badge == 1 && data.OfficerById[11][OfficerFields.Leadership] == 65);
            WI.Keyboard.Focus(away);
            Check("focus-loss-keeps-draft", card.Pending == 1 && card.TableDrafts.Find(new OfficerTarget(11), OfficerFields.Leadership)?.Value == 95);
            tableUnderTest.SetRows(tableUnderTest.Rows.Select(r => new GameTable.Row(r.Tag, (object?[])r.Cells.Clone())).ToList());
            Call(tableUnderTest, "SortBy", column);
            tableUnderTest.Visibility = W.Visibility.Collapsed; tableUnderTest.Visibility = W.Visibility.Visible; Layout(panel);
            Check("refresh-sort-and-tab-hide-keep-draft", Begin(tableUnderTest, 11, column).Box.Text == "95" && card.Pending == 1);
            object input = Field<object>(tableUnderTest, "input");
            ((Task)Call(tableUnderTest, "CommitInputAsync", input)!).GetAwaiter().GetResult();
            Check("enter-finishes-input-without-writing", card.Pending == 1 && data.OfficerById[11][OfficerFields.Leadership] == 65);
            Begin(tableUnderTest, 11, 4).Box.Text = "88";
            WI.Keyboard.Focus(away);
            Check("multiple-cells-remain-pending", card.Pending == 2);
            NumberField invalid = Begin(tableUnderTest, 11, column); invalid.Box.Text = "999999";
            WI.Keyboard.Focus(away);
            Check("invalid-input-retained-and-blocks-whole-read", card.TableDrafts.Changes(out string error) is null && error.Length > 0 && invalid.Box.Text == "999999" && card.Pending == 2);
            invalid.Box.Text = "bad input";
            tableUnderTest.SetRows(tableUnderTest.Rows.ToList());
            Check("invalid-text-survives-refresh", invalid.Box.Text == "bad input");
            invalid.Box.Text = "95";
            Call(tableUnderTest, "CancelInput");

            // Destroy and rebuild the status groups, just as a live status/membership refresh does.
            panel.Children.Remove(tableUnderTest);
            members.Show(data.Officers.Select(OfficerTables.RowOf).Select(o => (o.Status + " changed", o.Row)).ToList());
            tables = Field<List<GameTable>>(members, "tables");
            tableUnderTest = tables.Single(t => t.Contains(11));
            tableUnderTest.EditingEnabled = true; members.Children.Remove(tableUnderTest); panel.Children.Add(tableUnderTest); Layout(panel);
            Check("status-regrouping-preserves-input", Begin(tableUnderTest, 11, column).Box.Text == "95" && card.Pending == 2);
            Call(tableUnderTest, "CancelInput");
            var fields = Field<Dictionary<FieldDef, NumberField>>(card, "fields");
            fields[CityFields.Gold].Set(12345);
            Check("regular-and-table-inputs-share-footer-count", card.Pending == 3);
            var own = CityEdits.Plan(data, 1, new([new(CityFields.Gold, 12345)], [], 11));
            var drafts = card.TableDrafts.Changes(out error)!;
            EditPlan plan = EditorWindow.PlanCardEdits(data, own, drafts);
            Check("one-plan-merges-governor-status-and-multiple-abilities", !plan.Refused &&
                plan.Edits.Count(e => e.Address == data.OfficerById[11].Address) == 1 &&
                plan.Edits.Single(e => e.Address == data.OfficerById[11].Address).Changes.Any(c => c.Field == OfficerFields.StatusField));
            var memory = new FakeMemory(data);
            GameMemory.Apply(memory, TestGame.Classes, plan.Edits);
            Check("single-batch-writes-city-and-officers-with-linked-experience", CityFields.Gold.Read(memory[data.Cities[1].Address]!) == 12345 &&
                OfficerFields.Leadership.Read(memory[data.OfficerById[11].Address]!) == 95 &&
                OfficerFields.Strength.Read(memory[data.OfficerById[11].Address]!) == 88 &&
                OfficerFields.FitsExperience(memory[data.OfficerById[11].Address]!, OfficerFields.Leadership) &&
                OfficerFields.FitsExperience(memory[data.OfficerById[11].Address]!, OfficerFields.Strength));
            GameMemory.Apply(memory, TestGame.Classes, Undo.Of(plan.Edits));
            Check("one-undo-restores-all-records", plan.Edits.All(e => memory[e.Address]!.SequenceEqual(e.Expected)));
            EditPlan stale = EditorWindow.PlanCardEdits(data, own, [.. drafts, (new UnitTarget(0, 999), new List<FieldChange> { new(UnitFields.Morale, 130) })]);
            Check("stale-target-refuses-entire-batch", stale.Refused && stale.Edits.Count == 0);
            Check("refused-plan-keeps-all-inputs", card.Pending == 3 && invalid.Box.Text == "95");

            // Header fills also stage values; no writer dispatch is permitted until the footer is used.
            int writes = 0; tableUnderTest.ColumnWriter = (_, _, _) => { writes++; return Task.FromResult(true); };
            app.Dispatcher.BeginInvoke(WT.DispatcherPriority.Loaded, new Action(() =>
            {
                var modal = app.Windows.OfType<ColumnEditDialog>().Single(); modal.Opacity = 0;
                modal.Number.Box.Text = "99"; Click(modal.ApplyButton);
            }));
            var header = Field<WC.Canvas>(tableUnderTest, "header");
            Click(header.Children.OfType<PlateButton>().Single(b => b.Text == L.Lists.BulkColumn(OfficerFields.Leadership.Label)));
            Check("header-fill-stages-without-game-write", writes == 0 && card.TableDrafts.Find(new OfficerTarget(11), OfficerFields.Leadership)?.Value == 99);
            Call(window, "StageLoyalty", card, new[] { 11 }, 100);
            Check("loyalty-100-stages-without-clearing-other-drafts", card.Pending == 4 && card.TableDrafts.Find(new OfficerTarget(11), OfficerFields.Loyalty)?.Value == 100);
            card.TableDrafts.Get(new OfficerTarget(11), OfficerFields.Loyalty, 90).Set(120);
            Call(window, "StageLoyalty", card, new[] { 11 }, 100);
            Check("loyalty-100-preserves-higher-draft", card.TableDrafts.Find(new OfficerTarget(11), OfficerFields.Loyalty)?.Value == 120);
            Call(window, "StageLoyalty", card, new[] { 11 }, 150);
            Check("loyalty-shortcut-stages-without-clearing-other-drafts", card.Pending == 4 && card.TableDrafts.Find(new OfficerTarget(11), OfficerFields.Loyalty)?.Value == 150);
            // The direct button event used by the screenshot must dispatch the card apply path.
            bool footerClicked = false; card.ApplyClicked += () => footerClicked = true;
            Click(Field<PlateButton>(card, "apply")); // No game connection: the actual write handler refuses safely.
            Check("footer-dispatches-with-drafts-intact-when-disconnected", footerClicked && card.Pending == 4);
            card.TableDrafts.Follow(new OfficerTarget(11), OfficerFields.Leadership, 99);
            Check("matching-live-value-clears-only-satisfied-draft", card.Pending == 3);
            Call(card, "Revert");
            Check("revert-clears-regular-and-table-drafts", card.Pending == 0);
            Check("gate-opens", Call(window, "Show", new GateTarget(52)) is true);
            var gate = Field<GateEditorCard>(window, "gateEditor");
            gate.TableDrafts.Get(new OfficerTarget(11), OfficerFields.Loyalty, 90).Set(120);
            Check("gate-footer-counts-table-drafts", gate.Pending == 1);
            Check("force-opens", Call(window, "Show", new ForceTarget(1)) is true);
            var force = Field<ForceEditorCard>(window, "forceEditor");
            Check("force-all-embedded-tables-share-footer-drafts", force.CitiesTable.Drafts == force.TableDrafts &&
                force.GatesTable.Drafts == force.TableDrafts && force.UnitsTable.Drafts == force.TableDrafts && force.Members.Drafts == force.TableDrafts);
            force.TableDrafts.Get(new CityTarget(1), CityFields.Gold, 0).Set(1000);
            force.TableDrafts.Get(new UnitTarget(0, 11), UnitFields.Morale, 0).Set(100);
            Call(window, "StageUnits", new[] { (0, 11) }, false);
            Check("force-shortcut-preserves-other-table-drafts", force.Pending == 2 &&
                force.TableDrafts.Find(new UnitTarget(0, 11), UnitFields.Morale)?.Value == UnitFields.MoraleCap);
            Call(window, "Show", new ForceTarget(1));
            Check("reopening-card-clears-old-drafts", force.Pending == 0);

            // Exercise the complete card's routed keyboard events and reveal an invalid cell on a hidden tab.
            Call(window, "Show", new CityTarget(1));
            ((WC.Panel)WM.VisualTreeHelper.GetParent(card)).Children.Remove(card);
            host.Content = card; card.FillsEnabled = true;
            Call(card, "ShowPage", 2); Layout(card);
            tableUnderTest = Field<List<GameTable>>(members, "tables").Single(t => t.Contains(11));
            number = Begin(tableUnderTest, 11, column); number.Box.Text = "999999";
            Call(card, "ShowPage", 0); Layout(card);
            Check("invalid-draft-on-hidden-tab-blocks-save", card.TableDrafts.Changes(out error) is null);
            card.FocusInvalidTableInput();
            Check("validation-reveals-and-focuses-invalid-cell", number.Box.IsKeyboardFocused && number.Box.Text == "999999");
            bool closed = false; card.CloseClicked += () => closed = true;
            void Key(NumberField field, WI.Key key) => field.Box.RaiseEvent(new WI.KeyEventArgs(WI.Keyboard.PrimaryDevice,
                W.PresentationSource.FromVisual(field.Box), 0, key) { RoutedEvent = WI.Keyboard.PreviewKeyDownEvent });
            Key(number, WI.Key.Escape);
            Check("escape-reverts-cell-without-closing-card", card.Pending == 0 && !closed);
            number = Begin(tableUnderTest, 11, column); number.Box.Text = "97";
            Key(number, WI.Key.Enter);
            Check("routed-enter-retains-draft-without-saving", card.Pending == 1 && !closed && !number.Box.IsKeyboardFocused);
            number = Begin(tableUnderTest, 11, column); number.Box.Text = "98";
            Click(Field<PlateButton>(card, "apply"));
            Check("footer-reads-currently-focused-input-without-enter", card.TableDrafts.Changes(out error)!
                .Single(r => r.Target == new OfficerTarget(11)).Changes.Single(c => c.Field == OfficerFields.Leadership).Value == 98);
            Console.WriteLine($"PASS table drafts: {cases} checks; gameAccess=false");
        }
        finally { host.Close(); Field<MapView>(window, "map").HideEditors(); window.Close(); app.Shutdown(); }
    }
}

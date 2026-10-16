using System.Collections;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckForcePolicies(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        int cases = 0;
        void Check(string scenario, bool passed, object? details = null)
        {
            string line = JsonSerializer.Serialize(new { scenario, passed, details, gameAccess = false });
            log.WriteLine(line); Console.WriteLine(line); Require(passed, scenario); cases++;
        }
        static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Hidden)!.GetValue(obj)!;
        static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name,
            Hidden | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(obj, args);
        static void Pump()
        {
            var frame = new WT.DispatcherFrame();
            WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => frame.Continue = false));
            WT.Dispatcher.PushFrame(frame);
        }
        var data = new TestGame().Force(4, 503, 4).Officer(503, "원상", 4, 6, 1)
            .Officer(715, "장수 A", 4, 6, 4).With(715, OfficerFields.Policy, 1)
            .Officer(236, "장수 B", 4, 6, 4).With(236, OfficerFields.Policy, 2)
            .Officer(82, "장수 C", 4, 6, 4).With(82, OfficerFields.Policy, 28)
            .Officer(83, "장수 D", 4, 6, 4).With(83, OfficerFields.Policy, 3).Build();
        void Set(GameSnapshot snapshot, int field, int slot, int officer, int days = 0)
        {
            var bytes = snapshot.ForceRecords[4].Data;
            var who = ForceFields.PolicyOfficer(field, slot); who.Encode(officer).CopyTo(bytes, who.Offset);
            var time = ForceFields.PolicyDays(field, slot); time.Encode(days).CopyTo(bytes, time.Offset);
        }
        Set(data, 0, 1, 715); Set(data, 0, 2, 236); Set(data, 3, 1, 82);
        var window = new EditorWindow(automaticGameAccess: OfflineGameAccess) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        var map = Field<MapView>(window, "map"); var card = map.ForceEditor;
        void Snapshot(GameSnapshot snapshot) => window.GetType().GetField("snapshot", Hidden)!.SetValue(window, snapshot);
        void Open(GameSnapshot snapshot) { Snapshot(snapshot); Call(window, "Show", new ForceTarget(4)); }
        void Follow(GameSnapshot snapshot) { Snapshot(snapshot); Call(window, "RefreshEditor"); }
        object Row(int field) => ((IList)Field<object>(card, "policyRows"))[field]!;
        List<(int Officer, int Days)> Picked(int field) => (List<(int Officer, int Days)>)Row(field).GetType().GetProperty("Picked", Hidden)!.GetValue(Row(field))!;
        GameSnapshot Clone(GameSnapshot snapshot) => snapshot.With([new(RecordKind.Force,
            snapshot.ForceRecords[4].Address, snapshot.ForceRecords[4].Data, "원상군", [])]);
        try
        {
            Open(data); Pump();
            Check("opening-sparse-slots-does-not-create-five-edits", card.Pending == 0 && card.Changes().Count == 0,
                new { card.Pending, changes = card.Changes().Select(c => new { c.Field.Label, c.Value }) });
            for (int pass = 0; pass < 12; pass++) Follow(data);
            Check("repeated-refresh-keeps-sparse-slots", card.Pending == 0 && card.Changes().Count == 0
                && Picked(0)[1].Officer == 715 && Picked(0)[2].Officer == 236 && Picked(3)[1].Officer == 82);
            foreach (int field in Enumerable.Range(0, ForceFields.PolicyFields.Length))
            {
                var box = Field<WC.ComboBox>(Row(field), "add");
                foreach (int delta in new[] { -120, 120 })
                {
                    var wheel = new WI.MouseWheelEventArgs(WI.Mouse.PrimaryDevice, 0, delta) { RoutedEvent = W.UIElement.PreviewMouseWheelEvent };
                    box.RaiseEvent(wheel); Pump();
                    Check($"scroll-over-closed-policy-choice-{field}-{delta}", wheel.Handled && box.SelectedIndex == -1 && card.Pending == 0);
                }
            }
            var refreshed = Clone(data);
            Set(refreshed, 0, 1, 0); Set(refreshed, 0, 2, 0); Set(refreshed, 3, 1, 0);
            Follow(refreshed);
            Check("game-clearing-policies-does-not-requeue-old-officers", card.Pending == 0 && card.Changes().Count == 0
                && Picked(0).All(p => p.Officer == 0) && Picked(3).All(p => p.Officer == 0));
            var countdown = Clone(data); Set(countdown, 0, 1, 715, 10); Set(countdown, 0, 6, 0, 17);
            Open(countdown);
            Check("empty-slot-countdown-is-preserved-without-draft", card.Pending == 0 && card.Changes().Count == 0);
            var next = Clone(countdown); Set(next, 0, 1, 715, 9); Follow(next);
            Check("untouched-countdown-follows-game", Picked(0)[1] == (715, 9) && card.Pending == 0);
            Call(Row(0), "Hurry"); Call(card, "Render");
            Check("hurry-changes-only-occupied-countdown", card.Pending == 1 && card.Changes().Single() == new FieldChange(ForceFields.PolicyDays(0, 1), 1));
            Set(next, 0, 1, 715, 8); Follow(next);
            Check("refresh-preserves-requested-countdown", Picked(0)[1] == (715, 1) && card.Pending == 1);
            Set(next, 0, 1, 715, 1); Follow(next);
            Check("game-catching-up-clears-countdown-draft", card.Pending == 0 && card.Changes().Count == 0);
            Open(data);
            var add = Field<WC.ComboBox>(Row(0), "add"); add.SelectedIndex = 0; Pump();
            Check("explicit-add-fills-empty-slot-without-moving-existing-officers", Picked(0)[0] == (83, 1)
                && Picked(0)[1].Officer == 715 && Picked(0)[2].Officer == 236 && card.Pending == 2);
            Follow(refreshed);
            Check("refresh-preserves-only-user-added-policy", Picked(0)[0] == (83, 1)
                && Picked(0)[1].Officer == 0 && Picked(0)[2].Officer == 0 && card.Pending == 2);
            Open(data);
            var chips = Field<WC.WrapPanel>(Row(0), "chips");
            var remove = ((WC.StackPanel)((WC.Border)chips.Children[0]).Child).Children.OfType<WC.TextBlock>().Single(t => t.Text == "✕");
            remove.RaiseEvent(new WI.MouseButtonEventArgs(WI.Mouse.PrimaryDevice, 0, WI.MouseButton.Left) { RoutedEvent = W.UIElement.MouseLeftButtonUpEvent });
            Check("explicit-remove-preserves-other-slot-positions", Picked(0)[1].Officer == 0 && Picked(0)[2].Officer == 236
                && card.Pending == 1 && card.Changes().Single() == new FieldChange(ForceFields.PolicyOfficer(0, 1), 0));
            Follow(data);
            Check("refresh-preserves-explicit-removal", card.Pending == 1 && Picked(0)[1].Officer == 0 && Picked(0)[2].Officer == 236);
            var plan = ForceEdits.Plan(data, 4, [new(ForceFields.PolicyOfficer(0, 0), 0), new(ForceFields.PolicyOfficer(0, 1), 0)], []);
            Check("write-plan-excludes-identical-policy-values", !plan.Refused && plan.Edits.SelectMany(e => e.Changes).Count() == 1
                && plan.Edits.SelectMany(e => e.Changes).Single() == new FieldChange(ForceFields.PolicyOfficer(0, 1), 0));
            Console.WriteLine($"Native WPF force policies: {cases} cases passed; gameAccess=false");
        }
        finally { map.HideEditors(); window.Close(); }
    }
}

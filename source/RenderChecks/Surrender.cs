using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void CheckSurrender(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        int cases = 0;
        void Check(string scenario, bool passed)
        {
            string line = JsonSerializer.Serialize(new { scenario, passed, gameAccess = false });
            log.WriteLine(line); Console.WriteLine(line); Require(passed, scenario); cases++;
        }
        static void Click(PlateButton button) => ((Action)typeof(PlateButton).GetField("Click", Hidden)!.GetValue(button)!)();
        static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
        static object? Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Hidden)!.Invoke(owner, args);
        void Capture(W.FrameworkElement root, string name)
        {
            foreach (int dpi in new[] { 96, 144 })
            {
                var bitmap = new WMI.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi / 96),
                    (int)Math.Ceiling(root.ActualHeight * dpi / 96), dpi, dpi, WM.PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"{name}-{dpi}.png")); encoder.Save(file);
            }
        }
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        GameSnapshot data = new TestGame().Force(1, 1, 1).Force(2, 2, 2).Force(3, 3, 3).Force(46, 46, 46)
            .Officer(1, "조조", 1, 1, 1).Officer(2, "유비", 2, 2, 1).Officer(3, "손권", 3, 3, 1)
            .Officer(46, "이민족", 46, 4, 1).Build(playerForce: 1);
        var dialog = new SurrenderDialog(data) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            dialog.Show(); dialog.UpdateLayout();
            Check("player-is-default-recipient-and-no-rulers-start-selected", dialog.Target == 1 && dialog.Sources.Length == 0 && !dialog.AcceptButton.IsEnabled);
            Check("player-and-tribes-excluded-from-source-selection", dialog.SourceChoices.Keys.Order().SequenceEqual(new[] { 2, 3 }));
            Click(dialog.SelectAllButton);
            Check("select-all-chooses-multiple-rulers", dialog.Sources.SequenceEqual(new[] { 2, 3 }) && dialog.AcceptButton.IsEnabled);
            dialog.TargetChoice.SelectedValue = 2;
            Check("recipient-change-clears-all-rulers-and-disables-accept", !dialog.SourceChoices[2].IsEnabled &&
                dialog.SourceChoices.Values.All(c => c.IsChecked == false) && dialog.Sources.Length == 0 && !dialog.AcceptButton.IsEnabled);
            Click(dialog.SelectAllButton);
            Check("new-recipient-requires-explicit-selection", dialog.Sources.SequenceEqual(new[] { 3 }) && dialog.AcceptButton.IsEnabled);
            dialog.TargetChoice.SelectedValue = 1;
            Check("changing-back-does-not-restore-old-selection", dialog.SourceChoices[2].IsEnabled && dialog.Sources.Length == 0 && !dialog.AcceptButton.IsEnabled);
            Click(dialog.SelectAllButton);
            Click(dialog.ClearButton);
            Check("empty-selection-disables-accept", dialog.Sources.Length == 0 && !dialog.AcceptButton.IsEnabled);
            Click(dialog.SelectAllButton);
            Check("select-all-keeps-player-recipient-excluded", dialog.Sources.SequenceEqual(new[] { 2, 3 }));
            dialog.TargetChoice.SelectedValue = 1;
            Check("unchanged-recipient-keeps-explicit-selection", dialog.Sources.SequenceEqual(new[] { 2, 3 }));
            dialog.UpdateLayout();
            var root = (W.FrameworkElement)dialog.Content;
            Check("dialog-fits-work-area", dialog.ActualWidth <= W.SystemParameters.WorkArea.Width && dialog.ActualHeight <= W.SystemParameters.WorkArea.Height);
            Capture(root, "surrender");
            // Real loyalty buttons must route their own floor and preserve the status scope.
            var officers = new OfficerTables("") { FillsEnabled = true };
            var memberData = new TestGame().Force(1, 1, 1).Officer(1, "군주", 1, 1, 1).Officer(2, "장수", 1, 1, 4).Build();
            officers.Show(memberData.Officers.Select(OfficerTables.RowOf).ToList());
            var buttons = officers.Children.OfType<WC.DockPanel>().SelectMany(p => p.Children.OfType<PlateButton>()).ToList();
            var fills = new List<(int[] Ids, int Value)>();
            officers.LoyaltyClicked += (ids, value) => fills.Add((ids.ToArray(), value));
            Click(buttons.Single(b => b.Text == L.Officer.LoyaltyFill(100)));
            Click(buttons.Single(b => b.Text == L.Officer.LoyaltyFill(150)));
            Check("both-loyalty-buttons-dispatch-floor-for-vassals-only", buttons.Count == 2 && fills.Select(f => f.Value).SequenceEqual(new[] { 100, 150 }) &&
                fills.All(f => f.Ids.SequenceEqual(new[] { 2 })));
            officers.FillsEnabled = false;
            Check("both-loyalty-buttons-disable-when-disconnected", buttons.All(b => !b.IsEnabled));
            var forceCard = new ForceEditorCard();
            var single = (PlateButton)typeof(ForceEditorCard).GetField("surrenderButton", Hidden)!.GetValue(forceCard)!;
            Check("force-card-keeps-only-single-surrender", !single.IsEnabled &&
                typeof(ForceEditorCard).GetField("batchSurrenderButton", Hidden) is null);
            forceCard.FillsEnabled = true;
            int singleCalls = 0;
            forceCard.SurrenderClicked += () => singleCalls++;
            Click(single);
            Check("force-card-still-routes-single-surrender", singleCalls == 1 && single.IsEnabled);
            forceCard.FillsEnabled = false;
            Check("single-surrender-disables-while-busy-or-disconnected", !single.IsEnabled);

            string storage = Path.GetFullPath(Path.Combine(output, "storage"));
            var editor = OfflineEditor(() => false, storage, storage, storage);
            var editorRoot = (W.FrameworkElement)editor.Content;
            editor.Content = null;
            var host = new W.Window { Content = editorRoot, Width = 1000, Height = 700, Opacity = 0,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
                var batch = Field<PlateButton>(editor, "batchSurrenderButton");
                Check("list-batch-action-starts-disabled", !batch.IsEnabled);
                typeof(EditorWindow).GetField("snapshot", Hidden)!.SetValue(editor, data);
                Call(editor, "FillLists");
                var lists = Field<ListsCard>(editor, "lists");
                Field<MapView>(editor, "map").ShowLists(true);
                lists.ShowTab((int)ListKind.Forces); host.Show(); host.UpdateLayout();
                W.DependencyObject? parent = batch;
                while (parent is not null && !ReferenceEquals(parent, lists)) parent = WM.VisualTreeHelper.GetParent(parent);
                Check("batch-action-is-visible-in-force-list", batch.IsVisible && batch.IsEnabled && ReferenceEquals(parent, lists));
                Capture(lists, "force-list");
                lists.ShowTab((int)ListKind.Cities); host.UpdateLayout();
                Check("batch-action-is-hidden-on-other-lists", !batch.IsVisible);
                lists.ShowTab((int)ListKind.Forces); host.UpdateLayout();
                Call(editor, "SetBusy", true, false);
                Check("list-batch-action-disables-while-busy", !batch.IsEnabled);
                Call(editor, "SetBusy", false, false);
                Check("list-batch-action-enables-after-busy", batch.IsEnabled);
                typeof(EditorWindow).GetField("snapshot", Hidden)!.SetValue(editor, null);
                Call(editor, "UpdateControls");
                Check("list-batch-action-disables-when-disconnected", !batch.IsEnabled);
            }
            finally { host.Close(); editor.Close(); }
            Console.WriteLine($"PASS surrender: {cases} checks; gameAccess=false");
        }
        finally { dialog.Close(); app.Shutdown(); }
    }
}

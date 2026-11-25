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
    // [일괄 부활] without a game: the dialog's selection and draws, the officer list's button and the stop button.
    private static void CheckBatchRevival(string output)
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
        // Force 1 (조조) holds city 1. 하진·고순·장각 are dead, 윤대목 has not appeared, 임시 is a generated slot and
        // 여포 still leads a unit.
        GameSnapshot data = new TestGame().Force(1, 1, 1).City(1, 1).Officer(1, "조조", 1, 1, 1)
            .Officer(12, "하진", 0, 0, 9).Officer(13, "고순", 0, 0, 9).Officer(14, "장각", 0, 0, 9)
            .Officer(15, "윤대목", 0, 0, 7).Officer(16, "임시", 0, 0, 9).Officer(17, "여포", 0, 0, 9).Leading(17)
            .Build(playerForce: 1);
        data.OfficerById[16].Data[0x15F] |= 0x40;
        int year = data.Date!.Value.Year;
        var dialog = new BatchRevivalDialog(data, new Random(7)) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            dialog.Show(); dialog.UpdateLayout();
            int[] dead = [13, 14, 12];
            Check("opens-with-every-revivable-dead-officer-checked", dialog.Rows.Select(r => r.Officer.Id).SequenceEqual(dead) &&
                dialog.Selected.SequenceEqual(dead) && dialog.AcceptButton.IsEnabled);
            Check("defaults-to-free-officers-in-random-cities-for-20-to-40-years", dialog.MinYears.Text == "20" && dialog.MaxYears.Text == "40" &&
                dialog.PickCount.Text == "10" && dialog.CityChoice.Visibility == W.Visibility.Collapsed &&
                dialog.Requests.All(r => r.Mode == RevivalMode.Free && r.DeathYear >= year + 20 && r.DeathYear <= year + 40 && data.IsCity(r.Base)));
            Check("rows-show-what-will-be-written", dialog.Rows.All(r => dialog.Requests.Single(q => q.Officer == r.Officer.Id) is var q &&
                r.Year.Text == q.DeathYear.ToString() && r.City.Text == data.CityName(q.Base) &&
                r.Status.Text == OfficerFields.StatusName(9) + " → " + L.Terms.StatusFree));
            Check("skipped-officers-are-counted", Field<WC.TextBlock>(dialog, "count").Text ==
                L.BatchRevival.SelectedCount(3) + " · " + L.BatchRevival.Skipped(2));

            dialog.HiddenChoice.IsChecked = true;
            Check("hidden-officers-join-unchecked", dialog.Rows.Count == 4 && dialog.Selected.SequenceEqual(dead) &&
                dialog.Rows.Single(r => r.Officer.Id == 15).Year.Text.Length > 0);
            var before = dialog.Requests.ToArray();
            dialog.HiddenChoice.IsChecked = false;
            Check("hidden-toggle-keeps-earlier-draws", dialog.Rows.Count == 3 && dialog.Requests.SequenceEqual(before));

            bool changed = false;
            for (int i = 0; i < 10 && !changed; i++)
            {
                Click(dialog.ShuffleButton);
                changed = !dialog.Requests.SequenceEqual(before);
            }
            Check("shuffle-redraws-without-touching-checks", changed && dialog.Selected.SequenceEqual(dead));

            dialog.PickCount.Text = "2";
            Click(dialog.PickButton);
            int[] firstPick = dialog.Selected;
            Check("random-pick-checks-the-asked-number", firstPick.Length == 2 && firstPick.All(dead.Contains));
            Click(dialog.ShuffleButton);
            Check("shuffle-keeps-a-random-pick", dialog.Selected.SequenceEqual(firstPick));
            dialog.PickCount.Text = "99";
            Click(dialog.PickButton);
            Check("pick-above-the-candidates-checks-all", dialog.Selected.SequenceEqual(dead));
            dialog.PickCount.Text = "0";
            Check("invalid-pick-count-disables-pick", !dialog.PickButton.IsEnabled && dialog.Error == L.BatchRevival.InvalidCount);
            dialog.PickCount.Text = "10";
            Check("valid-pick-count-restores-pick", dialog.PickButton.IsEnabled && dialog.Error.Length == 0);

            dialog.PlaceChoice.SelectedIndex = 1;
            int fixedIndex = dialog.CityChoice.Items.Cast<string>().ToList().FindIndex(s => s.StartsWith(data.CityName(2)));
            dialog.CityChoice.SelectedIndex = fixedIndex;
            Check("fixed-city-sends-everyone-there", dialog.CityChoice.Visibility == W.Visibility.Visible &&
                dialog.Requests.All(r => r.Base == 2 && r.Mode == RevivalMode.Free) && dialog.AcceptButton.IsEnabled);
            dialog.StatusChoice.SelectedIndex = 1;
            Check("serving-limits-fixed-cities-to-living-rulers", dialog.CityChoice.Items.Count == 1 &&
                dialog.Requests.All(r => r.Base == 1 && r.Mode == RevivalMode.Serving) &&
                dialog.Rows.All(r => r.City.Text == data.CityName(1) + " · " + data.ForceName(1)));
            dialog.StatusChoice.SelectedIndex = 2;
            Check("random-status-keeps-a-city-both-statuses-take", dialog.CityChoice.Items.Count == 1 && dialog.Requests.All(r => r.Base == 1));
            dialog.StatusChoice.SelectedIndex = 0;
            dialog.PlaceChoice.SelectedIndex = 0;
            Check("random-city-hides-the-city-choice", dialog.CityChoice.Visibility == W.Visibility.Collapsed);

            dialog.MinYears.Text = "50";
            Check("reversed-years-block-accept", !dialog.AcceptButton.IsEnabled && dialog.Error == L.BatchRevival.InvalidYears(999 - year) &&
                dialog.Rows.All(r => r.Year.Text.Length == 0));
            dialog.MinYears.Text = "20";
            Check("valid-years-draw-again", dialog.AcceptButton.IsEnabled && dialog.Error.Length == 0 && dialog.Rows.All(r => r.Year.Text.Length > 0));
            Click(dialog.ClearButton);
            Check("empty-selection-blocks-accept", dialog.Selected.Length == 0 && !dialog.AcceptButton.IsEnabled);
            Click(dialog.SelectAllButton);
            Check("select-all-restores-every-candidate", dialog.Selected.SequenceEqual(dead) && dialog.AcceptButton.IsEnabled);

            dialog.UpdateLayout();
            Check("dialog-fits-work-area", dialog.ActualWidth <= W.SystemParameters.WorkArea.Width && dialog.ActualHeight <= W.SystemParameters.WorkArea.Height);
            Capture((W.FrameworkElement)dialog.Content, "batch-revival");

            string storage = Path.GetFullPath(Path.Combine(output, "storage"));
            var editor = OfflineEditor(() => false, storage, storage, storage);
            var editorRoot = (W.FrameworkElement)editor.Content;
            editor.Content = null;
            var host = new W.Window { Content = editorRoot, Width = 1000, Height = 700, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                var batch = Field<PlateButton>(editor, "batchRevivalButton");
                Check("list-batch-revival-starts-disabled", !batch.IsEnabled);
                typeof(EditorWindow).GetField("snapshot", Hidden)!.SetValue(editor, data);
                Call(editor, "FillLists");
                var lists = Field<ListsCard>(editor, "lists");
                Field<MapView>(editor, "map").ShowLists(true);
                lists.ShowTab((int)ListKind.Officers); host.Show(); host.UpdateLayout();
                W.DependencyObject? parent = batch;
                while (parent is not null && !ReferenceEquals(parent, lists)) parent = WM.VisualTreeHelper.GetParent(parent);
                Check("batch-revival-is-visible-in-officer-list", batch.IsVisible && batch.IsEnabled && ReferenceEquals(parent, lists));
                Capture(lists, "officer-list");
                lists.ShowTab((int)ListKind.Forces); host.UpdateLayout();
                Check("batch-revival-is-hidden-on-other-lists", !batch.IsVisible);
                lists.ShowTab((int)ListKind.Officers); host.UpdateLayout();

                var cancel = Field<PlateButton>(editor, "cancelButton");
                Call(editor, "SetBusy", true, false);
                Check("batch-revival-disables-while-busy", !batch.IsEnabled && cancel.Visibility == W.Visibility.Collapsed);
                using var stop = new CancellationTokenSource();
                typeof(EditorWindow).GetField("batchStop", Hidden)!.SetValue(editor, stop);
                Call(editor, "UpdateControls");
                Check("running-batch-shows-the-stop-button", cancel.Visibility == W.Visibility.Visible && cancel.Text == L.BatchRevival.Stop &&
                    Equals(cancel.ToolTip, L.BatchRevival.StopTip));
                Click(cancel);
                Check("stop-button-asks-the-batch-to-stop", stop.IsCancellationRequested);
                typeof(EditorWindow).GetField("batchStop", Hidden)!.SetValue(editor, null);
                Call(editor, "SetBusy", false, false);
                Check("finished-batch-restores-search-cancel", cancel.Visibility == W.Visibility.Collapsed && cancel.Text == L.Window.CancelSearch &&
                    Equals(cancel.ToolTip, L.Window.CancelSearchTip) && batch.IsEnabled);
                typeof(EditorWindow).GetField("snapshot", Hidden)!.SetValue(editor, null);
                Call(editor, "UpdateControls");
                Check("batch-revival-disables-when-disconnected", !batch.IsEnabled);
            }
            finally { host.Close(); editor.Close(); }
            Console.WriteLine($"PASS batch revival: {cases} checks; gameAccess=false");
        }
        finally { dialog.Close(); app.Shutdown(); }
    }
}

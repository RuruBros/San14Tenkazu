using System.Diagnostics;
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
    private static void CheckUiOrganization(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        void Check(string name, bool passed, object? details = null)
        {
            string line = JsonSerializer.Serialize(new { name, passed, details, gameAccess = false });
            log.WriteLine(line); Console.WriteLine(line);
            if (!passed) throw new InvalidOperationException(name);
        }
        static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
        static object? Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Hidden)!.Invoke(owner, args);
        static void Layout(W.FrameworkElement root, double width, double height)
        { root.Measure(new W.Size(width, height)); root.Arrange(new W.Rect(0, 0, width, height)); root.UpdateLayout(); }
        void Capture(W.FrameworkElement root, string name)
        {
            var image = new WMI.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, WM.PixelFormats.Pbgra32);
            var background = new WM.DrawingVisual();
            using (var draw = background.RenderOpen()) draw.DrawRectangle(CardSkin.Parchment, null, new W.Rect(0, 0, root.ActualWidth, root.ActualHeight));
            image.Render(background); image.Render(root);
            var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        string storage = Path.GetFullPath(Path.Combine(output, "storage"));
        var editor = OfflineEditor(() => false, storage, storage, storage);
        // Never show EditorWindow: its ContentRendered handler connects to a real game.
        var root = (W.FrameworkElement)editor.Content;
        var fixture = new TestGame().Force(1, 1, 1).City(1, 1);
        for (int i = 1; i <= 1000; i++) fixture.Officer(i, "장수" + i, 1, 1, 4);
        typeof(EditorWindow).GetField("snapshot", Hidden)!.SetValue(editor, fixture.Build());
        Call(editor, "FillLists");
        var lists = Field<ListsCard>(editor, "lists");
        var map = Field<MapView>(editor, "map");
        map.ShowLists(true); lists.ShowTab((int)ListKind.Officers);
        var tables = (GameTable[])typeof(EditorWindow).GetProperty("ListTables", Hidden)!.GetValue(editor)!;
        var before = tables.Select(t => t.Rows).ToArray();
        Field<SearchBox>(editor, "citySearch").Text = "존재하지 않는 도시";
        Field<SearchBox>(editor, "officerSearch").Text = "장수1000";
        Call(editor, "RefreshLists", true);
        Check("search-filters-current-table", tables[(int)ListKind.Officers].Rows.Count == 1);
        Check("search-keeps-other-thirteen-tables", tables.Select((t, i) => i == (int)ListKind.Officers || ReferenceEquals(t.Rows, before[i])).All(x => x));
        Check("hidden-filter-is-deferred", tables[(int)ListKind.Cities].Rows.Count > 0);
        lists.ShowTab((int)ListKind.Cities); lists.ShowTab((int)ListKind.Officers);
        Check("hidden-filter-applies-on-navigation", tables[(int)ListKind.Cities].Rows.Count == 0);
        Check("navigation-keeps-filter", tables[(int)ListKind.Officers].Rows.Count == 1);
        Field<SearchBox>(editor, "officerSearch").Text = "";
        Call(editor, "RefreshLists", true);
        Check("cleared-search-restores-all-officers", tables[(int)ListKind.Officers].Rows.Count == 1000);
        double Measure(bool current)
        {
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 30; i++) Call(editor, "RefreshLists", current);
            return watch.Elapsed.TotalMilliseconds / 30;
        }
        Measure(true); Measure(false);
        Check("fixture-refresh-timing", true, new { officers = 1000, iterations = 30, currentMs = Measure(true), allMs = Measure(false) });
        foreach (int width in new[] { 1000, 1500 })
        {
            Layout(root, width, 800);
            var status = Field<WC.TextBlock>(editor, "status");
            var settings = Field<PlateButton>(editor, "settingsButton");
            var point = settings.TranslatePoint(new W.Point(), root);
            Check("toolbar-fits-" + width, point.X >= 0 && point.X + settings.ActualWidth <= width && status.ActualWidth >= 100);
            lists.ShowTab((int)ListKind.Formations); Layout(root, width, 800);
            Capture(root, "combat-" + width);
            lists.ShowTab((int)ListKind.Policies); Layout(root, width, 800);
            Capture(root, "domestic-" + width);
        }
        editor.Content = null;
        var host = new W.Window { Content = root, Width = 1000, Height = 800, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            host.Show(); host.UpdateLayout();
            var button = Field<PlateButton>(editor, "settingsButton");
            ((Action)typeof(PlateButton).GetField("Click", Hidden)!.GetValue(button)!).Invoke();
            var popup = Field<System.Windows.Controls.Primitives.Popup>(editor, "settingsPopup");
            Check("settings-popup-opens", popup.IsOpen && popup.Child is WC.Border);
            var popupRoot = (W.FrameworkElement)popup.Child;
            Layout(popupRoot, 260, 105); Capture(popupRoot, "settings-menu");
            Check("disconnected-rules-allow-saved-settings", Field<PlateButton>(editor, "runtimeRulesButton").IsEnabled);
            ((Action)typeof(PlateButton).GetField("Click", Hidden)!.GetValue(button)!).Invoke();
            Check("settings-popup-toggles-closed", !popup.IsOpen);
        }
        finally { host.Close(); }
        var rules = new RuntimeRulesWindow(RuntimeRuleSettings.Original);
        try
        {
            string applied = Field<WC.TextBlock>(rules, "applied").Text;
            rules.TraitList.IsChecked = true;
            Check("selection-does-not-change-applied-summary", Field<WC.TextBlock>(rules, "applied").Text == applied
                && Field<WC.TextBlock>(rules, "pending").Text.Contains("아직 적용"));
            rules.ShowSettings(new(true, AbilityRuleMode.Growth200, true));
            Check("readback-updates-summary", Field<WC.TextBlock>(rules, "applied").Text.Contains("9개")
                && Field<WC.TextBlock>(rules, "pending").Text.Contains("같습니다"));
            var content = (WC.ScrollViewer)rules.Content;
            Layout(content, 530, 600); Capture(content, "rules");
            var panel = (WC.StackPanel)content.Content;
            panel.Children.OfType<WC.Expander>().Single().IsExpanded = true;
            Layout(content, 530, 480);
            Check("expanded-help-scrolls-on-small-window", content.ScrollableHeight > 0);
            Capture(content, "rules-help");
        }
        finally { rules.Close(); editor.Close(); app.Shutdown(); }
    }
}

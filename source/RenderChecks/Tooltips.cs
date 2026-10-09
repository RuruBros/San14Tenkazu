using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using San14Tenkazu;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    // Exercise the product's implicit popup style and runtime strings, including unbroken paths.
    // No browser, game access or changes to the user's running editor.
    private static void CheckTooltips(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Tooltip output already exists");
        Directory.CreateDirectory(output);
        using var journal = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        TooltipPresentation.Install(app.Resources);
        int cases = 0;
        void Record(string scenario, bool passed, object details)
        {
            cases++;
            string row = JsonSerializer.Serialize(new { scenario, passed, details });
            journal.WriteLine(row);
            Require(passed, "Tooltip check failed: " + scenario);
        }
        static IEnumerable<T> Descendants<T>(W.DependencyObject root) where T : W.DependencyObject
        {
            if (root is T match) yield return match;
            for (int i = 0; i < WM.VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (T child in Descendants<T>(WM.VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        static void Layout(W.FrameworkElement root)
        {
            root.Measure(new W.Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new W.Rect(new W.Point(), root.DesiredSize));
            root.UpdateLayout();
        }
        void Capture(W.FrameworkElement root, string name)
        {
            foreach (int dpi in new[] { 96, 144 })
            {
                var bitmap = new WMI.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi / 96),
                    (int)Math.Ceiling(root.ActualHeight * dpi / 96), dpi, dpi, WM.PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new WMI.PngBitmapEncoder();
                encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, name + "-" + dpi + ".png"));
                encoder.Save(file);
            }
        }
        void Check(string name, string text, bool capture = false, bool mustWrap = false)
        {
            var tip = new WC.ToolTip { Content = text };
            Layout(tip);
            WC.TextBlock? body = Descendants<WC.TextBlock>(tip).SingleOrDefault(t => t.Text == text);
            var oneLine = new WC.TextBlock { Text = text, FontFamily = tip.FontFamily, FontSize = tip.FontSize };
            oneLine.Measure(new W.Size(double.PositiveInfinity, double.PositiveInfinity));
            Record(name, tip.Style is not null && tip.ActualWidth <= TooltipPresentation.MaximumWidth + .01 && tip.ActualWidth > 0
                && body is { TextWrapping: W.TextWrapping.Wrap, TextTrimming: W.TextTrimming.None }
                && body.ActualWidth + .01 >= body.DesiredSize.Width && body.ActualHeight + .01 >= body.DesiredSize.Height
                && (!mustWrap || body.ActualHeight > oneLine.DesiredSize.Height),
                new { text, width = tip.ActualWidth, height = tip.ActualHeight, textWidth = body?.ActualWidth,
                    textHeight = body?.ActualHeight, unwrappedHeight = oneLine.DesiredSize.Height });
            if (capture) Capture(tip, name);
        }
        try
        {
            foreach ((string key, MemberInfo member) in L.Members().Where(m => m.Key.EndsWith("Tip", StringComparison.Ordinal)
                || m.Key.EndsWith("Hint", StringComparison.Ordinal) || m.Key is "Registered.FaceNote" or "Navigation.AncientNote"))
            {
                object? text = member is PropertyInfo property ? property.GetValue(null)
                    : ((MethodInfo)member).Invoke(null, ((MethodInfo)member).GetParameters().Select(p =>
                        (object)(p.Name is "description" ? L.Ai.TraitWarlikeTip : p.Name is "name" or "category" or "field" or "tie" ? "등록 장수" : 48)).ToArray());
                Check(key, (string)text!, key is "Seasons.OnTip" or "Seasons.OffTip" or "Window.PanelTip"
                    or "Force.AllianceTip" or "Ai.TraitWarlikeTip" or "Registered.RunningTip");
            }
            Check("runtime-path", @"C:\Sample\San14Tenkazu\" + new string('a', 160) + ".s14", true, true);
            Check("runtime-error", L.Window.Error(new string('E', 180)), true, true);
            Check("runtime-undo", L.Undo.LastTip(12, string.Join(" · ", Enumerable.Repeat(L.Ai.TraitFameTip, 3))), true);

            var tip = new WC.ToolTip { Content = L.Seasons.OnTip, Opacity = 0 };
            try
            {
                tip.IsOpen = true;
                var frame = new WT.DispatcherFrame();
                tip.Dispatcher.BeginInvoke(WT.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                WT.Dispatcher.PushFrame(frame);
                Record("native-popup-style", tip.IsLoaded && tip.ActualWidth is > 0 and <= TooltipPresentation.MaximumWidth
                    && Descendants<WC.TextBlock>(tip).Any(t => t.Text == L.Seasons.OnTip && t.TextWrapping == W.TextWrapping.Wrap),
                    new { tip.IsLoaded, tip.ActualWidth, tip.ActualHeight });
                tip.Content = L.Seasons.OffTip;
                tip.UpdateLayout();
                Record("native-popup-content-update", Descendants<WC.TextBlock>(tip).Any(t => t.Text == L.Seasons.OffTip),
                    new { content = tip.Content });
            }
            finally { tip.IsOpen = false; }

            // The map uses a separate hover card. Check its actual border and text at both map widths.
            var map = new MapSurface();
            var card = (WC.Border)typeof(MapSurface).GetField("card", Hidden)!.GetValue(map)!;
            var body = (WC.TextBlock)typeof(MapSurface).GetField("cardText", Hidden)!.GetValue(map)!;
            body.Text = L.Map.CityTip("장수와 도시 이름이 긴 지도 설명", "세력 이름이 긴 지도 설명", "거대")
                + "\n" + L.Ai.TraitFameTip;
            card.Visibility = W.Visibility.Visible;
            foreach (int width in new[] { 300, 600 })
            {
                map.Measure(new W.Size(width, 420));map.Arrange(new W.Rect(0, 0, width, 420));map.UpdateLayout();
                foreach (W.Point position in new[] { new W.Point(2, 2), new W.Point(width - 2, 2), new W.Point(width - 2, 418) })
                {
                    typeof(MapSurface).GetMethod("PlaceCard", Hidden)!.Invoke(map, [position]);map.UpdateLayout();
                    var bounds = card.TransformToAncestor(map).TransformBounds(new W.Rect(new W.Point(), card.RenderSize));
                    Record("map-hover-" + width + "-" + position.ToString(CultureInfo.InvariantCulture),
                        body.TextWrapping == W.TextWrapping.Wrap && bounds.Left >= 4 && bounds.Right <= width - 4 + .01
                        && bounds.Top >= 4 && bounds.Bottom <= 416 + .01 && bounds.Width <= TooltipPresentation.MaximumWidth,
                        new { width, position, bounds });
                }
                Capture(card, "map-hover-" + width);
            }
            Console.WriteLine($"Native WPF tooltips: {cases} cases passed; output={output}");
        }
        finally { app.Shutdown(); }
    }
}

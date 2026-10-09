using System.IO;
using System.Text.Json;
using San14Tenkazu;
using W = System.Windows;
using WC = System.Windows.Controls;
using WP = System.Windows.Controls.Primitives;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckChoices(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Choice output already exists");
        Directory.CreateDirectory(output);
        using var journal = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        int cases = 0;
        void Record(string name, bool passed, object? details = null)
        {
            cases++;
            journal.WriteLine(JsonSerializer.Serialize(new { name, passed, details }));
            Require(passed, "Choice check failed: " + name);
        }
        static void Pump()
        {
            var frame = new WT.DispatcherFrame();
            WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            WT.Dispatcher.PushFrame(frame);
        }
        static T Part<T>(WC.ComboBox box, string name) => (T)box.Template.FindName(name, box);
        static IEnumerable<T> Descendants<T>(W.DependencyObject root) where T : W.DependencyObject
        {
            for (int i = 0; i < WM.VisualTreeHelper.GetChildrenCount(root); ++i)
            {
                var child = WM.VisualTreeHelper.GetChild(root, i);
                if (child is T found) yield return found;
                foreach (T descendant in Descendants<T>(child)) yield return descendant;
            }
        }
        var choice = new NameChoice(196, none: false);
        choice.SetNames([new(1, "남", ""), new(2, "여", ""), new(3, "그 외", "")]);
        choice.Show(1);
        var editable = EditorCard.OfficerChoice();
        editable.ItemsSource = Enumerable.Range(0, 3000).Select(i => "장수 " + i).ToArray();
        var decisions = EditorCard.Choice(double.NaN);
        decisions.HorizontalAlignment = W.HorizontalAlignment.Stretch;
        foreach (AiGuidePage page in AiGuide.Pages) decisions.Items.Add(AiLabels.Decision(page.Decision));
        decisions.SelectedIndex = 0;
        var root = new WC.StackPanel { Margin = new W.Thickness(12), Background = CardSkin.Paper,
            Children = { new WC.TextBlock { Text = L.Officer.Gender }, choice.Box,
                new WC.TextBlock { Text = L.Officer.Spouse, Margin = new W.Thickness(0, 10, 0, 0) }, editable,
                new WC.TextBlock { Text = L.Ai.Title, Margin = new W.Thickness(0, 10, 0, 0) }, decisions } };
        var host = new W.Window { Content = root, Width = 320, Height = 360, WindowStyle = W.WindowStyle.None,
            AllowsTransparency = true, Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
            FontFamily = CardSkin.Font, FontSize = CardSkin.FontSize, Foreground = CardSkin.Ink };
        void Capture(string name)
        {
            host.UpdateLayout();
            foreach (int dpi in new[] { 96, 144 })
            {
                var bitmap = new WMI.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi / 96),
                    (int)Math.Ceiling(root.ActualHeight * dpi / 96), dpi, dpi, WM.PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new WMI.PngBitmapEncoder();encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, name + "-" + dpi + ".png"));encoder.Save(file);
            }
        }
        void CapturePopup(WP.Popup popup, string name)
        {
            double priorOpacity = popup.Child.Opacity;
            popup.Child.Opacity = 1;Pump();
            try
            {
                var element = (W.FrameworkElement)popup.Child;
                foreach (int dpi in new[] { 96, 144 })
                {
                    var bitmap = new WMI.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * dpi / 96),
                        (int)Math.Ceiling(element.ActualHeight * dpi / 96), dpi, dpi, WM.PixelFormats.Pbgra32);
                    bitmap.Render(element);
                    var encoder = new WMI.PngBitmapEncoder();encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, name + "-" + dpi + ".png"));encoder.Save(file);
                }
            }
            finally { popup.Child.Opacity = priorOpacity; }
        }
        try
        {
            host.Show();Pump();host.UpdateLayout();
            var input = Part<WC.TextBox>(editable, "PART_EditableTextBox");
            Record("editable-shares-paper-and-ink", input.IsVisible && input.Background == WM.Brushes.Transparent
                && editable.Background == CardSkin.Paper && editable.BorderBrush == CardSkin.Rule && editable.Foreground == CardSkin.Ink);
            Capture("normal");
            input.Text = "장수 2499";Pump();
            Record("typed-name-search-finds-officer", editable.SelectedIndex == 2499 && editable.Text == "장수 2499",
                new { editable.SelectedIndex, editable.Text });
            editable.IsReadOnly = true;
            Record("read-only-reaches-editable-input", input.IsReadOnly);
            editable.IsReadOnly = false;
            choice.Pick(3);choice.Mark();host.UpdateLayout();
            Record("pending-colour-inside-choice", choice.Box.Background == CardSkin.Pending
                && Part<WC.Border>(choice.Box, "edge").Background == CardSkin.Pending);
            Capture("pending");choice.Pick(1);choice.Mark();
            Record("restoring-choice-clears-pending-colour", choice.Box.Background == CardSkin.Paper);

            var popup = Part<WP.Popup>(editable, "PART_Popup");
            ((W.UIElement)popup.Child).Opacity = 0;
            var toggle = Part<WP.ToggleButton>(editable, "toggle");
            typeof(WP.ToggleButton).GetMethod("OnClick", Hidden)!.Invoke(toggle, null);Pump();
            int generated = Enumerable.Range(0, editable.Items.Count).Count(i => editable.ItemContainerGenerator.ContainerFromIndex(i) is not null);
            Record("arrow-opens-virtualized-dropdown", editable.IsDropDownOpen && popup.IsOpen && generated is > 0 and < 100,
                new { generated, count = editable.Items.Count, popupHeight = ((W.FrameworkElement)popup.Child).ActualHeight });
            var selected = (WC.ComboBoxItem)editable.ItemContainerGenerator.ContainerFromIndex(2499)!;
            Record("selected-officer-is-generated-in-open-list", selected.IsSelected && selected.ActualHeight > 0
                && (string)selected.Content == "장수 2499", new { selected.ActualWidth, selected.ActualHeight });
            var presenter = Descendants<WC.ContentPresenter>(selected).First();
            var renderedText = Descendants<WC.TextBlock>(selected).ToArray();
            Record("dropdown-style-has-initialized-ink", selected.Foreground == CardSkin.Ink && selected.Background is not null
                && Part<WC.Border>(editable, "edge").BorderBrush == CardSkin.Gold,
                new { itemForeground = selected.Foreground?.ToString(), expectedForeground = CardSkin.Ink.ToString(),
                    styleForeground = editable.ItemContainerStyle.Setters.OfType<W.Setter>().First(s => s.Property == WC.Control.ForegroundProperty).Value?.ToString(),
                    popupFocusEdge = Part<WC.Border>(editable, "edge").BorderBrush?.ToString() });
            Record("open-officer-item-renders-label", Equals(presenter.Content, selected.Content)
                && renderedText.Any(t => t.Text == "장수 2499" && t.ActualWidth > 0 && t.ActualHeight > 0 && t.Foreground == CardSkin.Ink),
                new { itemContent = selected.Content, presenterContent = presenter.Content, presenter.ActualWidth, presenter.ActualHeight,
                    text = renderedText.Select(t => new { t.Text, t.ActualWidth, t.ActualHeight, foreground = t.Foreground?.ToString() }) });
            editable.IsDropDownOpen = false;Pump();
            var fixedPopup = Part<WP.Popup>(choice.Box, "PART_Popup");
            ((W.UIElement)fixedPopup.Child).Opacity = 0;
            choice.Box.IsDropDownOpen = true;Pump();
            var fixedItems = Enumerable.Range(0, choice.Box.Items.Count).Select(i => (WC.ComboBoxItem)choice.Box.ItemContainerGenerator.ContainerFromIndex(i)!).ToArray();
            Record("open-fixed-items-render-labels", fixedItems.All(item => Descendants<WC.TextBlock>(item)
                .Any(t => t.Text == (string)item.Content && t.ActualWidth > 0 && t.ActualHeight > 0 && t.Foreground == CardSkin.Ink)),
                new { items = fixedItems.Select(item => new { item.Content, text = Descendants<WC.TextBlock>(item).Select(t => new { t.Text, t.IsVisible, t.Opacity, foreground = t.Foreground?.ToString(), t.FontSize }) }) });
            CapturePopup(fixedPopup, "open-fixed-list");
            fixedItems[2].IsEnabled = false;
            Record("disabled-item-renders-muted-label", fixedItems[2].Foreground == CardSkin.InkMuted
                && Descendants<WC.TextBlock>(fixedItems[2]).Any(t => t.Text == "그 외" && t.Foreground == CardSkin.InkMuted),
                new { foreground = fixedItems[2].Foreground?.ToString() });
            fixedItems[2].IsEnabled = true;
            choice.Box.IsDropDownOpen = false;Pump();
            var decisionPopup = Part<WP.Popup>(decisions, "PART_Popup");
            decisionPopup.Child.Opacity = 0;
            decisions.IsDropDownOpen = true;Pump();
            var decisionItems = Enumerable.Range(0, decisions.Items.Count)
                .Select(i => decisions.ItemContainerGenerator.ContainerFromIndex(i)).OfType<WC.ComboBoxItem>().ToArray();
            Record("open-ai-decision-items-render-labels", decisionItems.Length > 0 && decisionItems.All(item => Descendants<WC.TextBlock>(item)
                .Any(t => t.Text == (string)item.Content && t.ActualWidth > 0 && t.ActualHeight > 0 && t.Foreground == CardSkin.Ink)),
                new { count = decisions.Items.Count, generated = decisionItems.Length, selected = decisions.SelectedItem,
                    labels = decisionItems.Select(item => item.Content) });
            CapturePopup(decisionPopup, "open-ai-decisions");
            decisions.IsDropDownOpen = false;Pump();
            var source = W.PresentationSource.FromVisual(choice.Box)!;
            choice.Box.RaiseEvent(new WI.KeyEventArgs(WI.Keyboard.PrimaryDevice, source, 0, WI.Key.Down)
                { RoutedEvent = W.UIElement.KeyDownEvent });
            Record("keyboard-down-selects-next-value", choice.Picked == 2, new { choice.Picked });
            long before = choice.Picked;
            var wheel = new WI.MouseWheelEventArgs(WI.Mouse.PrimaryDevice, 0, 120) { RoutedEvent = W.UIElement.PreviewMouseWheelEvent };
            choice.Box.RaiseEvent(wheel);
            Record("closed-wheel-preserves-selection", choice.Picked == before && wheel.Handled);
            editable.IsEnabled = false;host.UpdateLayout();
            Record("disabled-choice-disables-editable-input", !input.IsEnabled && Math.Abs(editable.Opacity - .45) < .001);
            Capture("disabled");
            Console.WriteLine($"Native WPF choices: {cases} cases passed; output={output}");
        }
        finally { editable.IsDropDownOpen = false;choice.Box.IsDropDownOpen = false;decisions.IsDropDownOpen = false;host.Close(); }
    }
}

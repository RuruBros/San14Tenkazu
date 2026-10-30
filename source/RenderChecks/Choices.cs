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
        var editable = EditorCard.Choice(196);
        editable.ItemsSource = Enumerable.Range(0, 3000).Select(i => "장수 " + i).ToArray();
        editable.SelectedIndex = 0;
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
            var input = Part<WC.TextBox>(editable, "search");
            var rows = Part<WC.ListBox>(editable, "searchRows");
            var empty = Part<WC.TextBlock>(editable, "searchEmpty");
            void Key(WI.Key pressed) => input.RaiseEvent(new WI.KeyEventArgs(WI.Keyboard.PrimaryDevice, W.PresentationSource.FromVisual(input), 0, pressed) { RoutedEvent = WI.Keyboard.PreviewKeyDownEvent });
            WC.ListBoxItem RowItem(int at) { rows.ScrollIntoView(rows.Items[at]); Pump(); return (WC.ListBoxItem)rows.ItemContainerGenerator.ContainerFromIndex(at)!; }
            static void Press(W.UIElement target, W.RoutedEvent routed) => target.RaiseEvent(new WI.MouseButtonEventArgs(WI.Mouse.PrimaryDevice, 0, WI.MouseButton.Left) { RoutedEvent = routed });
            Record("closed-long-list-shows-paper-and-ink", !input.IsVisible && editable.Background == CardSkin.Paper && editable.BorderBrush == CardSkin.Rule && editable.Foreground == CardSkin.Ink);
            Capture("normal");
            choice.Pick(3);choice.Mark();host.UpdateLayout();
            Record("pending-colour-inside-choice", choice.Box.Background == CardSkin.Pending
                && Part<WC.Border>(choice.Box, "edge").Background == CardSkin.Pending);
            Capture("pending");choice.Pick(1);choice.Mark();
            Record("restoring-choice-clears-pending-colour", choice.Box.Background == CardSkin.Paper);

            var popup = Part<WP.Popup>(editable, "PART_Popup");
            ((W.UIElement)popup.Child).Opacity = 0;
            var toggle = Part<WP.ToggleButton>(editable, "toggle");
            typeof(WP.ToggleButton).GetMethod("OnClick", Hidden)!.Invoke(toggle, null);Pump();
            int generated = Enumerable.Range(0, rows.Items.Count).Count(i => rows.ItemContainerGenerator.ContainerFromIndex(i) is not null);
            Record("arrow-opens-virtualized-search-list", editable.IsDropDownOpen && popup.IsOpen && input.IsVisible && rows.IsVisible
                && rows.Items.Count == 3000 && generated is > 0 and < 100,
                new { generated, count = rows.Items.Count, popupHeight = ((W.FrameworkElement)popup.Child).ActualHeight });
            Record("search-box-lives-in-the-window-for-the-ime", ReferenceEquals(W.PresentationSource.FromVisual(input), W.PresentationSource.FromVisual(root))
                && !ReferenceEquals(W.PresentationSource.FromVisual(rows), W.PresentationSource.FromVisual(root)));
            Record("opening-puts-keyboard-in-search-box", input.IsKeyboardFocused, new { focus = WI.Keyboard.FocusedElement?.GetType().Name });
            Record("card-escape-leaves-an-open-list-alone", CardSkin.InOpenChoice(input) && !CardSkin.InOpenChoice(root));
            var hint = Part<WC.TextBlock>(editable, "searchHint");
            Record("empty-search-box-shows-hint-for-the-picked-name", hint.IsVisible && hint.Text == L.Card.ChoiceSearch && !Part<W.UIElement>(editable, "selection").IsVisible);
            Capture("search-open");
            input.Text = "2499";Pump();
            Record("typed-middle-of-name-narrows-rows-without-picking", rows.Items.Count == 1 && editable.SelectedIndex == 0
                && ((ChoiceRow)rows.SelectedItem).Index == 2499, new { rows.Items.Count, editable.SelectedIndex });
            Capture("searching");
            CapturePopup(popup, "open-search-list");
            input.Text = "없는 이름";Pump();
            Record("no-match-says-so-and-enter-picks-nothing", !rows.IsVisible && empty.IsVisible && empty.Text == L.Card.ChoiceNoMatch);
            Key(WI.Key.Enter);Pump();
            Record("enter-without-a-row-keeps-list-open", editable.IsDropDownOpen && editable.SelectedIndex == 0);
            input.Text = "장수 2499";Pump();
            int closedOn = -1;void OnClosed(object? _, EventArgs __) => closedOn = editable.SelectedIndex;
            editable.DropDownClosed += OnClosed;
            Key(WI.Key.Enter);Pump();
            editable.DropDownClosed -= OnClosed;
            Record("enter-picks-original-index-before-closing", editable.SelectedIndex == 2499 && closedOn == 2499 && !editable.IsDropDownOpen
                && !input.IsVisible && input.Text.Length == 0, new { editable.SelectedIndex, closedOn });
            editable.IsDropDownOpen = true;Pump();
            var selected = RowItem(2499);
            var renderedText = Descendants<WC.TextBlock>(selected).ToArray();
            Record("reopened-list-marks-picked-name-with-ink", rows.Items.Count == 3000 && rows.SelectedIndex == 2499 && selected.ActualHeight > 0
                && selected.Foreground == CardSkin.Ink && Part<WC.Border>(editable, "edge").BorderBrush == CardSkin.Gold
                && renderedText.Any(t => t.Text == "장수 2499" && t.ActualWidth > 0 && t.ActualHeight > 0 && t.Foreground == CardSkin.Ink),
                new { rows.SelectedIndex, text = renderedText.Select(t => new { t.Text, t.ActualWidth, foreground = t.Foreground?.ToString() }) });
            Key(WI.Key.Down);Pump();
            Record("down-moves-mark-and-leaves-pick", rows.SelectedIndex == 2500 && editable.SelectedIndex == 2499);
            Key(WI.Key.Escape);Pump();
            Record("escape-closes-and-leaves-pick", !editable.IsDropDownOpen && editable.SelectedIndex == 2499);
            editable.IsDropDownOpen = true;Pump();
            input.Text = "장수 7";Pump();
            var clicked = RowItem(0);int clickedIndex = ((ChoiceRow)clicked.DataContext).Index;
            Press(clicked, WI.Mouse.PreviewMouseDownEvent);
            Record("press-on-row-keeps-list-open", editable.IsDropDownOpen && editable.SelectedIndex == 2499);
            Press(clicked, WI.Mouse.PreviewMouseUpEvent);Pump();
            Record("click-picks-row", !editable.IsDropDownOpen && editable.SelectedIndex == clickedIndex && clickedIndex == 7, new { clickedIndex });
            editable.IsDropDownOpen = true;Pump();input.Text = "없는 이름";Pump();
            editable.ItemsSource = new[] { "장수 없는 이름 1" }.Concat(Enumerable.Range(0, 20).Select(i => "장수 " + i)).ToArray();Pump();
            Record("rows-follow-items-replaced-while-open", rows.Items.Count == 1 && ((ChoiceRow)rows.Items[0]).Label == "장수 없는 이름 1");
            editable.IsDropDownOpen = false;Pump();
            var guarded = new NameChoice(196, none: false);
            guarded.SetNames(Enumerable.Range(1, 20).Select(i => new GameName(i, "개성 " + i, i == 2 ? "설명" : "")));
            guarded.Show(1);guarded.Allow(id => id == 2 ? "이미 있음" : "");
            root.Children.Add(guarded.Box);host.UpdateLayout();
            var guardedInput = Part<WC.TextBox>(guarded.Box, "search");var guardedRows = Part<WC.ListBox>(guarded.Box, "searchRows");
            Part<WP.Popup>(guarded.Box, "PART_Popup").Child.Opacity = 0;
            guarded.Box.IsDropDownOpen = true;Pump();
            guardedInput.RaiseEvent(new WI.KeyEventArgs(WI.Keyboard.PrimaryDevice, W.PresentationSource.FromVisual(guardedInput), 0, WI.Key.Down) { RoutedEvent = WI.Keyboard.PreviewKeyDownEvent });Pump();
            Record("arrows-skip-a-name-that-cannot-be-picked", guardedRows.SelectedIndex == 2, new { guardedRows.SelectedIndex });
            var refused = (WC.ListBoxItem)guardedRows.ItemContainerGenerator.ContainerFromIndex(1)!;
            Record("refused-name-is-muted-with-why", !refused.IsEnabled && refused.Foreground == CardSkin.InkMuted && Equals(refused.ToolTip, "이미 있음"));
            guardedInput.Text = "개성 2";Pump();
            Record("typing-does-not-mark-a-refused-name", guardedRows.Items.OfType<ChoiceRow>().Any(r => r.Index == 1) && guardedRows.SelectedItem is ChoiceRow { Index: not 1 },
                new { marked = (guardedRows.SelectedItem as ChoiceRow)?.Index });
            guarded.Box.IsDropDownOpen = false;Pump();root.Children.Remove(guarded.Box);
            var members = EditorCard.Choice(196);
            members.DisplayMemberPath = "Name";
            members.ItemsSource = Enumerable.Range(0, 12).Select(i => new { Name = "거점 " + i, Force = i }).ToArray();
            root.Children.Add(members);host.UpdateLayout();
            var memberRows = Part<WC.ListBox>(members, "searchRows");
            Part<WP.Popup>(members, "PART_Popup").Child.Opacity = 0;
            members.IsDropDownOpen = true;Pump();
            Record("search-reads-display-member", ((ChoiceRow)memberRows.Items[3]).Label == "거점 3");
            members.IsDropDownOpen = false;Pump();root.Children.Remove(members);
            var fixedPopup = Part<WP.Popup>(choice.Box, "PART_Popup");
            ((W.UIElement)fixedPopup.Child).Opacity = 0;
            choice.Box.IsDropDownOpen = true;Pump();
            Record("short-list-opens-without-search", !Part<WC.TextBox>(choice.Box, "search").IsVisible && !Part<WC.ListBox>(choice.Box, "searchRows").IsVisible
                && Part<W.UIElement>(choice.Box, "itemsScroll").IsVisible);
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
            var decisionRows = Part<WC.ListBox>(decisions, "searchRows");
            var decisionItems = Enumerable.Range(0, decisionRows.Items.Count)
                .Select(i => decisionRows.ItemContainerGenerator.ContainerFromIndex(i)).OfType<WC.ListBoxItem>().ToArray();
            Record("open-ai-decision-rows-render-labels", decisions.Items.Count > ChoiceSearch.LongList && decisionItems.Length > 0 && decisionItems.All(item => Descendants<WC.TextBlock>(item)
                .Any(t => t.Text == ((ChoiceRow)item.DataContext).Label && t.ActualWidth > 0 && t.ActualHeight > 0 && t.Foreground == CardSkin.Ink)),
                new { count = decisions.Items.Count, generated = decisionItems.Length, selected = decisions.SelectedItem,
                    labels = decisionItems.Select(item => ((ChoiceRow)item.DataContext).Label) });
            CapturePopup(decisionPopup, "open-ai-decisions");
            decisions.IsDropDownOpen = false;Pump();
            var source = W.PresentationSource.FromVisual(choice.Box)!;
            var key = new WI.KeyEventArgs(WI.Keyboard.PrimaryDevice, source, 0, WI.Key.Down)
                { RoutedEvent = W.UIElement.KeyDownEvent };
            journal.WriteLine(JsonSerializer.Serialize(new { name="keyboard-before",choice.Picked,choice.Box.IsDropDownOpen,
                choice.Box.IsKeyboardFocusWithin,modifiers=WI.Keyboard.Modifiers.ToString(),focus=WI.Keyboard.FocusedElement?.GetType().Name }));
            Record("keyboard-down-starts-without-modifiers", WI.Keyboard.Modifiers == WI.ModifierKeys.None);
            choice.Box.RaiseEvent(key);
            long immediate = choice.Picked;
            Record("keyboard-down-selects-next-value", choice.Picked == 2, new { choice.Picked,immediate,key.Handled,
                choice.Box.IsKeyboardFocusWithin,modifiers=WI.Keyboard.Modifiers.ToString(),focus=WI.Keyboard.FocusedElement?.GetType().Name });
            long before = choice.Picked;
            var wheel = new WI.MouseWheelEventArgs(WI.Mouse.PrimaryDevice, 0, 120) { RoutedEvent = W.UIElement.PreviewMouseWheelEvent };
            choice.Box.RaiseEvent(wheel);
            Record("closed-wheel-preserves-selection", choice.Picked == before && wheel.Handled);
            editable.IsEnabled = false;host.UpdateLayout();
            Record("disabled-long-list-dims", !input.IsEnabled && Math.Abs(editable.Opacity - .45) < .001);
            Capture("disabled");
            Console.WriteLine($"Native WPF choices: {cases} cases passed; output={output}");
        }
        finally { editable.IsDropDownOpen = false;choice.Box.IsDropDownOpen = false;decisions.IsDropDownOpen = false;host.Close(); }
    }
}

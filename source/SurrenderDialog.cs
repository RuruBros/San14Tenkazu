using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace San14Tenkazu;

internal sealed class SurrenderDialog : W.Window
{
    internal WC.ComboBox TargetChoice { get; } = EditorCard.Choice(double.NaN);
    internal Dictionary<int, WC.CheckBox> SourceChoices { get; } = [];
    internal PlateButton AcceptButton { get; } = new(L.Surrender.BatchButton, primary: true, icon: Icons.Force);
    internal PlateButton SelectAllButton { get; } = new(L.Surrender.SelectAll);
    internal PlateButton ClearButton { get; } = new(L.Surrender.ClearSelection);
    internal int Target => TargetChoice.SelectedValue is int force ? force : 0;
    internal int[] Sources => SourceChoices.Where(pair => pair.Value.IsEnabled && pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();

    internal SurrenderDialog(GameSnapshot data)
    {
        Title = L.Surrender.BatchButton;
        WindowStartupLocation = W.WindowStartupLocation.CenterOwner;
        ResizeMode = W.ResizeMode.NoResize; ShowInTaskbar = false;
        SizeToContent = W.SizeToContent.WidthAndHeight;
        Background = CardSkin.Parchment; FontFamily = CardSkin.Font; FontSize = CardSkin.FontSize; Foreground = CardSkin.Ink;
        UseLayoutRounding = true;
        WM.TextOptions.SetTextFormattingMode(this, WM.TextFormattingMode.Display);
        var choices = BaseGrants.Takers(data).Where(c => c.Force >= 1).ToList();
        TargetChoice.HorizontalAlignment = W.HorizontalAlignment.Stretch;
        TargetChoice.SelectedValuePath = nameof(WC.ComboBoxItem.Tag);
        foreach (var choice in choices)
            TargetChoice.Items.Add(new WC.ComboBoxItem { Tag = choice.Force,
                Content = L.Auto.Choice(data.ForceName(choice.Force), choice.Cities) +
                    (choice.Force == data.PlayerForce ? " " + L.Auto.CurrentPlayer : "") });
        var count = new WC.TextBlock { Margin = new W.Thickness(0, 8, 0, 8), Foreground = CardSkin.InkMuted };
        void UpdateCount()
        {
            count.Text = L.Surrender.SelectedCount(Sources.Length);
            AcceptButton.IsEnabled = Target > 0 && Sources.Length > 0;
        }
        var sources = new WC.StackPanel();
        foreach (var choice in choices.Where(c => c.Force != data.PlayerForce))
        {
            var check = new WC.CheckBox { Content = L.Surrender.SourceChoice(data.ForceName(choice.Force),
                data.OfficerName(choice.Ruler), choice.Cities), Margin = new W.Thickness(0, 5, 0, 5),
                IsChecked = false, Foreground = CardSkin.Ink };
            SourceChoices.Add(choice.Force, check); sources.Children.Add(check);
            check.Checked += (_, _) => UpdateCount(); check.Unchecked += (_, _) => UpdateCount();
        }
        TargetChoice.SelectionChanged += (_, _) =>
        {
            foreach (var (force, check) in SourceChoices)
            {
                check.IsEnabled = force != Target;
                check.IsChecked = false;
            }
            UpdateCount();
        };
        TargetChoice.SelectedValue = choices.Any(c => c.Force == data.PlayerForce) ? data.PlayerForce
            : choices.FirstOrDefault().Force;
        SelectAllButton.Click += () => { foreach (var check in SourceChoices.Values.Where(c => c.IsEnabled)) check.IsChecked = true; };
        ClearButton.Click += () => { foreach (var check in SourceChoices.Values) check.IsChecked = false; };
        void Accept() { if (AcceptButton.IsEnabled) DialogResult = true; }
        AcceptButton.Click += Accept;
        var cancel = new PlateButton(L.Window.Cancel) { Margin = new W.Thickness(8, 0, 0, 0) };
        cancel.Click += () => DialogResult = false;
        PreviewKeyDown += (_, e) =>
        {
            if (TargetChoice.IsDropDownOpen) return;
            if (e.Key == WI.Key.Escape) { DialogResult = false; e.Handled = true; }
            else if (e.Key == WI.Key.Enter) { Accept(); e.Handled = true; }
        };
        ClearButton.Margin = new W.Thickness(8, 0, 0, 0);
        Content = new WC.Border { Background = CardSkin.Parchment, Padding = new W.Thickness(16),
            Child = new WC.StackPanel { Width = 480, Children =
        {
            new WC.TextBlock { Text = L.Surrender.TargetLabel, Margin = new W.Thickness(0, 0, 0, 6) }, TargetChoice,
            new WC.TextBlock { Text = L.Surrender.SourcesLabel, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 14, 0, 8) },
            new WC.StackPanel { Orientation = WC.Orientation.Horizontal, Children = { SelectAllButton, ClearButton } },
            new WC.ScrollViewer { Content = sources, MaxHeight = 300, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled, Margin = new W.Thickness(0, 8, 0, 0) }, count,
            new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = W.HorizontalAlignment.Right,
                Children = { AcceptButton, cancel } }
        }}};
        Loaded += (_, _) => TargetChoice.Focus();
        UpdateCount();
    }
}

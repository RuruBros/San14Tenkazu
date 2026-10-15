using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace San14Tenkazu;

// Collect one value; the window previews and writes the resulting batch through its regular fill path.
internal sealed class ColumnEditDialog : W.Window
{
    internal NumberField Number { get; }
    internal PlateButton ApplyButton { get; } = new(L.Card.Apply, primary: true, icon: Icons.Apply);
    internal long Value { get; private set; }

    internal ColumnEditDialog(FieldDef field, int count, int skipped, bool deferred = false)
    {
        Title = L.Lists.BulkColumn(field.Label);
        WindowStartupLocation = W.WindowStartupLocation.CenterOwner;
        ResizeMode = W.ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = W.SizeToContent.WidthAndHeight;
        Background = CardSkin.Parchment;
        FontFamily = CardSkin.Font; FontSize = CardSkin.FontSize; Foreground = CardSkin.Ink;
        UseLayoutRounding = true;
        WM.TextOptions.SetTextFormattingMode(this, WM.TextFormattingMode.Display);
        Number = new NumberField(field, 150);
        Number.Show(field.Min);
        Number.Box.Clear();
        Number.Box.HorizontalAlignment = W.HorizontalAlignment.Left;
        System.Windows.Automation.AutomationProperties.SetName(Number.Box, field.Label);
        var error = new WC.TextBlock { Foreground = CardSkin.Poor, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 4, 0, 12) };
        void Validate()
        {
            ApplyButton.IsEnabled = Number.IsValid;
            error.Text = Number.IsValid || Number.Box.Text.Length == 0 ? "" : field.RangeMessage;
        }
        Number.Edited += Validate;
        var cancel = new PlateButton(L.Window.Cancel) { Margin = new W.Thickness(8, 0, 0, 0) };
        ApplyButton.Click += Accept;
        cancel.Click += () => DialogResult = false;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == WI.Key.Enter) { Accept(); e.Handled = true; }
            else if (e.Key == WI.Key.Escape) { DialogResult = false; e.Handled = true; }
        };
        Content = new WC.Border { Background = CardSkin.Parchment, Padding = new W.Thickness(16), Child = new WC.StackPanel { Width = 400, Children =
        {
            new WC.TextBlock { Text = L.Lists.BulkScope(count), TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 0, 8) },
            new WC.TextBlock { Text = L.Lists.DraftApplyNote, TextWrapping = W.TextWrapping.Wrap,
                Visibility = deferred ? W.Visibility.Visible : W.Visibility.Collapsed, Margin = new W.Thickness(0, 0, 0, 8) },
            new WC.TextBlock { Text = L.Lists.BulkSkipped(skipped), TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted,
                Visibility = skipped > 0 ? W.Visibility.Visible : W.Visibility.Collapsed, Margin = new W.Thickness(0, 0, 0, 8) },
            new WC.TextBlock { Text = $"{field.Label}: {field.Min:N0}~{field.Max:N0}", Margin = new W.Thickness(0, 0, 0, 4) },
            Number.Box, error,
            new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = W.HorizontalAlignment.Right, Children = { ApplyButton, cancel } }
        }}};
        Loaded += (_, _) => Number.Box.Focus();
        Validate();
    }

    private void Accept()
    {
        if (!Number.IsValid || !Number.TryRead(out long value, out _)) return;
        Value = value;
        DialogResult = true;
    }
}

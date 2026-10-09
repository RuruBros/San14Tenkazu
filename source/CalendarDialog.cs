using System.Globalization;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace San14Tenkazu;

// Numeric fields use the game's 30-day calendar rather than a Gregorian DatePicker.
internal sealed class CalendarDialog : W.Window
{
    internal readonly WC.TextBox YearBox = CardSkin.NumberBox(88), MonthBox = CardSkin.NumberBox(62), DayBox = CardSkin.NumberBox(62);
    internal readonly PlateButton ApplyButton = new(L.Calendar.Apply, primary: true, icon: Icons.Apply) { MinWidth = 130 };
    internal GameCalendarDate? Target { get; private set; }
    private readonly GameCalendarDate before;
    private readonly WC.TextBlock preview = new() { TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 8, 0, 12) };

    internal CalendarDialog(GameCalendarDate current)
    {
        if (!current.IsValid) throw new ArgumentOutOfRangeException(nameof(current));
        before = current;
        Title = L.Calendar.Title;
        WindowStartupLocation = W.WindowStartupLocation.CenterOwner;
        ResizeMode = W.ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = W.SizeToContent.WidthAndHeight;
        Background = CardSkin.Parchment;
        FontFamily = CardSkin.Font; FontSize = CardSkin.FontSize; Foreground = CardSkin.Ink;
        UseLayoutRounding = true;
        WM.TextOptions.SetTextFormattingMode(this, WM.TextFormattingMode.Display);
        var fields = new WC.StackPanel { Orientation = WC.Orientation.Horizontal };
        foreach ((WC.TextBox box, string label, int value) in new[]
            { (YearBox, L.Calendar.Year, current.Year), (MonthBox, L.Calendar.Month, current.Month), (DayBox, L.Calendar.Day, current.Day) })
        {
            box.Text = value.ToString(CultureInfo.InvariantCulture);
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            box.ToolTip = L.Calendar.Range;
            fields.Children.Add(new WC.StackPanel { Margin = new W.Thickness(0, 0, 12, 0),
                Children = { new WC.TextBlock { Text = label, Margin = new W.Thickness(0, 0, 0, 4) }, box } });
            box.TextChanged += (_, _) => ValidateInput();
        }
        var cancel = new PlateButton(L.Window.Cancel) { MinWidth = 90, Margin = new W.Thickness(8, 0, 0, 0) };
        ApplyButton.Click += Accept;
        cancel.Click += () => DialogResult = false;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == WI.Key.Enter) { Accept(); e.Handled = true; }
            else if (e.Key == WI.Key.Escape) { DialogResult = false; e.Handled = true; }
        };
        Content = new WC.Border { Background = CardSkin.Parchment, Padding = new W.Thickness(16),
            Child = new WC.StackPanel { Width = 430, Children =
        {
            new WC.TextBlock { Text = L.Calendar.Note, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 0, 12) },
            fields,
            new WC.TextBlock { Text = L.Calendar.Range, TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 8, 0, 0) },
            preview,
            new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = W.HorizontalAlignment.Right, Children = { ApplyButton, cancel } }
        }}};
        Loaded += (_, _) => { YearBox.Focus(); YearBox.SelectAll(); };
        ValidateInput();
    }

    private void ValidateInput()
    {
        Target = null;
        bool Number(WC.TextBox box, out int value) => int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
        if (Number(YearBox, out int year) && Number(MonthBox, out int month) && Number(DayBox, out int day) &&
            new GameCalendarDate(year, month, day) is { IsValid: true } date)
        {
            Target = date;
            preview.Text = date == before ? L.Calendar.Unchanged : L.Calendar.Preview(before, date);
            preview.Foreground = CardSkin.Ink;
        }
        else
        {
            preview.Text = L.Calendar.Range;
            preview.Foreground = CardSkin.Poor;
        }
        ApplyButton.IsEnabled = Target is { } target && target != before;
    }

    private void Accept()
    {
        ValidateInput();
        if (ApplyButton.IsEnabled) DialogResult = true;
    }
}

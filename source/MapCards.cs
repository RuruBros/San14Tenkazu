using System.Globalization;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace San14Tenkazu;

// Shared WPF colours, input controls and action buttons use the game's parchment, ink and wood palette.
internal static partial class CardSkin
{
    internal static readonly WM.FontFamily Font = new(Theme.UiFontName);
    internal const double FontSize = 13;   // 9.75 pt
    internal static readonly WM.Brush Parchment = Brush(Theme.Parchment), Paper = Brush(Theme.Paper), Ink = Brush(Theme.Ink),
        InkMuted = Brush(Theme.InkMuted), Rule = Brush(Theme.Rule), Pending = Brush(Theme.Pending), Poor = Brush(Theme.Poor),
        Highlight = Brush(Theme.Highlight), DarkWood = Brush(Theme.DarkWood), Gold = Brush(Theme.Gold), Wood = Brush(Theme.Wood),
        InkOnDark = Brush(Theme.InkOnDark);

    internal static WM.Color Color(System.Drawing.Color c) => WM.Color.FromArgb(c.A, c.R, c.G, c.B);

    internal static WM.SolidColorBrush Brush(System.Drawing.Color c)
    {
        var brush = new WM.SolidColorBrush(Color(c));
        brush.Freeze();
        return brush;
    }

    // Text boxes on paper with a thin rule for an edge that turns gold while typing, without the system's
    // blue focus edge.
    private static readonly WC.ControlTemplate BoxTemplate = (WC.ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='TextBox'>" +
        "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>" +
        "<ScrollViewer x:Name='PART_ContentHost' Focusable='False' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden'/>" +
        "</Border></ControlTemplate>");

    internal static WC.TextBox TextBox()
    {
        var box = new WC.TextBox
        {
            Template = BoxTemplate, Background = Paper, Foreground = Ink, BorderBrush = Rule, BorderThickness = new W.Thickness(1),
            Padding = new W.Thickness(3, 2, 3, 2), VerticalContentAlignment = W.VerticalAlignment.Center
        };
        box.GotKeyboardFocus += (_, _) => box.BorderBrush = Gold;
        box.LostKeyboardFocus += (_, _) => box.BorderBrush = Rule;
        return box;
    }

    internal static WC.TextBox NumberBox(double width)
    {
        WC.TextBox box = TextBox();
        box.Width = width;
        box.TextAlignment = W.TextAlignment.Right;
        // No input method in a number box: its full-width mode (Alt+=) would type "１２３".
        WI.InputMethod.SetIsInputMethodEnabled(box, false);
        return box;
    }

    // An icon (Icons) in a colour, as a WPF image; null until the game's art is loaded (for game icons).
    private static readonly Dictionary<(string Name, int Ink), WMI.BitmapSource> tintedIcons = [];

    internal static WMI.BitmapSource? Icon(string name, System.Drawing.Color ink)
    {
        if (tintedIcons.TryGetValue((name, ink.ToArgb()), out WMI.BitmapSource? known)) return known;
        if (GameSkin.Icon(name) is not { } bitmap) return null;
        return tintedIcons[(name, ink.ToArgb())] = MapArt.Tinted(MapArt.PixelsOf(bitmap), bitmap.Width, bitmap.Height, ink);
    }

    // An icon in a card's corner, muted until the mouse is over it (the fallback text while the icon is not loaded).
    internal static W.FrameworkElement CornerMark(string icon, string fallbackText, string tip, Action run)
    {
        var picture = new WC.Image { Width = 16, Height = 16, Stretch = WM.Stretch.Uniform };
        WM.RenderOptions.SetBitmapScalingMode(picture, WM.BitmapScalingMode.HighQuality);
        var fallback = new WC.TextBlock { Text = fallbackText, FontSize = 16, Foreground = InkMuted };
        var mark = new WC.Border
        {
            Padding = new W.Thickness(6, 2, 2, 4), Cursor = WI.Cursors.Hand, Background = WM.Brushes.Transparent,
            HorizontalAlignment = W.HorizontalAlignment.Right, VerticalAlignment = W.VerticalAlignment.Top, ToolTip = tip
        };
        void Show(bool hover)
        {
            System.Drawing.Color ink = hover ? Theme.Highlight : Theme.InkMuted;
            picture.Source = Icon(icon, ink);
            fallback.Foreground = hover ? Highlight : InkMuted;
            mark.Child = picture.Source is null ? fallback : picture;
        }
        Show(false);
        GameSkin.Changed += () => mark.Dispatcher.BeginInvoke(() => Show(mark.IsMouseOver));
        mark.MouseEnter += (_, _) => Show(true);
        mark.MouseLeave += (_, _) => Show(false);
        mark.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            run();
        };
        return mark;
    }
}

// The editor's button (GameSkin's colours): a rounded plate with a gold edge, dark wood for ordinary buttons and
// red lacquer for the one main action; both brighten under the pointer. A tab's button is gold while picked.
internal sealed class PlateButton : W.FrameworkElement
{
    private static readonly WM.Typeface Face = new(CardSkin.Font, W.FontStyles.Normal, W.FontWeights.Normal, W.FontStretches.Normal),
        BoldFace = new(CardSkin.Font, W.FontStyles.Normal, W.FontWeights.Bold, W.FontStretches.Normal);
    private string text;
    private bool hover, pressed;

    internal PlateButton(string text, bool primary = false, string? icon = null, bool iconOnly = false)
    {
        this.text = text;
        Primary = primary;
        IconName = icon;
        IconOnly = iconOnly;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = WI.Cursors.Hand;
        IsEnabledChanged += (_, _) => InvalidateVisual();
        GameSkin.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            InvalidateMeasure();
            InvalidateVisual();
        });
    }

    internal bool Primary { get; }

    // An icon (Icons) before the text, in the text's colour, as on the editor's other buttons.
    internal string? IconName { get; }

    // The icon alone (the text then names the button in its tooltip).
    internal bool IconOnly { get; }

    // A tab's button: the one picked is a gold plate with dark ink, as GameSkin.DrawTab.
    internal bool Selected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            InvalidateVisual();
        }
    }
    private bool selected;

    // A count on a gold badge at the top right (0 for none): how many values an icon-only [변경 적용] will write.
    internal int Badge
    {
        get => badge;
        set
        {
            if (badge == value) return;
            badge = value;
            InvalidateVisual();
        }
    }
    private int badge;

    private const double IconSide = 16, IconGap = 6;

    private System.Drawing.Color Ink => selected ? Theme.Ink : GameSkin.TextColor(State, Primary);

    private WMI.BitmapSource? IconImage => IconName is null ? null : CardSkin.Icon(IconName, Ink);

    internal string Text
    {
        get => text;
        set
        {
            if (text == value) return;
            text = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    internal event Action? Click;

    private GameSkin.State State => !IsEnabled ? GameSkin.State.Disabled : pressed ? GameSkin.State.Pressed : hover ? GameSkin.State.Hover : GameSkin.State.Normal;

    private WM.FormattedText Format() => new(text, CultureInfo.CurrentUICulture, W.FlowDirection.LeftToRight, selected ? BoldFace : Face, CardSkin.FontSize,
        CardSkin.Brush(Ink), WM.VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // One height for every button.
    protected override W.Size MeasureOverride(W.Size availableSize)
    {
        WM.FormattedText formatted = Format();
        if (IconOnly && IconImage is not null) return new W.Size(Math.Min(40, availableSize.Width), Math.Min(32, availableSize.Height));
        double icon = IconImage is null ? 0 : IconSide + IconGap;
        return new W.Size(Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + icon + 28, Math.Max(Math.Ceiling(formatted.Height) + 12, 32));
    }

    protected override void OnRender(WM.DrawingContext dc)
    {
        if (ActualWidth <= 4 || ActualHeight <= 4) return;
        (System.Drawing.Color top, System.Drawing.Color bottom, System.Drawing.Color edge) = selected ? GameSkin.TabColors : GameSkin.PlateColors(State, Primary);
        var plate = new W.Rect(1.5, 1.5, ActualWidth - 3, ActualHeight - 3);
        var fill = new WM.LinearGradientBrush(CardSkin.Color(top), CardSkin.Color(bottom), 90);
        fill.Freeze();
        dc.DrawRoundedRectangle(fill, new WM.Pen(CardSkin.Brush(edge), 1), plate, 4, 4);
        // A faint light line under the top edge gives the plate its lacquered look.
        var shine = new WM.Pen(new WM.SolidColorBrush(WM.Color.FromArgb((byte)(top.A * 40 / 255), 255, 255, 255)), 1);
        dc.DrawLine(shine, new W.Point(plate.Left + 4, plate.Top + 1.5), new W.Point(plate.Right - 4, plate.Top + 1.5));
        WM.FormattedText formatted = Format();
        double drop = pressed ? 1 : 0;
        if (IconImage is { } only && IconOnly)
        {
            double side = IconSide + 2;
            dc.DrawImage(only, new W.Rect(Math.Round((ActualWidth - side) / 2), Math.Round((ActualHeight - side) / 2) + drop, side, side));
        }
        else if (IconImage is { } icon)
        {
            double width = IconSide + IconGap + formatted.Width, x = Math.Round((ActualWidth - width) / 2);
            dc.DrawImage(icon, new W.Rect(x, Math.Round((ActualHeight - IconSide) / 2) + drop, IconSide, IconSide));
            dc.DrawText(formatted, new W.Point(x + IconSide + IconGap, Math.Round((ActualHeight - formatted.Height) / 2) + drop));
        }
        else dc.DrawText(formatted, new W.Point(Math.Round((ActualWidth - formatted.Width) / 2), Math.Round((ActualHeight - formatted.Height) / 2) + drop));
        if (badge > 0 && IconOnly) DrawBadge(dc);
    }

    private static readonly WM.Typeface BadgeFace = new(new WM.FontFamily(Theme.NumberFontName + ", " + CardSkin.Font.Source), W.FontStyles.Normal, W.FontWeights.Bold, W.FontStretches.Normal);

    private void DrawBadge(WM.DrawingContext dc)
    {
        var count = new WM.FormattedText(badge > 99 ? "99+" : badge.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentUICulture, W.FlowDirection.LeftToRight,
            BadgeFace, 10, CardSkin.Ink, WM.VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double height = 14, width = Math.Max(height, Math.Ceiling(count.Width) + 6);
        var pill = new W.Rect(ActualWidth - width - 1, 0, width, height);
        dc.DrawRoundedRectangle(CardSkin.Brush(Theme.GoldLight), new WM.Pen(CardSkin.DarkWood, 1), pill, height / 2, height / 2);
        dc.DrawText(count, new W.Point(Math.Round(pill.X + (width - count.Width) / 2), Math.Round((height - count.Height) / 2)));
    }

    protected override void OnMouseEnter(WI.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        hover = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(WI.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        hover = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(WI.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        pressed = true;
        Focus();
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(WI.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        e.Handled = true;
        bool click = pressed && IsMouseOver;
        pressed = false;
        ReleaseMouseCapture();
        InvalidateVisual();
        if (click) Click?.Invoke();
    }

    protected override void OnLostMouseCapture(WI.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        pressed = false;
        InvalidateVisual();
    }

    protected override void OnKeyDown(WI.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (WI.Key.Enter or WI.Key.Space)) return;
        e.Handled = true;
        Click?.Invoke();
    }
}

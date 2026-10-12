using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WS = System.Windows.Shapes;
using WT = System.Windows.Threading;

namespace San14Tenkazu;

// The log at the bottom of the window, as wide as it: what the editor read and wrote, newest at the bottom.
// Its bar folds it away and opens it again, as an accordion; folded, only the bar shows.
internal sealed class LogPanel : WC.DockPanel
{
    private const double OpenHeight = 170;
    private static readonly WM.Brush HoverInk = CardSkin.Brush(Theme.GoldLight);

    private readonly WC.TextBox text = new()
    {
        IsReadOnly = true, TextWrapping = W.TextWrapping.Wrap, AcceptsReturn = true, BorderThickness = new W.Thickness(0),
        Background = CardSkin.DarkWood, Foreground = CardSkin.InkOnDark, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto
    };
    private readonly WC.Border body;
    private readonly WC.DockPanel heading=new(){LastChildFill=true};
    // Points up while folded (the log opens upward), down while open.
    private readonly WS.Path chevron = new()
    {
        Data = WM.Geometry.Parse("M 0,5 L 5,0 L 10,5"), Width = 10, Height = 5, StrokeThickness = 1.6,
        StrokeStartLineCap = WM.PenLineCap.Round, StrokeEndLineCap = WM.PenLineCap.Round, StrokeLineJoin = WM.PenLineJoin.Round,
        RenderTransformOrigin = new W.Point(0.5, 0.5), VerticalAlignment = W.VerticalAlignment.Center, Margin = new W.Thickness(0, 1, 8, 0)
    };
    private readonly WC.TextBlock title = new() { Text = $"{L.Window.Log} (v{Program.AppVersion})", VerticalAlignment = W.VerticalAlignment.Center };

    internal LogPanel()
    {
        // The bar is dark wood under a gold rule, as the legend above it; the whole bar is the fold.
        var bar = new WC.Border
        {
            Background = CardSkin.DarkWood, BorderBrush = CardSkin.Gold, BorderThickness = new W.Thickness(0, 1, 0, 0),
            Padding = new W.Thickness(14, 3, 14, 4), Cursor = WI.Cursors.Hand,
            Child = heading
        };
        heading.Children.Add(new WC.StackPanel { Orientation = WC.Orientation.Horizontal, Children = { chevron, title } });
        bar.MouseEnter += (_, _) => Paint(true);
        bar.MouseLeave += (_, _) => Paint(false);
        bar.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Open = !Open;
        };
        // The log shares the toolbar's dark wood and light text, inside a gold line.
        body = new WC.Border
        {
            Background = CardSkin.DarkWood, BorderBrush = CardSkin.Gold, BorderThickness = new W.Thickness(0, 1, 0, 0),
            Height = OpenHeight, Padding = new W.Thickness(14, 4, 4, 4), Child = text, Visibility = W.Visibility.Collapsed
        };
        SetDock(bar, WC.Dock.Top);
        Children.Add(bar);
        Children.Add(body);
        Paint(false);
    }
#if LOCAL_DEVELOPER
    internal void SetDeveloperButton(PlateButton button)
    {
        SetDock(button,WC.Dock.Right);button.VerticalAlignment=W.VerticalAlignment.Center;
        heading.Children.Insert(0,button);
    }
#endif

    internal bool Open
    {
        get => body.Visibility == W.Visibility.Visible;
        set
        {
            body.Visibility = value ? W.Visibility.Visible : W.Visibility.Collapsed;
            chevron.RenderTransform = new WM.RotateTransform(value ? 180 : 0);
            // Once laid out, at the newest line.
            if (value) Dispatcher.BeginInvoke(text.ScrollToEnd, WT.DispatcherPriority.Loaded);
        }
    }

    private void Paint(bool hover)
    {
        WM.Brush ink = hover ? HoverInk : CardSkin.InkOnDark;
        chevron.Stroke = ink;
        title.Foreground = ink;
    }

    internal void Append(string line)
    {
        text.AppendText(line);
        if (Open) text.ScrollToEnd();
    }
}

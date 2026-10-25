using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

// Overlay the existing row so progress never inserts another row into the layout.
internal sealed class OperationProgressSlot : WC.Grid
{
    private W.UIElement? restingContent;

    internal void SetRestingContent(W.UIElement content)
    {
        restingContent = content;
        Children.Add(content);
    }

    internal void Show(W.UIElement progress)
    {
        if (restingContent is not null) restingContent.Visibility = W.Visibility.Hidden;
        Children.Add(progress);
    }

    internal void Clear(W.UIElement progress)
    {
        Children.Remove(progress);
        if (restingContent is not null) restingContent.Visibility = W.Visibility.Visible;
    }
}

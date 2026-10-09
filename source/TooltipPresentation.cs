using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

// Keep ToolTip values as strings: selected choices and status updates read them back.
// The popup's string template wraps both localized help and runtime diagnostics.
internal static class TooltipPresentation
{
    internal const double MaximumWidth = 360;

    internal static void Install(W.ResourceDictionary resources)
    {
        var style = new W.Style(typeof(WC.ToolTip));
        style.Setters.Add(new W.Setter(W.FrameworkElement.MaxWidthProperty, MaximumWidth));
        style.Setters.Add(new W.Setter(WC.Control.FontFamilyProperty, CardSkin.Font));
        style.Setters.Add(new W.Setter(WC.Control.FontSizeProperty, CardSkin.FontSize));
        style.Setters.Add(new W.Setter(WC.Control.BackgroundProperty, CardSkin.Paper));
        style.Setters.Add(new W.Setter(WC.Control.ForegroundProperty, CardSkin.Ink));
        style.Setters.Add(new W.Setter(WC.Control.BorderBrushProperty, CardSkin.Rule));
        style.Setters.Add(new W.Setter(WC.Control.PaddingProperty, new W.Thickness(8, 5, 8, 5)));
        style.Resources[new W.DataTemplateKey(typeof(string))] = (W.DataTemplate)W.Markup.XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<TextBlock Text='{Binding}' TextWrapping='Wrap'/></DataTemplate>");
        resources[typeof(WC.ToolTip)] = style;
    }
}

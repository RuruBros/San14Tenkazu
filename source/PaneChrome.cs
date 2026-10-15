using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;
using WM = System.Windows.Media;

namespace San14Tenkazu;

// Docked pages share spacing and typography without the former floating-card frame.
internal class PanePage : WC.Border
{
    protected readonly WC.Border Inner = new() { Background = CardSkin.Paper };

    protected PanePage()
    {
        Child = Inner;
        Background = CardSkin.Paper;
        HorizontalAlignment = W.HorizontalAlignment.Stretch;
        VerticalAlignment = W.VerticalAlignment.Stretch;
        Visibility = W.Visibility.Collapsed;
        UseLayoutRounding = true;
        WD.TextElement.SetFontFamily(this, CardSkin.Font);
        WD.TextElement.SetFontSize(this, CardSkin.FontSize);
        WD.TextElement.SetForeground(this, CardSkin.Ink);
        WM.TextOptions.SetTextFormattingMode(this, WM.TextFormattingMode.Display);
    }

    internal bool Shown => Visibility == W.Visibility.Visible;
}

internal static class PaneChrome
{
    internal static WC.Border Toolbar(W.UIElement child) => new()
    {
        Background = CardSkin.Parchment, BorderBrush = CardSkin.Rule, BorderThickness = new W.Thickness(0, 0, 0, 1),
        Padding = new W.Thickness(12, 8, 12, 8), Child = child
    };

    internal static WC.Border Footer(W.UIElement child) => new()
    {
        Background = CardSkin.Parchment, BorderBrush = CardSkin.Rule, BorderThickness = new W.Thickness(0, 1, 0, 0),
        Padding = new W.Thickness(12, 8, 12, 8), Child = child
    };
}

// Task tabs use an underline, categories use paper outlines, and third-level lists use capsules.
internal sealed class PaneTab : WC.Button
{
    private readonly bool primary;
    private readonly bool capsule;
    private bool selected;
    private static readonly WM.Brush CapsuleSelection = CardSkin.Brush(Theme.CapsuleSelection),
        CapsuleHover = CardSkin.Brush(Theme.PaperShade);
    private static readonly WC.ControlTemplate TabTemplate = (WC.ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<Border x:Name='edge' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' CornerRadius='3' Background='{TemplateBinding Background}' " +
        "BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}'>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='edge' Property='BorderThickness' Value='2'/></Trigger>" +
        "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='edge' Property='Opacity' Value='0.45'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>");

    private static readonly WC.ControlTemplate PrimaryTemplate = (WC.ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<Border x:Name='edge' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='{TemplateBinding Background}' " +
        "BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}'>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsEnabled' Value='False'><Setter TargetName='edge' Property='Opacity' Value='0.45'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>");

    private static readonly WC.ControlTemplate CapsuleTemplate = (WC.ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<Border x:Name='edge' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' CornerRadius='13' Background='{TemplateBinding Background}' " +
        "BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}'>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsPressed' Value='True'><Setter TargetName='edge' Property='Opacity' Value='0.8'/></Trigger>" +
        "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='edge' Property='Opacity' Value='0.45'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>");

    internal PaneTab(string text, bool primary = false, bool capsule = false)
    {
        this.primary = primary;
        this.capsule = capsule;
        Content = text;
        FontFamily = CardSkin.Font;
        FontSize = CardSkin.FontSize;
        Template = primary ? PrimaryTemplate : capsule ? CapsuleTemplate : TabTemplate;
        Padding = primary ? new W.Thickness(8, 6, 8, 5) : capsule ? new W.Thickness(12, 2, 12, 2) : new W.Thickness(10, 4, 10, 4);
        Margin = primary ? new W.Thickness(0, 0, 4, 0) : capsule ? new W.Thickness(0, 0, 6, 2) : new W.Thickness(0, 0, 4, 4);
        MinHeight = capsule ? 26 : 28;
        BorderThickness = primary ? new W.Thickness(0, 0, 0, 3) : new W.Thickness(1);
        Cursor = System.Windows.Input.Cursors.Hand;
        Selected = false;
        MouseEnter += (_, _) => { if (capsule) ApplySelection(); else BorderBrush = CardSkin.Highlight; };
        MouseLeave += (_, _) => ApplySelection();
        GotKeyboardFocus += (_, _) => { if (capsule) ApplySelection(); else BorderBrush = CardSkin.Highlight; };
        LostKeyboardFocus += (_, _) => ApplySelection();
    }

    internal bool Selected
    {
        get => selected;
        set
        {
            selected = value;
            ApplySelection();
        }
    }

    private void ApplySelection()
    {
        if (capsule)
        {
            Background = selected ? CapsuleSelection : IsMouseOver ? CapsuleHover : WM.Brushes.Transparent;
            BorderBrush = IsKeyboardFocused ? CardSkin.Gold : WM.Brushes.Transparent;
            Foreground = selected || IsMouseOver ? CardSkin.Ink : CardSkin.InkMuted;
            FontWeight = selected ? W.FontWeights.SemiBold : W.FontWeights.Normal;
            return;
        }
        Background = primary ? CardSkin.Parchment : selected ? CardSkin.Parchment : CardSkin.Paper;
        BorderBrush = selected ? CardSkin.Gold : primary ? WM.Brushes.Transparent : CardSkin.Rule;
        Foreground = selected ? CardSkin.Ink : CardSkin.InkMuted;
        FontWeight = selected ? W.FontWeights.SemiBold : W.FontWeights.Normal;
    }
}

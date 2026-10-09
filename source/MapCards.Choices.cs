using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

internal static partial class CardSkin
{
    // Both editable name pickers and fixed choices use the same paper, ink and focus edge as text boxes.
    // Keep the standard WPF parts so keyboard selection, text search and virtualized lists still work.
    // Palette fields live in another partial file. Defer these factories until CardSkin's static
    // initialization has finished so setters and focus triggers never capture null brushes.
    private static readonly Lazy<WC.ControlTemplate> ChoiceTemplate = new(CreateChoiceTemplate);
    private static readonly Lazy<W.Style> ChoiceItemStyle = new(CreateChoiceItemStyle);

    internal static WC.ComboBox ChoiceBox() => new()
    {
        Template = ChoiceTemplate.Value, ItemContainerStyle = ChoiceItemStyle.Value,
        Background = Paper, Foreground = Ink, BorderBrush = Rule, BorderThickness = new W.Thickness(1),
        FontFamily = Font, FontSize = FontSize, Padding = new W.Thickness(4, 2, 4, 2), MinHeight = 26,
        VerticalContentAlignment = W.VerticalAlignment.Center
    };

    private static WC.ControlTemplate CreateChoiceTemplate()
    {
        var template = (WC.ControlTemplate)W.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBox'>" +
            "<Grid><Border x:Name='edge' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'/>" +
            "<ToggleButton x:Name='toggle' Focusable='False' ClickMode='Press' Foreground='{TemplateBinding Foreground}' IsChecked='{Binding IsDropDownOpen,Mode=TwoWay,RelativeSource={RelativeSource TemplatedParent}}'>" +
            "<ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Grid Background='Transparent'><Path Data='M 0 0 L 4 4 L 8 0' Stroke='{TemplateBinding Foreground}' StrokeThickness='1' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,6,0'/></Grid></ControlTemplate></ToggleButton.Template></ToggleButton>" +
            "<ContentPresenter x:Name='selection' Margin='4,2,22,2' HorizontalAlignment='Left' VerticalAlignment='Center' IsHitTestVisible='False' Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' ContentTemplateSelector='{TemplateBinding ItemTemplateSelector}'/>" +
            "<TextBox x:Name='PART_EditableTextBox' Visibility='Hidden' Margin='4,2,22,2' Background='Transparent' Foreground='{TemplateBinding Foreground}' VerticalContentAlignment='Center' IsReadOnly='{TemplateBinding IsReadOnly}'>" +
            "<TextBox.Template><ControlTemplate TargetType='TextBox'><ScrollViewer x:Name='PART_ContentHost' Focusable='False' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden'/></ControlTemplate></TextBox.Template></TextBox>" +
            "<Popup x:Name='PART_Popup' Placement='Bottom' AllowsTransparency='True' Focusable='False' IsOpen='{TemplateBinding IsDropDownOpen}'>" +
            "<Border x:Name='dropDown' MinWidth='{TemplateBinding ActualWidth}' MaxHeight='{TemplateBinding MaxDropDownHeight}' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'>" +
            "<ScrollViewer CanContentScroll='True' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Auto'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer>" +
            "</Border></Popup></Grid><ControlTemplate.Triggers>" +
            "<Trigger Property='IsEditable' Value='True'><Setter TargetName='selection' Property='Visibility' Value='Hidden'/><Setter TargetName='PART_EditableTextBox' Property='Visibility' Value='Visible'/><Setter Property='IsTabStop' Value='False'/></Trigger>" +
            "<Trigger Property='HasItems' Value='False'><Setter TargetName='dropDown' Property='MinHeight' Value='26'/></Trigger>" +
            "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>");
        foreach (W.DependencyProperty property in new[] { W.UIElement.IsMouseOverProperty, W.UIElement.IsKeyboardFocusWithinProperty, WC.ComboBox.IsDropDownOpenProperty })
            template.Triggers.Add(new W.Trigger { Property = property, Value = true,
                Setters = { new W.Setter(WC.Border.BorderBrushProperty, Gold, "edge") } });
        return template;
    }

    private static W.Style CreateChoiceItemStyle()
    {
        var style = new W.Style(typeof(WC.ComboBoxItem));
        style.Setters.Add(new W.Setter(WC.Control.PaddingProperty, new W.Thickness(5, 3, 5, 3)));
        style.Setters.Add(new W.Setter(WC.Control.ForegroundProperty, Ink));
        style.Setters.Add(new W.Setter(WC.Control.BackgroundProperty, Paper));
        style.Setters.Add(new W.Setter(WC.Control.TemplateProperty, (WC.ControlTemplate)W.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ComboBoxItem'>" +
            "<Border Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border></ControlTemplate>")));
        foreach (W.DependencyProperty property in new[] { WC.ComboBoxItem.IsHighlightedProperty, WC.Primitives.Selector.IsSelectedProperty })
            style.Triggers.Add(new W.Trigger { Property = property, Value = true,
                Setters = { new W.Setter(WC.Control.BackgroundProperty, Brush(Theme.Selection)) } });
        style.Triggers.Add(new W.Trigger { Property = W.UIElement.IsEnabledProperty, Value = false,
            Setters = { new W.Setter(WC.Control.ForegroundProperty, InkMuted) } });
        return style;
    }
}

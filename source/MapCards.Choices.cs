using W = System.Windows;
using System.Collections.Specialized;
using WC = System.Windows.Controls;
using WD = System.Windows.Data;
using WI = System.Windows.Input;
using WT = System.Windows.Threading;

namespace San14Tenkazu;

internal static partial class CardSkin
{
    // Every choice uses the same paper, ink and focus edge as text boxes.
    // Keep the standard WPF parts so keyboard selection and text search still work on a closed box.
    // Palette fields live in another partial file. Defer these factories until CardSkin's static
    // initialization has finished so setters and focus triggers never capture null brushes.
    private static readonly Lazy<WC.ControlTemplate> ChoiceTemplate = new(CreateChoiceTemplate);
    private static readonly Lazy<W.Style> ChoiceItemStyle = new(CreateChoiceItemStyle);
    private static readonly Lazy<W.Style> ChoiceRowStyle = new(CreateChoiceRowStyle);

    internal static WC.ComboBox ChoiceBox()
    {
        var box = new WC.ComboBox
        {
            Template = ChoiceTemplate.Value, ItemContainerStyle = ChoiceItemStyle.Value,
            Background = Paper, Foreground = Ink, BorderBrush = Rule, BorderThickness = new W.Thickness(1),
            FontFamily = Font, FontSize = FontSize, Padding = new W.Thickness(4, 2, 4, 2), MinHeight = 26,
            VerticalContentAlignment = W.VerticalAlignment.Center
        };
        ChoiceSearch.Attach(box);
        return box;
    }

    // A card's Esc closes the card from its PreviewKeyDown, before the list that is open under the keyboard sees it.
    internal static bool InOpenChoice(object? source) => source is W.DependencyObject element
        && (element as WC.ComboBox ?? WC.ItemsControl.ItemsControlFromItemContainer(element) ?? (element as W.FrameworkElement)?.TemplatedParent) is WC.ComboBox { IsDropDownOpen: true };

    private static WC.ControlTemplate CreateChoiceTemplate()
    {
        var template = (WC.ControlTemplate)W.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ComboBox'>" +
            "<Grid><Border x:Name='edge' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'/>" +
            "<ToggleButton x:Name='toggle' Focusable='False' ClickMode='Press' Foreground='{TemplateBinding Foreground}' IsChecked='{Binding IsDropDownOpen,Mode=TwoWay,RelativeSource={RelativeSource TemplatedParent}}'>" +
            "<ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Grid Background='Transparent'><Path Data='M 0 0 L 4 4 L 8 0' Stroke='{TemplateBinding Foreground}' StrokeThickness='1' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,6,0'/></Grid></ControlTemplate></ToggleButton.Template></ToggleButton>" +
            "<ContentPresenter x:Name='selection' Margin='4,2,22,2' HorizontalAlignment='Left' VerticalAlignment='Center' IsHitTestVisible='False' Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' ContentTemplateSelector='{TemplateBinding ItemTemplateSelector}'/>" +
            // The search box stays in the window, not the popup: a popup never holds the Win32 focus the Korean IME needs.
            "<TextBox x:Name='search' Visibility='Collapsed' Margin='4,2,22,2' Background='Transparent' Foreground='{TemplateBinding Foreground}' VerticalContentAlignment='Center'>" +
            "<TextBox.Template><ControlTemplate TargetType='TextBox'><ScrollViewer x:Name='PART_ContentHost' Focusable='False' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden'/></ControlTemplate></TextBox.Template></TextBox>" +
            "<TextBlock x:Name='searchHint' Visibility='Collapsed' IsHitTestVisible='False' Margin='6,2,22,2' VerticalAlignment='Center'/>" +
            "<Popup x:Name='PART_Popup' Placement='Bottom' AllowsTransparency='True' Focusable='False' IsOpen='{TemplateBinding IsDropDownOpen}'>" +
            "<Border x:Name='dropDown' MinWidth='{TemplateBinding ActualWidth}' MaxHeight='{TemplateBinding MaxDropDownHeight}' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'>" +
            "<Grid><ScrollViewer x:Name='itemsScroll' CanContentScroll='True' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Auto'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer>" +
            "<ListBox x:Name='searchRows' Visibility='Collapsed' Focusable='False' DisplayMemberPath='Label' BorderThickness='0' Background='Transparent' Foreground='{TemplateBinding Foreground}' VirtualizingPanel.VirtualizationMode='Recycling'/>" +
            "<TextBlock x:Name='searchEmpty' Visibility='Collapsed' Margin='5,3,5,3'/></Grid>" +
            "</Border></Popup></Grid><ControlTemplate.Triggers>" +
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

    // The rows of a searched list look like the items of any other; the keyboard stays in the search box.
    private static W.Style CreateChoiceRowStyle()
    {
        var style = new W.Style(typeof(WC.ListBoxItem));
        style.Setters.Add(new W.Setter(W.UIElement.FocusableProperty, false));
        style.Setters.Add(new W.Setter(W.UIElement.IsEnabledProperty, new WD.Binding(nameof(ChoiceRow.Enabled))));
        style.Setters.Add(new W.Setter(W.FrameworkElement.ToolTipProperty, new WD.Binding(nameof(ChoiceRow.Tip))));
        style.Setters.Add(new W.Setter(WC.ToolTipService.ShowOnDisabledProperty, true));
        style.Setters.Add(new W.Setter(WC.Control.PaddingProperty, new W.Thickness(5, 3, 5, 3)));
        style.Setters.Add(new W.Setter(WC.Control.ForegroundProperty, Ink));
        style.Setters.Add(new W.Setter(WC.Control.BackgroundProperty, Paper));
        style.Setters.Add(new W.Setter(WC.Control.TemplateProperty, (WC.ControlTemplate)W.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'>" +
            "<Border Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border></ControlTemplate>")));
        foreach (W.DependencyProperty property in new[] { W.UIElement.IsMouseOverProperty, WC.ListBoxItem.IsSelectedProperty })
            style.Triggers.Add(new W.Trigger { Property = property, Value = true,
                Setters = { new W.Setter(WC.Control.BackgroundProperty, Brush(Theme.Selection)) } });
        style.Triggers.Add(new W.Trigger { Property = W.UIElement.IsEnabledProperty, Value = false,
            Setters = { new W.Setter(WC.Control.ForegroundProperty, InkMuted) } });
        return style;
    }

    internal static W.Style RowStyle => ChoiceRowStyle.Value;
}

internal sealed record ChoiceRow(int Index, string Label, bool Enabled, object? Tip);

// A long list opens with a search: the box takes the typing, and the list shows the names holding what is typed.
// The rows are a list of their own over the box's items, which stay whole: callers read their values by SelectedIndex,
// and a filter on the items would shift it (and drop a picked name that does not match).
internal sealed class ChoiceSearch
{
    internal const int LongList = 10;
    private const int PageRows = 10;

    private readonly WC.ComboBox box;
    private WC.TextBox input = null!;
    private WC.TextBlock hint = null!, empty = null!;
    private W.UIElement selection = null!, items = null!;
    private WC.ListBox rows = null!;
    private List<ChoiceRow> shown = [];
    private ChoiceRow? pressed;
    private bool searching;

    private ChoiceSearch(WC.ComboBox box) => this.box = box;

    internal static void Attach(WC.ComboBox box)
    {
        var search = new ChoiceSearch(box);
        box.DropDownOpened += (_, _) => search.Opened();
        box.DropDownClosed += (_, _) => search.Closed();
    }

    private void Parts()
    {
        if (input is not null) return;
        box.ApplyTemplate();
        T Part<T>(string name) => (T)box.Template.FindName(name, box);
        input = Part<WC.TextBox>("search");
        hint = Part<WC.TextBlock>("searchHint");
        empty = Part<WC.TextBlock>("searchEmpty");
        selection = Part<W.UIElement>("selection");
        items = Part<W.UIElement>("itemsScroll");
        rows = Part<WC.ListBox>("searchRows");
        hint.Text = L.Card.ChoiceSearch;
        hint.Foreground = empty.Foreground = CardSkin.InkMuted;
        empty.Text = L.Card.ChoiceNoMatch;
        rows.ItemContainerStyle = CardSkin.RowStyle;
        input.TextChanged += (_, _) =>
        {
            if (!searching) return;
            hint.Visibility = input.Text.Length == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
            rows.MinWidth = Math.Max(rows.MinWidth, rows.ActualWidth);
            Fill();
        };
        input.PreviewKeyDown += Key;
        // What the box would do with these is for its own list, which is not showing.
        input.KeyDown += (_, e) => { if (e.Key is WI.Key.Left or WI.Key.Right or WI.Key.Home or WI.Key.End) e.Handled = true; };
        // A press is held here so the list does not take the mouse from the open box.
        rows.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = RowAt(e.OriginalSource);
            e.Handled = pressed is not null;
        };
        rows.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (pressed is not null && pressed == RowAt(e.OriginalSource)) Pick(pressed);
            pressed = null;
        };
    }

    private ChoiceRow? RowAt(object source) => source is W.DependencyObject element
        && WC.ItemsControl.ContainerFromElement(rows, element) is WC.ListBoxItem { DataContext: ChoiceRow row } ? row : null;

    private void Opened()
    {
        Parts();
        if (box.Items.Count <= LongList) return;
        searching = true;
        ((INotifyCollectionChanged)box.Items).CollectionChanged += ItemsChanged;
        rows.MinWidth = 0;
        Show(true);
        Fill();
        // After the box has put the keyboard on its picked item, and the list has been laid out.
        box.Dispatcher.BeginInvoke(() =>
        {
            if (!searching) return;
            input.Focus();
            WI.Keyboard.Focus(input);
            if (rows.SelectedItem is not null) rows.ScrollIntoView(rows.SelectedItem);
        }, WT.DispatcherPriority.Input);
    }

    private void Closed()
    {
        if (!searching) return;
        searching = false;
        ((INotifyCollectionChanged)box.Items).CollectionChanged -= ItemsChanged;
        input.Text = "";
        rows.ItemsSource = shown = [];
        Show(false);
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Fill();

    private void Show(bool on)
    {
        W.Visibility search = on ? W.Visibility.Visible : W.Visibility.Collapsed;
        input.Visibility = hint.Visibility = rows.Visibility = search;
        selection.Visibility = items.Visibility = on ? W.Visibility.Collapsed : W.Visibility.Visible;
        empty.Visibility = W.Visibility.Collapsed;
    }

    // The picked name is marked until something is typed; then the first name that can be picked.
    private void Fill()
    {
        string word = input.Text.Trim();
        shown = Enumerable.Range(0, box.Items.Count).Select(Row).Where(row => row.Label.Contains(word, StringComparison.OrdinalIgnoreCase)).ToList();
        rows.ItemsSource = shown;
        rows.SelectedItem = (word.Length == 0 ? shown.Find(row => row.Index == box.SelectedIndex) : null) ?? shown.Find(row => row.Enabled);
        if (rows.SelectedItem is not null) rows.ScrollIntoView(rows.SelectedItem);
        rows.Visibility = shown.Count > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        empty.Visibility = shown.Count > 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
    }

    private ChoiceRow Row(int index) => box.Items[index] switch
    {
        WC.ComboBoxItem item => new(index, item.Content?.ToString() ?? "", item.IsEnabled, item.ToolTip),
        object item when box.DisplayMemberPath.Length > 0 => new(index, item.GetType().GetProperty(box.DisplayMemberPath)?.GetValue(item)?.ToString() ?? "", true, null),
        object item => new(index, item.ToString() ?? "", true, null),
        _ => new(index, "", true, null)
    };

    private void Key(object sender, WI.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case WI.Key.Down: Move(1); break;
            case WI.Key.Up: Move(-1); break;
            case WI.Key.PageDown: Move(PageRows); break;
            case WI.Key.PageUp: Move(-PageRows); break;
            case WI.Key.Enter: if (rows.SelectedItem is ChoiceRow row) Pick(row); break;
            case WI.Key.Escape: box.IsDropDownOpen = false; break;
            case WI.Key.Tab:
                if (rows.SelectedItem is ChoiceRow marked) Pick(marked); else box.IsDropDownOpen = false;
                box.MoveFocus(new WI.TraversalRequest(WI.Keyboard.Modifiers.HasFlag(WI.ModifierKeys.Shift) ? WI.FocusNavigationDirection.Previous : WI.FocusNavigationDirection.Next));
                break;
            default: return;
        }
        e.Handled = true;
    }

    // Moves the mark, past names that cannot be picked; it stays where it is when none lies that way.
    private void Move(int by)
    {
        int step = Math.Sign(by);
        for (int at = Math.Clamp(rows.SelectedIndex + by, 0, Math.Max(0, shown.Count - 1)); at >= 0 && at < shown.Count; at += step)
        {
            if (!shown[at].Enabled) continue;
            rows.SelectedIndex = at;
            rows.ScrollIntoView(shown[at]);
            return;
        }
    }

    // The selection first: a box that acts on its closing reads what was picked.
    private void Pick(ChoiceRow row)
    {
        if (!row.Enabled) return;
        box.SelectedIndex = row.Index;
        box.IsDropDownOpen = false;
    }
}

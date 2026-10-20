using System.Globalization;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace San14Tenkazu;

// The editor's tables, as its Windows tables used to draw them: a wood row of column names (a click sorts by that column,
// again the other way), paper rows with every other one shaded, portraits, cells of two lines (the second smaller
// and muted) and values with a bar along their foot (Theme.BarMaximum). Each row is one drawn element and only
// those in sight are made, so a thousand officers scroll freely. A number cell with a writer edits in place;
// other cells and ↑↓ through the rows raise RowPicked.
internal sealed class GameTable : WC.Border
{
    internal enum Kind { Face, Text, TwoLines, Number, Shield }

    // A column: its name, what its cells hold and its share of the width (a portrait column is as wide as the rows
    // are high). A number column's field gives its bar (when the field has a maximum) and its tooltip.
    internal sealed record Column(string Header, Kind Kind, double Weight = 1, FieldDef? Field = null);

    // A row: what it opens and its cells in the columns' order: a face number, a text ("above\nbelow" in a
    // TwoLines column), a long, or a Shield.
    internal sealed record Row(object Tag, object?[] Cells);

    // A force's name after its shield (ForceShield).
    internal sealed record Shield(string Text, System.Drawing.Color Colour);

    private const double HeaderHeight = 26, SmallSize = 11, Pad = 4;
    private static readonly WM.Typeface Typeface = new(CardSkin.Font, W.FontStyles.Normal, W.FontWeights.Normal, W.FontStretches.Normal);
    private static readonly WM.Brush Shade = CardSkin.Brush(Theme.PaperShade), Picked = CardSkin.Brush(Theme.Selection),
        Hover = CardSkin.Brush(Theme.WithAlpha(Theme.Selection, 110)), Track = CardSkin.Brush(Theme.WithAlpha(Theme.Wood, 40)),
        HeaderRule = CardSkin.Brush(Theme.WithAlpha(Theme.InkOnDark, 60));
    private static readonly Dictionary<System.Drawing.Color, WM.Brush> Brushes = [];
    private static readonly WC.ControlTemplate ListTemplate = (WC.ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ListBox'>" +
        "<ScrollViewer x:Name='scroll' Focusable='False' CanContentScroll='True' HorizontalScrollBarVisibility='Disabled' VerticalScrollBarVisibility='Auto'>" +
        "<ItemsPresenter/></ScrollViewer></ControlTemplate>");

    private readonly Column[] columns;
    private readonly HeaderLine header;
    private readonly RowList list;
    private WC.ScrollViewer? scroll;
    private List<Row> rows = [];
    private int sortColumn = -1;   // -1: the order the rows came in
    private bool descending;
    private readonly WC.Canvas inputLayer = new() { ClipToBounds = true };
    private CellInput? input;
    private bool editingEnabled;

    private sealed record CellInput(RowItem Item, Row Row, int Column, NumberField Number)
    {
        internal bool Committing { get; set; }
        internal WI.KeyEventHandler? KeyHandler { get; set; }
        internal WI.KeyboardFocusChangedEventHandler? FocusHandler { get; set; }
    }

    internal GameTable(Column[] columns, double rowHeight)
    {
        this.columns = columns;
        RowHeight = rowHeight;
        BorderBrush = CardSkin.Rule;
        BorderThickness = new W.Thickness(1);
        Background = CardSkin.Paper;
        header = new HeaderLine(this);
        list = new RowList(this) { Template = ListTemplate, Background = CardSkin.Paper, FocusVisualStyle = null };
        WC.VirtualizingPanel.SetIsVirtualizing(list, true);
        WC.VirtualizingPanel.SetVirtualizationMode(list, WC.VirtualizationMode.Recycling);
        WC.VirtualizingPanel.SetScrollUnit(list, WC.ScrollUnit.Pixel);
        var layout = new WC.DockPanel();
        WC.DockPanel.SetDock(header, WC.Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(list);
        var body = new WC.Grid();
        body.Children.Add(layout);
        body.Children.Add(inputLayer);
        Child = body;
        SizeChanged += (_, _) => PositionInput();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelInput(); };
        // The game's shields come after the table is first drawn.
        if (columns.Any(c => c.Kind == Kind.Shield))
            VisualEventSubscription.Attach(this, static changed => MapArt.Changed += changed,
                static changed => MapArt.Changed -= changed, Redraw);
    }

    internal double RowHeight { get; }

    // A click on a row, or ↑↓ (and the page keys) through them: the row's tag.
    internal event Action<object>? RowPicked;

    // Production lists and cards use Drafts and their footer. The callback also permits isolated table checks.
    internal Func<object, FieldDef, long, Task<bool>>? CellWriter { get; set; }
    internal Func<object, FieldDef, bool>? CanEditCell { get; set; }
    internal Func<object, EditorTarget>? DraftTarget { get; set; }
    private TableDrafts? drafts;
    internal TableDrafts? Drafts
    {
        get => drafts;
        set
        {
            CancelInput();
            if (drafts is not null) drafts.Changed -= DraftChanged;
            drafts = value;
            if (drafts is not null) drafts.Changed += DraftChanged;
            Redraw();
        }
    }

    private NumberField? Draft(Row row, int column) => columns[column].Field is { } field && DraftTarget is { } target
        ? drafts?.Find(target(row.Tag), field) : null;

    private void DraftChanged()
    {
        if (input is { } edit && Draft(edit.Row, edit.Column) != edit.Number) CancelInput();
        Redraw();
    }
    // One batch for the eligible rows in this table, including rows outside the scroll viewport.
    internal Func<IReadOnlyList<object>, FieldDef, long, Task<bool>>? ColumnWriter
    {
        get => columnWriter;
        set { columnWriter = value; header.UpdateActions(); }
    }
    private Func<IReadOnlyList<object>, FieldDef, long, Task<bool>>? columnWriter;
    internal bool EditingEnabled
    {
        get => editingEnabled;
        set
        {
            editingEnabled = value;
            if (!value) CancelInput();
            header.UpdateActions();
        }
    }

    // Portraits for the face columns, by face number (null until decoded: RefreshFaces).
    internal Func<int, WM.Brush?>? Faces { get; set; }

    // The rows in the order shown.
    internal IReadOnlyList<Row> Rows => rows;

    // New rows. The same ones (by tag) are refreshed in place, keeping the order, the scroll and the pick; others
    // make the table again in its sort, keeping the scroll and the pick where they can.
    internal void SetRows(IReadOnlyList<Row> next)
    {
        if (drafts is not null && DraftTarget is { } target)
            foreach (Row row in next)
                for (int column = 0; column < columns.Length; column++)
                    if (columns[column].Field is { } field && row.Cells[column] is long value)
                        drafts.Follow(target(row.Tag), field, value);
        if (input is { Committing: false } edit)
        {
            Row? fresh = next.FirstOrDefault(r => Equals(r.Tag, edit.Row.Tag));
            if (fresh?.Cells[edit.Column] is long number && CanEdit(fresh, edit.Column)) edit.Number.Follow(number);
            else CancelInput();
        }
        if (next.Count == rows.Count && rows.Select(r => r.Tag).ToHashSet().SetEquals(next.Select(r => r.Tag)))
        {
            // Only the rows whose values moved are drawn again: a table as tall as its rows has made them all.
            var byTag = next.ToDictionary(r => r.Tag);
            for (int i = 0; i < rows.Count; i++)
            {
                object?[] fresh = byTag[rows[i].Tag].Cells;
                if (fresh.SequenceEqual(rows[i].Cells)) continue;
                fresh.CopyTo(rows[i].Cells, 0);
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is RowItem item) item.InvalidateVisual();
            }
            header.UpdateActions();
            return;
        }
        object? picked = (list.SelectedItem as Row)?.Tag;
        CancelInput();
        double offset = scroll?.VerticalOffset ?? 0;
        rows = Sorted(next.ToList());
        list.ItemsSource = rows;
        list.SelectedItem = picked is null ? null : rows.FirstOrDefault(r => Equals(r.Tag, picked));
        scroll?.ScrollToVerticalOffset(offset);
        header.UpdateActions();
    }

    private IReadOnlyList<object> ColumnTargets(int column) => rows.Where(row => CanEdit(row, column)).Select(row => row.Tag).ToList();

    private async Task EditColumnAsync(int column)
    {
        if (columnWriter is not { } write || input is { Committing: true }) return;
        IReadOnlyList<object> targets = ColumnTargets(column);
        if (targets.Count == 0) return;
        CancelInput();
        FieldDef field = columns[column].Field!;
        var dialog = new ColumnEditDialog(field, targets.Count, rows.Count - targets.Count, deferred: drafts is not null) { Owner = W.Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && editingEnabled && columnWriter == write)
        {
            if (drafts is not null && DraftTarget is { } target)
            {
                foreach (Row row in rows.Where(r => targets.Contains(r.Tag)))
                    drafts.Get(target(row.Tag), field, (long)row.Cells[column]!).Set(dialog.Value);
            }
            else await write(targets, field, dialog.Value);
        }
    }

    // Picks the row with this tag (false when it is not listed), without raising RowPicked.
    internal bool Select(object tag)
    {
        if (rows.FirstOrDefault(r => Equals(r.Tag, tag)) is not { } row) return false;
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        return true;
    }

    internal bool Contains(object tag) => rows.Any(r => Equals(r.Tag, tag));

    internal bool FocusDraft(EditorTarget target, FieldDef field, Action showPage)
    {
        int column = Array.FindIndex(columns, c => c.Field == field);
        Row? row = rows.FirstOrDefault(r => DraftTarget?.Invoke(r.Tag) == target);
        if (row is null || column < 0 || !CanEdit(row, column)) return false;
        showPage();
        list.ScrollIntoView(row);
        UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(row) is not RowItem item) return false;
        item.BringIntoView();
        if (!BeginInput(item, column)) return false;
        CellInput? focused = input;
        // List tab changes queue search focus. Validation must focus the invalid cell after that navigation.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (input == focused) focused?.Number.Box.Focus();
        }));
        return true;
    }

    // Portraits decoded since the rows were drawn.
    internal void RefreshFaces() => Redraw();

    private void Redraw()
    {
        for (int i = 0; i < rows.Count; i++)
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is RowItem item) item.InvalidateVisual();
    }

    private void Pick(Row row) => RowPicked?.Invoke(row.Tag);

    // Names first go up, values first go down, as one compares officers; again, the other way.
    private void SortBy(int column)
    {
        CancelInput();
        descending = column == sortColumn ? !descending : columns[column].Kind == Kind.Number;
        sortColumn = column;
        object? picked = list.SelectedItem;
        rows = Sorted(rows);
        list.ItemsSource = rows;
        list.SelectedItem = picked;
        if (picked is not null) list.ScrollIntoView(picked);
        else scroll?.ScrollToTop();
        header.InvalidateVisual();
        header.InvalidateMeasure();
    }

    private List<Row> Sorted(List<Row> unsorted)
    {
        if (sortColumn < 0) return unsorted;
        int column = sortColumn;
        var comparer = Comparer<Row>.Create((a, b) => Compare(a.Cells[column], b.Cells[column]));
        return (descending ? unsorted.OrderByDescending(r => r, comparer) : unsorted.OrderBy(r => r, comparer)).ToList();
    }

    private static int Compare(object? a, object? b) => (a, b) switch
    {
        (long x, long y) => x.CompareTo(y),
        (int x, int y) => x.CompareTo(y),
        _ => string.Compare(TextOf(a), TextOf(b), StringComparison.CurrentCulture)
    };

    private static string TextOf(object? cell) => cell switch
    {
        Shield shield => shield.Text,
        null => "",
        _ => Convert.ToString(cell, CultureInfo.CurrentCulture) ?? ""
    };

    private bool CanEdit(Row row, int column) => editingEnabled && CellWriter is not null && column >= 0 && column < columns.Length
        && columns[column] is { Kind: Kind.Number, Field: { } field } && row.Cells[column] is long
        && (CanEditCell?.Invoke(row.Tag, field) ?? true);

    private int ColumnAt(RowItem item, double x)
    {
        double[] edges = Edges(item.ActualWidth, 1 / WM.VisualTreeHelper.GetDpi(item).DpiScaleX);
        for (int i = 0; i < columns.Length; i++)
            if (x >= edges[i] && x < edges[i + 1]) return i;
        return -1;
    }

    private bool BeginInput(RowItem item, int column)
    {
        if (item.Row is not { } row || !CanEdit(row, column) || input is { Committing: true }) return false;
        CancelInput();
        list.SelectedItem = row;
        var number = drafts is not null && DraftTarget is { } target
            ? drafts.Get(target(row.Tag), columns[column].Field!, (long)row.Cells[column]!)
            : new NumberField(columns[column].Field!);
        number.Box.Margin = new W.Thickness(0);
        if (drafts is null) number.Show((long)row.Cells[column]!);
        var edit = new CellInput(item, row, column, number);
        input = edit;
        inputLayer.Children.Add(number.Box);
        PositionInput();
        edit.KeyHandler = (_, e) =>
        {
            if (e.Key == WI.Key.Enter)
            {
                e.Handled = true;
                _ = CommitInputAsync(edit);
            }
            else if (e.Key == WI.Key.Escape)
            {
                e.Handled = true;
                drafts?.Revert(number);
                CancelInput();
                list.Focus();
            }
        };
        // Card tables retain their NumberField in Drafts; removing the overlay never discards that input.
        // Tables without a bound draft store retain their standalone callback behavior.
        edit.FocusHandler = (_, _) => { if (input == edit) CancelInput(); };
        number.Box.PreviewKeyDown += edit.KeyHandler;
        number.Box.LostKeyboardFocus += edit.FocusHandler;
        number.Box.Focus();
        number.Box.SelectAll();
        return true;
    }

    private void PositionInput()
    {
        if (input is not { } edit) return;
        double[] edges = Edges(edit.Item.ActualWidth, 1 / WM.VisualTreeHelper.GetDpi(edit.Item).DpiScaleX);
        double height = Math.Min(28, RowHeight - 2);
        W.Point point = edit.Item.TranslatePoint(new W.Point(edges[edit.Column] + 1, (RowHeight - height) / 2), inputLayer);
        edit.Number.Box.Width = Math.Max(1, edges[edit.Column + 1] - edges[edit.Column] - 2);
        edit.Number.Box.Height = height;
        WC.Canvas.SetLeft(edit.Number.Box, point.X);
        WC.Canvas.SetTop(edit.Number.Box, point.Y);
    }

    private void CancelInput()
    {
        if (input is { Committing: true }) return;
        if (input is { } edit)
        {
            edit.Number.Box.PreviewKeyDown -= edit.KeyHandler;
            edit.Number.Box.LostKeyboardFocus -= edit.FocusHandler;
        }
        input = null;
        inputLayer.Children.Clear();
    }

    private async Task CommitInputAsync(CellInput edit)
    {
        if (input != edit || edit.Committing || !CanEdit(edit.Row, edit.Column) || CellWriter is not { } write) return;
        if (!edit.Number.TryRead(out long value, out string error))
        {
            edit.Number.Box.ToolTip = error;
            return;
        }
        if (drafts is not null)
        {
            CancelInput();
            list.Focus();
            return;
        }
        if (value == edit.Number.Current)
        {
            CancelInput();
            list.Focus();
            return;
        }
        edit.Committing = true;
        edit.Number.Box.IsEnabled = false;
        bool written;
        try { written = await write(edit.Row.Tag, edit.Number.Field, value); }
        finally
        {
            edit.Committing = false;
            edit.Number.Box.IsEnabled = true;
        }
        if (input != edit) return;
        if (written || !IsVisible || !editingEnabled)
        {
            CancelInput();
            if (IsVisible) list.Focus();
        }
        else
        {
            edit.Number.Box.Focus();
            edit.Number.Box.SelectAll();
        }
    }

    // The columns' left edges and the last right edge across a width, on whole device pixels.
    private double[] Edges(double width, double pixel)
    {
        double faceWidth = RowHeight + 2;
        double weights = columns.Where(c => c.Kind != Kind.Face).Sum(c => c.Weight);
        double rest = Math.Max(0, width - columns.Count(c => c.Kind == Kind.Face) * faceWidth);
        var edges = new double[columns.Length + 1];
        double x = 0;
        for (int i = 0; i < columns.Length; i++)
        {
            edges[i] = Math.Round(x / pixel) * pixel;
            x += columns[i].Kind == Kind.Face ? faceWidth : rest * columns[i].Weight / weights;
        }
        edges[^1] = Math.Round(x / pixel) * pixel;
        return edges;
    }

    private static WM.Brush BrushOf(System.Drawing.Color colour)
    {
        if (!Brushes.TryGetValue(colour, out WM.Brush? brush)) Brushes[colour] = brush = CardSkin.Brush(colour);
        return brush;
    }

    private static WM.FormattedText Text(string text, double size, WM.Brush brush, double width, double pixelsPerDip, bool wrap = false)
    {
        var formatted = new WM.FormattedText(text, CultureInfo.CurrentUICulture, W.FlowDirection.LeftToRight, Typeface, size, brush, pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, width)
        };
        if (!wrap)
        {
            formatted.MaxLineCount = 1;
            formatted.Trimming = W.TextTrimming.CharacterEllipsis;
        }
        return formatted;
    }

    private void DrawRow(WM.DrawingContext dc, RowItem item)
    {
        if (item.Row is not { } row || item.ActualWidth <= 0) return;
        double width = item.ActualWidth, height = item.ActualHeight;
        W.DpiScale dpi = WM.VisualTreeHelper.GetDpi(item);
        double pixel = 1 / dpi.DpiScaleX;
        int index = list.ItemContainerGenerator.IndexFromContainer(item);
        dc.DrawRectangle(item.IsSelected ? Picked : index % 2 == 1 ? Shade : CardSkin.Paper, null, new W.Rect(0, 0, width, height));
        if (item.IsMouseOver && !item.IsSelected) dc.DrawRectangle(Hover, null, new W.Rect(0, 0, width, height));
        double[] edges = Edges(width, pixel);
        for (int i = 0; i < columns.Length; i++)
        {
            var cell = new W.Rect(edges[i], 0, Math.Max(0, edges[i + 1] - edges[i] - pixel), height - pixel);
            dc.PushClip(new WM.RectangleGeometry(cell));
            if (Draft(row, i) is { IsChanged: true } draft)
            {
                dc.DrawRectangle(CardSkin.Pending, null, cell);
                if (draft.IsValid) DrawCell(dc, columns[i], draft.Value, cell, dpi.PixelsPerDip);
                else DrawLine(dc, Text(draft.Box.Text, CardSkin.FontSize, CardSkin.Poor, cell.Width - Pad * 2, dpi.PixelsPerDip), cell, right: true);
            }
            else DrawCell(dc, columns[i], row.Cells[i], cell, dpi.PixelsPerDip);
            dc.Pop();
            dc.DrawRectangle(CardSkin.Rule, null, new W.Rect(edges[i + 1] - pixel, 0, pixel, height));
        }
        dc.DrawRectangle(CardSkin.Rule, null, new W.Rect(0, height - pixel, width, pixel));
    }

    private void DrawCell(WM.DrawingContext dc, Column column, object? value, W.Rect cell, double pixelsPerDip)
    {
        switch (column.Kind)
        {
            case Kind.Face when value is int face && Faces?.Invoke(face) is { } brush:
                double side = Math.Min(cell.Width, cell.Height) - 2;
                dc.DrawRectangle(brush, null, new W.Rect(cell.X + (cell.Width - side) / 2, cell.Y + (cell.Height - side) / 2, side, side));
                break;
            case Kind.Shield when value is Shield shield:
                double shieldHeight = Math.Min(cell.Height - 6, 22), shieldWidth = Math.Round(shieldHeight * 40 / 52);
                var area = new W.Rect(cell.X + Pad + 2, cell.Y + Math.Round((cell.Height - shieldHeight) / 2), shieldWidth, shieldHeight);
                ForceShield.Draw(dc, area, shield.Colour);
                WM.FormattedText name = Text(shield.Text, CardSkin.FontSize, CardSkin.Ink, cell.Right - area.Right - 6 - Pad, pixelsPerDip);
                dc.DrawText(name, new W.Point(Math.Round(area.Right + 6), Math.Round(cell.Y + (cell.Height - name.Height) / 2)));
                break;
            case Kind.Number when value is long number:
                if (column.Field is { } field && Theme.BarMaximum(field) is double max)
                {
                    // The bar along the cell's foot, no thicker in tall rows.
                    double ratio = Math.Clamp(number / max, 0, 1), barHeight = Math.Clamp(cell.Height / 5, 3, 6);
                    var track = new W.Rect(cell.X + 4, cell.Bottom - barHeight - 3, Math.Max(0, cell.Width - 8), barHeight);
                    dc.DrawRectangle(Track, null, track);
                    dc.DrawRectangle(BrushOf(Theme.BarColor(ratio)), null, track with { Width = Math.Round(track.Width * ratio) });
                }
                DrawLine(dc, Text(number.ToString("N0", CultureInfo.CurrentCulture), CardSkin.FontSize, CardSkin.Ink, cell.Width - Pad * 2, pixelsPerDip), cell, right: true);
                break;
            case Kind.TwoLines when value is string text:
                // The second line under the first, smaller and muted, the two centred; a long text wraps.
                int split = text.IndexOf('\n');
                string above = split < 0 ? text : text[..split], below = split < 0 ? "" : text[(split + 1)..];
                WM.FormattedText first = Text(above, CardSkin.FontSize, CardSkin.Ink, cell.Width - Pad - 3, pixelsPerDip, wrap: true);
                WM.FormattedText? second = below.Length == 0 ? null : Text(below, SmallSize, CardSkin.InkMuted, cell.Width - Pad - 3, pixelsPerDip, wrap: true);
                double top = cell.Y + Math.Max(0, (cell.Height - first.Height - (second?.Height ?? 0)) / 2);
                dc.DrawText(first, new W.Point(cell.X + Pad, Math.Round(top)));
                if (second is not null) dc.DrawText(second, new W.Point(cell.X + Pad, Math.Round(top + first.Height)));
                break;
            case Kind.Text or Kind.TwoLines when value is not null:
                DrawLine(dc, Text(TextOf(value), CardSkin.FontSize, CardSkin.Ink, cell.Width - Pad * 2, pixelsPerDip), cell, right: false);
                break;
        }
    }

    private static void DrawLine(WM.DrawingContext dc, WM.FormattedText text, W.Rect cell, bool right)
    {
        double x = right ? cell.Right - Pad - text.Width : cell.X + Pad;
        dc.DrawText(text, new W.Point(Math.Round(x), Math.Round(cell.Y + (cell.Height - text.Height) / 2)));
    }

    // The rows' scroll viewer, once the list has its template: a table in a hidden card gets it when first shown, not
    // when loaded.
    private void Attach(WC.ScrollViewer? viewer)
    {
        if (viewer is null || viewer == scroll) return;
        scroll = viewer;
        // The column names keep over their columns while the scroll bar takes its width from the rows.
        viewer.ScrollChanged += (_, e) =>
        {
            header.ScrollBar = viewer.ComputedVerticalScrollBarVisibility == W.Visibility.Visible ? W.SystemParameters.VerticalScrollBarWidth : 0;
            if (e.VerticalChange != 0 || e.HorizontalChange != 0) CancelInput();
            PositionInput();
        };
        viewer.PreviewMouseWheel += PassWheelAtEnd;
    }

    // A table that cannot scroll further that way hands the wheel to what holds it (a card that scrolls).
    private void PassWheelAtEnd(object sender, WI.MouseWheelEventArgs e)
    {
        if (scroll is null) return;
        if (e.Delta > 0 ? scroll.VerticalOffset > 0 : scroll.VerticalOffset < scroll.ScrollableHeight) return;
        e.Handled = true;
        if (WM.VisualTreeHelper.GetParent(this) is W.UIElement around)
            around.RaiseEvent(new WI.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
    }

    // The rows: a list box for the pick, ↑↓ and the virtualizing panel, whose items draw themselves.
    private sealed class RowList(GameTable table) : WC.ListBox
    {
        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            table.Attach(GetTemplateChild("scroll") as WC.ScrollViewer);
        }

        protected override W.DependencyObject GetContainerForItemOverride() => new RowItem(table);

        protected override bool IsItemItsOwnContainerOverride(object item) => item is RowItem;

        protected override void PrepareContainerForItemOverride(W.DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            var row = (RowItem)element;
            if (table.input is { } edit && edit.Item == row && !Equals(edit.Row.Tag, (item as Row)?.Tag)) table.CancelInput();
            row.Row = item as Row;
            row.InvalidateVisual();
        }

        protected override void OnKeyUp(WI.KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.Key is WI.Key.Up or WI.Key.Down or WI.Key.PageUp or WI.Key.PageDown or WI.Key.Home or WI.Key.End && SelectedItem is Row row) table.Pick(row);
        }

        protected override void OnKeyDown(WI.KeyEventArgs e)
        {
            if (e.Key == WI.Key.F2 && SelectedItem is Row row && ItemContainerGenerator.ContainerFromItem(row) is RowItem item)
            {
                int column = Enumerable.Range(0, table.columns.Length).FirstOrDefault(c => table.CanEdit(row, c), -1);
                if (column >= 0) e.Handled = table.BeginInput(item, column);
            }
            base.OnKeyDown(e);
        }
    }

    private sealed class RowItem : WC.ListBoxItem
    {
        private static readonly WC.ControlTemplate Bare = new(typeof(WC.ListBoxItem));
        private readonly GameTable table;

        internal RowItem(GameTable table)
        {
            this.table = table;
            Template = Bare;
            Height = table.RowHeight;
            FocusVisualStyle = null;
            // The game's sprites (the forces' shields) drawn much smaller than they are.
            WM.RenderOptions.SetBitmapScalingMode(this, WM.BitmapScalingMode.HighQuality);
            Cursor = WI.Cursors.Hand;
        }

        internal Row? Row { get; set; }

        protected override void OnRender(WM.DrawingContext dc) => table.DrawRow(dc, this);

        protected override void OnSelected(W.RoutedEventArgs e)
        {
            base.OnSelected(e);
            InvalidateVisual();
        }

        protected override void OnUnselected(W.RoutedEventArgs e)
        {
            base.OnUnselected(e);
            InvalidateVisual();
        }

        protected override void OnMouseEnter(WI.MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            InvalidateVisual();
        }

        protected override void OnMouseLeave(WI.MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            InvalidateVisual();
        }

        protected override void OnMouseMove(WI.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int column = table.ColumnAt(this, e.GetPosition(this).X);
            bool editable = Row is { } row && table.CanEdit(row, column);
            Cursor = editable ? WI.Cursors.IBeam : WI.Cursors.Hand;
            ToolTip = editable ? table.drafts is null ? L.Lists.EditCellTip : L.Lists.DraftCellTip : null;
        }

        protected override void OnMouseLeftButtonUp(WI.MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (table.BeginInput(this, table.ColumnAt(this, e.GetPosition(this).X))) e.Handled = true;
            else if (Row is { } row) table.Pick(row);
        }
    }

    // The column names on wood; a click sorts, the pointer over a field's name tells its range.
    private sealed class HeaderLine : WC.Canvas
    {
        private readonly GameTable table;
        private int tipColumn = -1;
        private const double ButtonSize = 22, ButtonGap = 2;
        private readonly Dictionary<int, PlateButton> actions = [];
        private bool stacked;

        internal HeaderLine(GameTable table)
        {
            this.table = table;
            ClipToBounds = true;
        }

        internal void UpdateActions()
        {
            for (int column = 0; column < table.columns.Length; column++)
            {
                if (table.columns[column] is not { Kind: Kind.Number, Field: { } field }) continue;
                if (!actions.TryGetValue(column, out PlateButton? button))
                {
                    int index = column;
                    button = new PlateButton(L.Lists.BulkColumn(field.Label), icon: Icons.Fill, iconOnly: true)
                    { Width = ButtonSize, Height = ButtonSize };
                    System.Windows.Automation.AutomationProperties.SetName(button, button.Text);
                    WC.ToolTipService.SetShowOnDisabled(button, true);
                    button.Click += () => _ = table.EditColumnAsync(index);
                    actions.Add(column, button);
                    Children.Add(button);
                }
                button.Visibility = table.columnWriter is null ? W.Visibility.Collapsed : W.Visibility.Visible;
                button.IsEnabled = table.columnWriter is not null && table.rows.Any(row => table.CanEdit(row, column));
                button.ToolTip = table.drafts is null ? L.Lists.BulkColumnTip(field.Label) : L.Lists.DraftColumnTip(field.Label);
            }
            InvalidateMeasure();
            InvalidateArrange();
            InvalidateVisual();
        }

        protected override W.Size MeasureOverride(W.Size available)
        {
            base.MeasureOverride(available);
            double[] edges = table.Edges(Math.Max(0, available.Width - scrollBar), 1 / WM.VisualTreeHelper.GetDpi(this).DpiScaleX);
            bool next = actions.Any(pair => pair.Value.Visibility == W.Visibility.Visible &&
                edges[pair.Key + 1] - edges[pair.Key] < Text(table.columns[pair.Key].Header, CardSkin.FontSize, CardSkin.InkOnDark,
                    1000, WM.VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace + Pad * 2 + ButtonSize + ButtonGap + 2 + (pair.Key == table.sortColumn ? 12 : 0));
            if (stacked != next) { stacked = next; InvalidateVisual(); }
            return new W.Size(0, stacked ? HeaderHeight * 2 : HeaderHeight);
        }

        protected override W.Size ArrangeOverride(W.Size size)
        {
            double[] edges = table.Edges(Math.Max(0, size.Width - scrollBar), 1 / WM.VisualTreeHelper.GetDpi(this).DpiScaleX);
            foreach ((int column, PlateButton button) in actions)
            {
                WC.Canvas.SetLeft(button, stacked ? (edges[column] + edges[column + 1] - ButtonSize) / 2 : Math.Max(edges[column], edges[column + 1] - ButtonSize - ButtonGap));
                WC.Canvas.SetTop(button, (stacked ? HeaderHeight : 0) + (HeaderHeight - ButtonSize) / 2);
            }
            return base.ArrangeOverride(size);
        }

        // The width the rows' scroll bar takes on the right, where no column name goes.
        internal double ScrollBar
        {
            set
            {
                if (scrollBar == value) return;
                scrollBar = value;
                InvalidateMeasure();
                InvalidateArrange();
                InvalidateVisual();
            }
        }
        private double scrollBar;

        private double[] Edges() => table.Edges(Math.Max(0, ActualWidth - scrollBar), 1 / WM.VisualTreeHelper.GetDpi(this).DpiScaleX);

        private int ColumnAt(double x)
        {
            double[] edges = Edges();
            for (int i = 0; i < table.columns.Length; i++)
                if (x >= edges[i] && x < edges[i + 1]) return i;
            return -1;
        }

        protected override void OnRender(WM.DrawingContext dc)
        {
            dc.DrawRectangle(CardSkin.Wood, null, new W.Rect(0, 0, ActualWidth, ActualHeight));
            W.DpiScale dpi = WM.VisualTreeHelper.GetDpi(this);
            double pixel = 1 / dpi.DpiScaleX;
            double[] edges = Edges();
            for (int i = 0; i < table.columns.Length; i++)
            {
                double left = edges[i], width = edges[i + 1] - left;
                bool sorted = i == table.sortColumn;
                double actionWidth = !stacked && actions.TryGetValue(i, out PlateButton? button) && button.Visibility == W.Visibility.Visible ? ButtonSize + ButtonGap : 0;
                dc.PushClip(new WM.RectangleGeometry(new W.Rect(left, 0, Math.Max(0, width - actionWidth), ActualHeight)));
                WM.FormattedText name = Text(table.columns[i].Header, CardSkin.FontSize, CardSkin.InkOnDark, width - Pad * 2 - actionWidth - (sorted ? 12 : 0), dpi.PixelsPerDip);
                dc.DrawText(name, new W.Point(Math.Round(left + Pad + 2), Math.Round((HeaderHeight - name.Height) / 2)));
                if (sorted)
                {
                    // The sort's triangle at the right of the name's cell: up for rising, down for falling.
                    double x = edges[i + 1] - actionWidth - 11, y = HeaderHeight / 2;
                    var triangle = new WM.StreamGeometry();
                    using (WM.StreamGeometryContext g = triangle.Open())
                    {
                        g.BeginFigure(table.descending ? new W.Point(x - 4, y - 2) : new W.Point(x - 4, y + 2), true, true);
                        g.LineTo(table.descending ? new W.Point(x + 4, y - 2) : new W.Point(x + 4, y + 2), false, false);
                        g.LineTo(table.descending ? new W.Point(x, y + 3) : new W.Point(x, y - 3), false, false);
                    }
                    triangle.Freeze();
                    dc.DrawGeometry(CardSkin.InkOnDark, null, triangle);
                }
                dc.Pop();
                if (i < table.columns.Length - 1) dc.DrawRectangle(HeaderRule, null, new W.Rect(edges[i + 1] - pixel, 4, pixel, ActualHeight - 8));
            }
        }

        protected override void OnMouseMove(WI.MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.OriginalSource is PlateButton) return;
            int column = ColumnAt(e.GetPosition(this).X);
            Cursor = column >= 0 && table.columns[column].Kind != Kind.Face ? WI.Cursors.Hand : WI.Cursors.Arrow;
            if (column == tipColumn) return;
            tipColumn = column;
            ToolTip = column >= 0 && table.columns[column].Field is { } field
                ? $"{field.Label}: {field.Min:N0}~{field.Max:N0}" + (field.Hint.Length > 0 ? $" ({field.Hint})" : "")
                : null;
        }

        protected override void OnMouseLeftButtonUp(WI.MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (e.Handled || e.OriginalSource is PlateButton) return;
            int column = ColumnAt(e.GetPosition(this).X);
            if (column < 0 || table.columns[column].Kind == Kind.Face) return;
            e.Handled = true;
            table.SortBy(column);
        }
    }
}

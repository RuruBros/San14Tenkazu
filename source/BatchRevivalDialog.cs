using System.Globalization;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace San14Tenkazu;

// [일괄 부활]: the officers to revive (all of them, picked by hand, or a random number of them) and a drawn status,
// city and death year for each. The rows show exactly what will be written; [다시 섞기] draws again without
// touching the checks.
internal sealed class BatchRevivalDialog : W.Window
{
    internal sealed record Row(OfficerRecord Officer, WC.CheckBox Check, WC.TextBlock Status, WC.TextBlock City, WC.TextBlock Year);

    private static readonly double[] Columns = [150, 110, 190, 50];
    private readonly GameSnapshot data;
    private readonly Random random;
    private readonly List<Row> rows = [];
    private readonly WC.StackPanel rowPanel = new();
    private readonly WC.TextBlock count = new() { Margin = new W.Thickness(0, 8, 0, 0), Foreground = CardSkin.InkMuted };
    private readonly WC.TextBlock error = new() { Margin = new W.Thickness(0, 4, 0, 8), Foreground = CardSkin.Poor, TextWrapping = W.TextWrapping.Wrap };
    private readonly Dictionary<int, OfficerRevival.Request> draws = [];
    private IReadOnlyList<OfficerRevival.Destination> cityChoices = [];
    private string optionsError = "", pickError = "", planError = "";
    private int skipped;
    private bool filling;

    internal WC.ComboBox StatusChoice { get; } = EditorCard.Choice(160);
    internal WC.ComboBox PlaceChoice { get; } = EditorCard.Choice(160);
    internal WC.ComboBox CityChoice { get; } = EditorCard.Choice(250);
    internal WC.TextBox MinYears { get; } = CardSkin.NumberBox(48);
    internal WC.TextBox MaxYears { get; } = CardSkin.NumberBox(48);
    internal WC.CheckBox HiddenChoice { get; } = new() { Content = L.BatchRevival.IncludeHidden, Foreground = CardSkin.Ink, Margin = new W.Thickness(0, 4, 0, 4) };
    internal WC.TextBox PickCount { get; } = CardSkin.NumberBox(48);
    internal PlateButton SelectAllButton { get; } = new(L.Surrender.SelectAll);
    internal PlateButton ClearButton { get; } = new(L.Surrender.ClearSelection);
    internal PlateButton PickButton { get; } = new(L.BatchRevival.Pick) { ToolTip = L.BatchRevival.PickTip };
    internal PlateButton ShuffleButton { get; } = new(L.BatchRevival.Shuffle) { ToolTip = L.BatchRevival.ShuffleTip };
    internal PlateButton AcceptButton { get; } = new(L.BatchRevival.Button, primary: true, icon: Icons.Revert);
    internal IReadOnlyList<Row> Rows => rows;
    internal string Error => error.Text;
    internal int[] Selected => rows.Where(r => r.Check.IsChecked == true).Select(r => r.Officer.Id).ToArray();
    // The checked rows' draws, in row order: what [일괄 부활] writes.
    internal IReadOnlyList<OfficerRevival.Request> Requests => Selected.Select(id => draws[id]).ToArray();

    private BatchRevivalStatus Status => StatusChoice.SelectedIndex is >= 0 and var index ? (BatchRevivalStatus)index : BatchRevivalStatus.Free;
    private bool FixedCity => PlaceChoice.SelectedIndex == 1;

    internal BatchRevivalDialog(GameSnapshot data, Random? random = null)
    {
        this.data = data;
        this.random = random ?? new Random();
        Title = L.BatchRevival.Button;
        WindowStartupLocation = W.WindowStartupLocation.CenterOwner;
        ResizeMode = W.ResizeMode.NoResize; ShowInTaskbar = false;
        SizeToContent = W.SizeToContent.WidthAndHeight;
        Background = CardSkin.Parchment; FontFamily = CardSkin.Font; FontSize = CardSkin.FontSize; Foreground = CardSkin.Ink;
        UseLayoutRounding = true;
        WM.TextOptions.SetTextFormattingMode(this, WM.TextFormattingMode.Display);

        filling = true;
        StatusChoice.ItemsSource = new[] { L.Revival.Free, L.Revival.Serving, L.BatchRevival.RandomStatus };
        StatusChoice.SelectedIndex = 0;
        PlaceChoice.ItemsSource = new[] { L.BatchRevival.RandomCity, L.BatchRevival.FixedCity };
        PlaceChoice.SelectedIndex = 0;
        CityChoice.Margin = new W.Thickness(6, 2, 0, 2);
        CityChoice.Visibility = W.Visibility.Collapsed;
        MinYears.Text = OfficerBatchRevival.DefaultMinYears.ToString(CultureInfo.CurrentCulture);
        MaxYears.Text = OfficerBatchRevival.DefaultMaxYears.ToString(CultureInfo.CurrentCulture);
        PickCount.Text = OfficerBatchRevival.DefaultPick.ToString(CultureInfo.CurrentCulture);
        filling = false;
        FillCities();
        FillRows(checkNew: true);
        Redraw(all: true);

        StatusChoice.SelectionChanged += (_, _) => { if (filling) return; FillCities(); Redraw(all: true); };
        PlaceChoice.SelectionChanged += (_, _) =>
        {
            CityChoice.Visibility = FixedCity ? W.Visibility.Visible : W.Visibility.Collapsed;
            Redraw(all: true);
        };
        CityChoice.SelectionChanged += (_, _) => Redraw(all: true);
        MinYears.TextChanged += (_, _) => Redraw(all: true);
        MaxYears.TextChanged += (_, _) => Redraw(all: true);
        HiddenChoice.Checked += (_, _) => { FillRows(checkNew: false); Redraw(all: false); };
        HiddenChoice.Unchecked += (_, _) => { FillRows(checkNew: false); Redraw(all: false); };
        PickCount.TextChanged += (_, _) => CheckPickCount();
        SelectAllButton.Click += () => SetChecks(_ => true);
        ClearButton.Click += () => SetChecks(_ => false);
        PickButton.Click += Pick;
        ShuffleButton.Click += () => Redraw(all: true);
        AcceptButton.Click += Accept;
        var cancel = new PlateButton(L.Window.Cancel) { Margin = new W.Thickness(8, 0, 0, 0) };
        cancel.Click += () => DialogResult = false;
        PreviewKeyDown += (_, e) =>
        {
            if (StatusChoice.IsDropDownOpen || PlaceChoice.IsDropDownOpen || CityChoice.IsDropDownOpen) return;
            if (e.Key == WI.Key.Escape) { DialogResult = false; e.Handled = true; }
            else if (e.Key == WI.Key.Enter)
            {
                // Enter in the number of officers picks them; in the year boxes it only ends the input.
                if (PickCount.IsKeyboardFocused) Pick();
                else if (!MinYears.IsKeyboardFocused && !MaxYears.IsKeyboardFocused) Accept();
                e.Handled = true;
            }
        };

        var years = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, Children =
        {
            EditorCard.Label(L.BatchRevival.YearsBefore), MinYears, EditorCard.Label("~"), MaxYears, EditorCard.Label(L.BatchRevival.YearsAfter)
        }};
        MinYears.Margin = MaxYears.Margin = new W.Thickness(0, 2, 6, 2);
        // [전체 선택] [선택 해제]  [N]명 [무작위 선택]  [다시 섞기]
        ClearButton.Margin = new W.Thickness(6, 0, 0, 0);
        PickCount.Margin = new W.Thickness(16, 2, 0, 2);
        ShuffleButton.Margin = new W.Thickness(16, 0, 0, 0);
        var selection = new WC.WrapPanel { Margin = new W.Thickness(0, 10, 0, 6) };
        foreach (W.FrameworkElement element in new W.FrameworkElement[] { SelectAllButton, ClearButton, PickCount,
            EditorCard.Label(L.BatchRevival.PickUnit, 4), PickButton, ShuffleButton })
        {
            element.VerticalAlignment = W.VerticalAlignment.Center;
            selection.Children.Add(element);
        }
        var header = RowGrid(L.Lists.OfficersTab, L.Revival.Mode, L.BatchRevival.Place, L.Fields.Death);
        header.Margin = new W.Thickness(20, 0, 0, 2);
        foreach (WC.TextBlock text in header.Children.OfType<WC.TextBlock>()) text.Foreground = CardSkin.InkMuted;
        Content = new WC.Border { Background = CardSkin.Parchment, Padding = new W.Thickness(16),
            Child = new WC.StackPanel { Width = 540, Children =
        {
            EditorCard.Line(L.Revival.Mode, StatusChoice),
            EditorCard.Line(L.BatchRevival.Place, PlaceChoice, CityChoice),
            EditorCard.Line(L.Revival.DeathYear, years),
            EditorCard.Line("", HiddenChoice),
            selection, header,
            new WC.Border { BorderBrush = CardSkin.Rule, BorderThickness = new W.Thickness(0, 1, 0, 1),
                Child = new WC.ScrollViewer { Content = rowPanel, MaxHeight = 300, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled } },
            count, error,
            new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = W.HorizontalAlignment.Right,
                Children = { AcceptButton, cancel } }
        }}};
        Loaded += (_, _) => StatusChoice.Focus();
        UpdateCount();
    }

    private static WC.Grid RowGrid(params string[] texts)
    {
        var grid = new WC.Grid();
        for (int column = 0; column < Columns.Length; column++)
        {
            grid.ColumnDefinitions.Add(new WC.ColumnDefinition { Width = new W.GridLength(Columns[column]) });
            var text = new WC.TextBlock { Text = column < texts.Length ? texts[column] : "", TextTrimming = W.TextTrimming.CharacterEllipsis,
                Margin = new W.Thickness(0, 0, 6, 0), VerticalAlignment = W.VerticalAlignment.Center };
            WC.Grid.SetColumn(text, column);
            grid.Children.Add(text);
        }
        return grid;
    }

    // The fixed-city choices follow the status: any city for 재야, a living ruler's city when the draw can serve.
    private void FillCities()
    {
        int selected = CityChoice.SelectedIndex is >= 0 and var index && index < cityChoices.Count ? cityChoices[index].Base : 0;
        cityChoices = OfficerBatchRevival.CityChoices(data, Status);
        bool wasFilling = filling;
        filling = true;
        CityChoice.ItemsSource = cityChoices.Select(c => c.Force > 0 ? c.Name + " · " + data.ForceName(c.Force) : c.Name).ToArray();
        int found = cityChoices.ToList().FindIndex(c => c.Base == selected);
        CityChoice.SelectedIndex = found >= 0 ? found : cityChoices.Count > 0 ? 0 : -1;
        filling = wasFilling;
    }

    // The candidates for the hidden-officer choice. Rows already shown keep their checks; new ones start checked only
    // when the dialog opens (모두 부활), so including 미등장 장수 never selects them unasked.
    private void FillRows(bool checkNew)
    {
        var found = OfficerBatchRevival.Find(data, HiddenChoice.IsChecked == true);
        skipped = found.Skipped;
        var shown = rows.ToDictionary(r => r.Officer.Id);
        var names = found.Officers.GroupBy(o => o.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        rows.Clear();
        rowPanel.Children.Clear();
        filling = true;
        foreach (OfficerRecord officer in found.Officers)
        {
            if (!shown.Remove(officer.Id, out Row? row))
            {
                var grid = RowGrid(names.Contains(officer.Name) ? $"{officer.Name} (#{officer.Id})" : officer.Name, "", "", "");
                var cells = grid.Children.OfType<WC.TextBlock>().ToArray();
                row = new(officer, new WC.CheckBox { Content = grid, Foreground = CardSkin.Ink, Margin = new W.Thickness(0, 2, 0, 2),
                    VerticalContentAlignment = W.VerticalAlignment.Center, IsChecked = checkNew }, cells[1], cells[2], cells[3]);
                row.Check.Checked += (_, _) => UpdateCount();
                row.Check.Unchecked += (_, _) => UpdateCount();
            }
            rows.Add(row);
            rowPanel.Children.Add(row.Check);
        }
        foreach (int gone in shown.Keys) draws.Remove(gone);
        filling = false;
    }

    private OfficerBatchRevival.Options? ReadOptions(out string why)
    {
        why = "";
        if (!TypedNumber.TryParse(MinYears.Text, out long min) || !TypedNumber.TryParse(MaxYears.Text, out long max))
        {
            why = data.Date is null ? L.Transfers.NoDate : L.BatchRevival.InvalidYears(OfficerBatchRevival.MaxYears(data));
            return null;
        }
        int city = 0;
        if (FixedCity)
        {
            if (CityChoice.SelectedIndex is not (>= 0 and var index) || index >= cityChoices.Count) { why = L.Transfers.PickBase; return null; }
            city = cityChoices[index].Base;
        }
        var options = new OfficerBatchRevival.Options(Status, city, (int)Math.Clamp(min, -1, 100_000), (int)Math.Clamp(max, -1, 100_000));
        why = OfficerBatchRevival.Check(data, options) ?? "";
        return why.Length == 0 ? options : null;
    }

    // Draws every row again (all) or only rows without a draw. Options that cannot draw clear the draws.
    private void Redraw(bool all)
    {
        if (filling) return;
        planError = "";
        if (ReadOptions(out optionsError) is not { } options) draws.Clear();
        else
        {
            if (all) draws.Clear();
            foreach (var (officer, request) in OfficerBatchRevival.Draw(data,
                rows.Select(r => r.Officer.Id).Where(id => !draws.ContainsKey(id)).ToArray(), options, random))
                draws[officer] = request;
        }
        foreach (Row row in rows) ShowDraw(row);
        UpdateCount();
    }

    private void ShowDraw(Row row)
    {
        if (!draws.TryGetValue(row.Officer.Id, out var request))
        {
            row.Status.Text = row.City.Text = row.Year.Text = "";
            return;
        }
        bool serving = request.Mode == RevivalMode.Serving;
        row.Status.Text = OfficerFields.StatusName(row.Officer.Status) + " → " + (serving ? L.Terms.StatusRegular : L.Terms.StatusFree);
        int force = serving ? data.ForceOfCorps(data.Cities[request.Base].Corps) : 0;
        row.City.Text = force > 0 ? data.CityName(request.Base) + " · " + data.ForceName(force) : data.CityName(request.Base);
        row.Year.Text = request.DeathYear.ToString(CultureInfo.CurrentCulture);
    }

    private void SetChecks(Func<int, bool> check)
    {
        filling = true;
        foreach (Row row in rows) row.Check.IsChecked = check(row.Officer.Id);
        filling = false;
        planError = "";
        UpdateCount();
    }

    private bool CheckPickCount()
    {
        bool valid = TypedNumber.TryParse(PickCount.Text, out long number) && number >= 1;
        pickError = valid ? "" : L.BatchRevival.InvalidCount;
        PickButton.IsEnabled = valid;
        UpdateCount();
        return valid;
    }

    // N officers from every candidate, replacing the checks; N at or above the candidates checks them all.
    private void Pick()
    {
        if (!CheckPickCount() || !TypedNumber.TryParse(PickCount.Text, out long number)) return;
        var picked = OfficerBatchRevival.Pick(rows.Select(r => r.Officer.Id).ToArray(), (int)Math.Min(number, int.MaxValue), random).ToHashSet();
        SetChecks(picked.Contains);
    }

    private void UpdateCount()
    {
        if (filling) return;
        int selected = Selected.Length;
        count.Text = L.BatchRevival.SelectedCount(selected) + (skipped > 0 ? " · " + L.BatchRevival.Skipped(skipped) : "");
        error.Text = planError.Length > 0 ? planError : optionsError.Length > 0 ? optionsError : pickError;
        AcceptButton.IsEnabled = selected > 0 && optionsError.Length == 0 && planError.Length == 0 && Selected.All(draws.ContainsKey);
    }

    // Each draw must still pass the single revival's plan on the dialog's snapshot before the dialog closes.
    private void Accept()
    {
        if (!AcceptButton.IsEnabled) return;
        foreach (var request in Requests)
        {
            EditPlan plan = OfficerRevival.Plan(data, request);
            if (!plan.Refused) continue;
            planError = L.BatchRevival.Refused(data.OfficerName(request.Officer), plan.Refusal);
            UpdateCount();
            return;
        }
        DialogResult = true;
    }
}

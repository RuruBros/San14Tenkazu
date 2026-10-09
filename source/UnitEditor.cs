using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;

namespace San14Tenkazu;

// A unit's editor card in two pages: 부대 (troops, wounded, morale and an army's 진형, with the fills the quick card had)
// and 능력·상태 (terrain/fire/supply, the 능력 변화 tactics and facilities leave, and 혼란·저지·도발). Its commander and force named under
// the title open their own cards.
internal sealed class UnitEditorCard : EditorCard
{
    // Everything the card shows for a unit: who leads it, where it is and goes, its values (UnitFields.All and
    // UnitFields.Timed), and for an army the 진형 it may be given (Formations, with each one's 기동).
    internal sealed record View(int Slot, int Commander, string CommanderName, int Force, string ForceName, System.Drawing.Color Colour,
        string Kind, string Formation, string Place, string Destination, IReadOnlyDictionary<FieldDef, long> Values,
        bool IsArmy, int FormationId, IReadOnlyList<GameName> Formations, IReadOnlyList<UnitCondition> Conditions);

    private const double TimedWidth = 56;
    private static readonly FieldDef[] Boxes = [.. UnitFields.All, .. UnitFields.Timed];

    private readonly Dictionary<FieldDef, NumberField> fields =
        Boxes.ToDictionary(f => f, f => new NumberField(f, UnitFields.All.Contains(f) ? 110 : TimedWidth));
    private readonly NameChoice formation = new(160, none: false);
    private readonly WC.DockPanel formationLine;
    private readonly WC.TextBlock place = new() { TextWrapping = W.TextWrapping.Wrap, MaxWidth = 280, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly WC.TextBlock destination = new() { TextWrapping = W.TextWrapping.Wrap, MaxWidth = 280, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly WC.StackPanel conditions = new();
    private int pending;
    private bool filling;

    // The unit being edited ((0, 0) when none).
    internal (int Slot, int Commander) Unit { get; private set; }

    internal override EditorTarget? Target => Unit.Commander == 0 ? null : new UnitTarget(Unit.Slot, Unit.Commander);

    internal override int Pending => pending;

    internal UnitEditorCard()
    {
        OffersGoTo();
        var page = new WC.StackPanel();
        page.Children.Add(Line(L.Unit.Place, place));
        page.Children.Add(Line(L.Unit.Destination, destination));
        page.Children.Add(Heading(L.Unit.Heading));
        foreach (FieldDef field in UnitFields.All)
            page.Children.Add(Line(field.Label, fields[field].Box, Muted(field.Hint)));
        formationLine = Line(UnitFields.Formation.Label, formation.Box);
        page.Children.Add(formationLine);
        var shortcuts = new WC.WrapPanel { Margin = new W.Thickness(0, 6, 0, 0) };
        shortcuts.Children.Add(Shortcut(L.Unit.Heal, L.Unit.HealTip, () =>
        {
            fields[UnitFields.Troops].Set(fields[UnitFields.Troops].Value + fields[UnitFields.Wounded].Value);
            fields[UnitFields.Wounded].Set(0);
        }, Icons.Heal));
        shortcuts.Children.Add(Shortcut(L.Unit.Morale(UnitFields.MoraleCap), L.Unit.MoraleTip(UnitFields.MoraleCap), () => fields[UnitFields.Morale].Set(UnitFields.MoraleCap)));
        page.Children.Add(shortcuts);
        page.Children.Add(Note(L.Unit.ComeAndGo));
        AddPage(L.Unit.Page, page);
        AddPage(L.Unit.StatusPage, StatusPage());

        foreach (NumberField field in fields.Values) field.Edited += Mark;
        formation.Box.SelectionChanged += (_, _) => Mark();
    }

    // 능력 변화: a level and the time left for each of the five; 상태 이상: the time left of each.
    private W.UIElement StatusPage()
    {
        var page = new WC.StackPanel();
        page.Children.Add(Heading(L.Unit.CurrentConditions));
        page.Children.Add(conditions);
        page.Children.Add(Note(L.Unit.ConditionsNote));
        page.Children.Add(Heading(L.Unit.Boosts));
        page.Children.Add(Line("", Column(L.Unit.Level), Column(L.Unit.TimeLeft)));
        for (int boost = 0; boost < UnitFields.BoostLevels.Length; boost++)
            page.Children.Add(Line(UnitFields.BoostName(boost), fields[UnitFields.BoostLevels[boost]].Box, fields[UnitFields.BoostTimes[boost]].Box));
        var boosts = new WC.WrapPanel { Margin = new W.Thickness(0, 4, 0, 0) };
        boosts.Children.Add(Shortcut(L.Unit.BoostAll, L.Unit.BoostAllTip(UnitFields.BoostLevelMax, UnitFields.BoostTimeMax), () =>
        {
            foreach (FieldDef level in UnitFields.BoostLevels) fields[level].Set(UnitFields.BoostLevelMax);
            foreach (FieldDef time in UnitFields.BoostTimes) fields[time].Set(UnitFields.BoostTimeMax);
        }));
        page.Children.Add(boosts);
        page.Children.Add(Heading(L.Unit.Ailments));
        page.Children.Add(Line("", Column(L.Unit.TimeLeft)));
        for (int ailment = 0; ailment < UnitFields.Ailments.Length; ailment++)
            page.Children.Add(Line(UnitFields.AilmentName(ailment), fields[UnitFields.Ailments[ailment]].Box));
        var ailments = new WC.WrapPanel { Margin = new W.Thickness(0, 4, 0, 0) };
        ailments.Children.Add(Shortcut(L.Unit.ClearAilments, L.Unit.ClearAilmentsTip, () =>
        {
            foreach (FieldDef ailment in UnitFields.Ailments) fields[ailment].Set(0);
        }, Icons.Heal));
        page.Children.Add(ailments);
        page.Children.Add(Note(L.Unit.TimeNote(UnitFields.HoursPerCount, UnitFields.BoostPercent)));
        return page;
    }

    private static WC.TextBlock Column(string text) => new()
    {
        Text = text, Width = TimedWidth + 6, Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Center
    };

    private static WC.TextBlock Muted(string text) => new() { Text = text, Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Center };

    private static WC.TextBlock Note(string text) => new()
    {
        Text = text, Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 6, 0, 0)
    };

    internal void Open(View view)
    {
        if ((view.Slot, view.Commander) != Unit) StartOver();
        Unit = (view.Slot, view.Commander);
        Title.Text = L.Unit.Title(view.CommanderName);
        Shield.Colour = view.Colour;
        ShowSummary(view);
        foreach ((FieldDef field, NumberField box) in fields) box.Show(view.Values[field]);
        ShowFormation(view);
        Mark();
    }

    // Untouched boxes follow game values; position, environmental conditions and supply follow every refresh.
    internal void Follow(View view)
    {
        if ((view.Slot, view.Commander) != Unit) return;
        ShowSummary(view);
        foreach ((FieldDef field, NumberField box) in fields) box.Follow(view.Values[field]);
        if (formation.Picked == formation.Current && view.FormationId != formation.Current) ShowFormation(view);
        Mark();
    }

    private void ShowFormation(View view)
    {
        filling = true;
        formation.SetNames(view.Formations);
        formation.Show(view.FormationId);
        filling = false;
        formationLine.Visibility = view.IsArmy ? W.Visibility.Visible : W.Visibility.Collapsed;
    }

    private void ShowSummary(View view)
    {
        conditions.Children.Clear();
        foreach (UnitCondition condition in view.Conditions)
        {
            var value = new WC.TextBlock
            {
                Text = condition.Value, TextWrapping = W.TextWrapping.Wrap,
                Foreground = condition.Warning ? CardSkin.Poor : CardSkin.Ink,
                VerticalAlignment = W.VerticalAlignment.Center, MaxWidth = 280,
                ToolTip = condition.Hint
            };
            conditions.Children.Add(Line(condition.Label, value));
        }
        var commander = new WD.Span();
        commander.Inlines.Add(new WD.Run(L.Unit.CommanderLabel + " "));
        commander.Inlines.Add(Link(view.CommanderName, new OfficerTarget(view.Commander)));
        Fill(Subtitle, new WD.Inline[]
        {
            view.Force > 0 ? Link(view.ForceName, new ForceTarget(view.Force)) : new WD.Run(view.ForceName),
            commander, new WD.Run(view.Kind)
        }.Concat(view.Formation.Length > 0 ? [new WD.Run(view.Formation)] : []));
        place.Text = view.Place.Length > 0 ? view.Place : "-";
        destination.Text = view.Destination.Length > 0 ? view.Destination : "-";
    }

    // What to write, or null (with the reason in error and the box focused) when something typed is not valid.
    internal List<FieldChange>? Changes(out string error)
    {
        error = "";
        var changes = new List<FieldChange>();
        foreach (NumberField field in fields.Values.Where(f => f.IsChanged))
        {
            if (!field.TryRead(out long value, out error)) return null;
            changes.Add(new FieldChange(field.Field, value));
        }
        if (formationLine.Visibility == W.Visibility.Visible && formation.Picked != formation.Current)
            changes.Add(new FieldChange(UnitFields.Formation, formation.Picked));
        return changes;
    }

    private void Mark()
    {
        if (filling) return;
        pending = fields.Values.Count(f => f.IsChanged) + (formation.Mark() ? 1 : 0);
        Count();
    }
}

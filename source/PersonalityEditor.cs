using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

// A 개성's editor card: what it does, how long it lasts, whom its effects reach and when, its two effects, and the
// officers who have it. The 개성 is the game's own definition, saved with the game: a change reaches every officer
// with it in that save, so [변경 적용] says how many before it writes (PersonalityEdits). How long it lasts comes from
// the game's files and is shown only; conditions and kinds that do nothing with it are greyed with why
// (PersonalityRules), and under a 괴멸·격파 condition the kinds are the game's morale mark.
internal sealed class PersonalityEditorCard : EditorCard
{
    // One effect: its kind (0 none) and value.
    internal readonly record struct EffectShown(int Kind, long Value);

    // Everything the card shows for a 개성: its condition, number and second condition, how long it lasts (Days for one
    // lit by an attack), its effects, the kinds there are (named, with whom each reaches), the kinds that are only
    // switched on, and the officers who have it (id, name, in play).
    internal sealed record View(int Index, string Name, string Description, int Condition, long ConditionValue, int Filter,
        PersonalityRules.Timing Timing, int Days, IReadOnlyList<EffectShown> Effects, IReadOnlyList<GameName> Kinds, IReadOnlySet<int> Switches,
        IReadOnlyList<(int Officer, string Name, bool Active)> Holders);

    private const int ShownHolders = 60;
    private readonly NameChoice condition = new(270, none: false), filter = new(270, none: false);
    private readonly NameChoice[] kinds = [new(270), new(270)];
    private readonly NumberField number = new(PersonalityFields.ConditionValue, 100);
    private readonly NumberField[] values = PersonalityFields.Values.Select(f => new NumberField(f, 100)).ToArray();
    private readonly WC.TextBlock timing = new() { TextWrapping = W.TextWrapping.Wrap, MaxWidth = 270, VerticalAlignment = W.VerticalAlignment.Center };
    private readonly WC.DockPanel numberLine, filterLine;
    private readonly WC.TextBlock numberLabel;
    private readonly WC.TextBlock description = new() { TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 2, 0, 4) };
    private readonly WC.WrapPanel holders = new();
    private readonly WC.TextBlock holderNote = new() { Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap };
    private View? view;
    private bool settling;
    private int pending;

    internal int Personality { get; private set; }

    internal override EditorTarget? Target => Personality == 0 ? null : new PersonalityTarget(Personality);

    internal override int Pending => pending;

    internal PersonalityEditorCard()
    {
        Shield.Visibility = W.Visibility.Collapsed;
        filter.SetNames(PersonalityRules.Filters.Select(f => new GameName(f, PersonalityEdits.FilterText(f), "")));
        var page = new WC.StackPanel();
        page.Children.Add(Heading(L.Personality.Does));
        page.Children.Add(description);
        page.Children.Add(Heading(L.Personality.Effects));
        WC.DockPanel timingLine = Line(L.Personality.Timing, timing);
        timingLine.ToolTip = L.Personality.TimingTip;
        page.Children.Add(timingLine);
        page.Children.Add(Line(L.Personality.Condition, condition.Box));
        numberLine = Line(L.Personality.TroopsLabel, number.Box);
        numberLabel = (WC.TextBlock)numberLine.Children[0];
        page.Children.Add(numberLine);
        filterLine = Line(L.Personality.Filter, filter.Box);
        page.Children.Add(filterLine);
        for (int effect = 0; effect < values.Length; effect++)
        {
            page.Children.Add(Line(effect == 0 ? L.Personality.Effect1 : L.Personality.Effect2, kinds[effect].Box));
            page.Children.Add(Line(L.Personality.Value, values[effect].Box));
            values[effect].Edited += Settle;
            kinds[effect].Box.SelectionChanged += (_, _) => Settle();
        }
        condition.Box.SelectionChanged += (_, _) => Settle();
        filter.Box.SelectionChanged += (_, _) => Settle();
        number.Edited += Settle;
        page.Children.Add(new WC.TextBlock
        {
            Text = L.Personality.Note,
            Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 6, 0, 0)
        });
        page.Children.Add(Heading(L.Personality.Holders));
        page.Children.Add(holders);
        page.Children.Add(holderNote);
        AddPage(L.Personality.Page, page);
    }

    internal void Open(View shown)
    {
        if (shown.Index != Personality) StartOver();
        Personality = shown.Index;
        view = shown;
        Title.Text = shown.Name;
        settling = true;
        IEnumerable<int> offered = PersonalityRules.Offered.Contains(shown.Condition) ? PersonalityRules.Offered : PersonalityRules.Offered.Append(shown.Condition);
        condition.SetNames(offered.Select(c => new GameName(c, PersonalityEdits.ConditionText(c), "")));
        condition.Show(shown.Condition);
        condition.Allow(c => PersonalityRules.ConditionProblem(shown.Timing, c));
        filter.Show(shown.Filter);
        number.Show(shown.ConditionValue);
        for (int effect = 0; effect < values.Length; effect++)
        {
            kinds[effect].SetNames(shown.Kinds);
            kinds[effect].Show(shown.Effects[effect].Kind);
            values[effect].Show(shown.Effects[effect].Value);
        }
        settling = false;
        Show(shown);
        Settle();
    }

    // The game's values now: boxes nobody changed follow them.
    internal void Follow(View shown)
    {
        if (shown.Index != Personality) return;
        view = shown;
        settling = true;
        number.Follow(shown.ConditionValue);
        for (int effect = 0; effect < values.Length; effect++) values[effect].Follow(shown.Effects[effect].Value);
        settling = false;
        Show(shown);
        Settle();
    }

    private void Show(View shown)
    {
        int active = shown.Holders.Count(h => h.Active);
        Subtitle.Text = L.Personality.Subtitle(shown.Holders.Count, active);
        description.Text = shown.Description.Length > 0 ? shown.Description : "-";
        timing.Text = shown.Timing switch
        {
            PersonalityRules.Timing.Always => L.Personality.TimingAlways,
            PersonalityRules.Timing.WhileMet => L.Personality.TimingWhileMet,
            _ => L.Personality.TimingAfterAttack(shown.Days)
        };
        condition.Box.IsEnabled = shown.Timing != PersonalityRules.Timing.AfterAttack;
        holders.Children.Clear();
        foreach ((int officer, string name, bool _) in shown.Holders.OrderByDescending(h => h.Active).Take(ShownHolders))
            holders.Children.Add(Chip(name, "", added: false, remove: null, open: () => RaiseLink(new OfficerTarget(officer))));
        holderNote.Text = shown.Holders.Count == 0 ? L.Personality.NoHolders
            : shown.Holders.Count > ShownHolders ? L.Personality.MoreHolders(shown.Holders.Count - ShownHolders) : "";
        holderNote.Visibility = holderNote.Text.Length > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
    }

    // After a choice: the lines the picked condition uses, the kinds that work with it, the values a kind fixes (none
    // has 0, one only switched on 1, the event's mark follows its value), then what differs from the game.
    private void Settle()
    {
        if (settling || view is null) return;
        settling = true;
        int picked = condition.Picked;
        PersonalityRules.Target target = PersonalityRules.TargetOf(picked);
        bool eventMorale = target == PersonalityRules.Target.Event;
        PersonalityRules.Number numberMeaning = PersonalityRules.NumberOf(view.Index, picked);
        numberLine.Visibility = numberMeaning == PersonalityRules.Number.None ? W.Visibility.Collapsed : W.Visibility.Visible;
        numberLabel.Text = numberMeaning == PersonalityRules.Number.Range ? L.Personality.RangeLabel : L.Personality.TroopsLabel;
        bool filtered = view.Timing == PersonalityRules.Timing.WhileMet && target is PersonalityRules.Target.Own or PersonalityRules.Target.Others;
        filterLine.Visibility = filtered ? W.Visibility.Visible : W.Visibility.Collapsed;
        if (filtered && !PersonalityRules.Filters.Contains(filter.Picked)) filter.Pick(PersonalityRules.FilterArmy);
        for (int effect = 0; effect < values.Length; effect++)
        {
            int gameKind = view.Effects[effect].Kind;
            if (eventMorale) kinds[effect].Pick(values[effect].Value != 0 ? PersonalityRules.EventMorale : 0);
            else if (kinds[effect].Picked == PersonalityRules.EventMorale && gameKind != PersonalityRules.EventMorale) kinds[effect].Pick(gameKind);
            kinds[effect].Box.IsEnabled = !eventMorale;
            kinds[effect].Allow(k => PersonalityRules.KindProblem(view.Timing, picked, k));
            int kind = kinds[effect].Picked;
            bool free = eventMorale || kind != 0 && !view.Switches.Contains(kind);
            values[effect].Box.IsEnabled = free;
            if (!free && kind != gameKind) values[effect].Set(kind == 0 ? 0 : 1);
        }
        settling = false;
        Mark();
    }

    // What to write, or null (with the reason in error and the box focused) when something typed is not valid.
    internal List<FieldChange>? Changes(out string error)
    {
        error = "";
        var changes = new List<FieldChange>();
        if (view is null) return changes;
        if (condition.Picked != view.Condition) changes.Add(new FieldChange(PersonalityFields.Condition, condition.Picked));
        if (numberLine.Visibility == W.Visibility.Visible && number.IsChanged)
        {
            if (!number.TryRead(out long value, out error)) return null;
            changes.Add(new FieldChange(PersonalityFields.ConditionValue, value));
        }
        if (filterLine.Visibility == W.Visibility.Visible && filter.Picked != view.Filter) changes.Add(new FieldChange(PersonalityFields.Filter, filter.Picked));
        for (int effect = 0; effect < values.Length; effect++)
        {
            if (kinds[effect].Picked != view.Effects[effect].Kind) changes.Add(new FieldChange(PersonalityFields.Kinds[effect], kinds[effect].Picked));
            if (!values[effect].IsChanged) continue;
            if (!values[effect].TryRead(out long value, out error)) return null;
            changes.Add(new FieldChange(PersonalityFields.Values[effect], value));
        }
        return changes;
    }

    private void Mark()
    {
        pending = (condition.Mark() ? 1 : 0) + (filterLine.Visibility == W.Visibility.Visible && filter.Mark() ? 1 : 0)
            + (numberLine.Visibility == W.Visibility.Visible && number.IsChanged ? 1 : 0)
            + kinds.Count(k => k.Mark()) + values.Count(f => f.IsChanged);
        Count();
    }
}

using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

internal sealed class TacticEditorCard : EditorCard
{
    internal sealed record View(TacticRecord Record, RuleProfile Profile);
    private readonly Dictionary<TacticField, NumberField> fields = [];
    private readonly Dictionary<TacticField, NameChoice> choices = [];
    private readonly Dictionary<TacticField, WC.TextBlock> details = [], labels = [];
    private readonly WC.CheckBox auto = new() { Content = L.Rules.AutoApply, ToolTip = L.Rules.AutoTip };
    private readonly PlateButton restore = new(L.Rules.Restore, icon: Icons.Revert) { ToolTip = L.Rules.RestoreTip };
    private bool showing;
    internal event Action? RestoreClicked;
    internal event Action<bool>? AutoChanged;
    internal int Tactic { get; private set; }
    internal override EditorTarget? Target => Tactic == 0 ? null : new TacticTarget(Tactic);
    internal override int Pending => Target is null ? 0 : fields.Values.Count(f => f.IsChanged) + choices.Values.Count(c => c.Picked != c.Current);
    internal TacticEditorCard()
    {
        Shield.Visibility = W.Visibility.Collapsed;
        var page = new WC.StackPanel();
        page.Children.Add(new WC.TextBlock { Text = L.Rules.Note, TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 0, 0, 12) });
        page.Children.Add(auto);
        foreach (TacticField f in TacticRules.Fields)
        {
            if (f.Id.StartsWith("kind", StringComparison.Ordinal)) page.Children.Add(Heading(f.Slot == 0 ? L.Personality.Effect1 : L.Personality.Effect2));
            var info = new WC.TextBlock { Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, VerticalAlignment = W.VerticalAlignment.Center };
            details[f] = info;
            W.UIElement input;
            if (f.Id.StartsWith("kind", StringComparison.Ordinal) || f.Id == "ability")
            {
                var choice = new NameChoice(180, none: false);
                choice.SetNames(f.Id == "ability" ? [new GameName(1, L.Rules.Martial, ""), new GameName(2, L.Rules.Intelligence, "")]
                    : Enumerable.Range(0, 24).Select(k => new GameName(k, TacticRules.KindName(k), "")));
                choices[f] = choice; input = choice.Box;
                choice.Box.SelectionChanged += (_, _) => { if (!showing) Settle(); };
            }
            else
            {
                var number = new NumberField(f.Input, 80); fields[f] = number; input = number.Box;
                number.Edited += Settle;
            }
            var line = Line(f.Label, input, info); labels[f] = (WC.TextBlock)line.Children[0]; page.Children.Add(line);
        }
        page.Children.Add(new WC.TextBlock { Text = L.Rules.TacticHint, TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 10, 0, 0) });
        AddPage(L.Rules.TacticPage, page); Shortcuts.Children.Add(restore);
        restore.Click += () => RestoreClicked?.Invoke();
        auto.Checked += (_, _) => { if (!showing) AutoChanged?.Invoke(true); };
        auto.Unchecked += (_, _) => { if (!showing) AutoChanged?.Invoke(false); };
    }
    internal void SetAvailable(bool available) { restore.IsEnabled = auto.IsEnabled = available; }
    internal void Open(View view)
    {
        if (Tactic != view.Record.Id) StartOver();
        Tactic = view.Record.Id; showing = true;
        foreach (var f in fields) f.Value.Show(view.Record.Value(f.Key));
        foreach (var f in choices) f.Value.Show(view.Record.Value(f.Key));
        showing = false; Follow(view);
    }
    private void Settle()
    {
        if (showing) return;
        foreach (var c in choices) c.Value.Mark();
        for (int slot = 0; slot < 2; ++slot)
        {
            int kind = choices[TacticRules.Field($"kind{slot + 1}")].Picked;
            labels[TacticRules.Field($"amount{slot + 1}")].Text = TacticRules.AmountLabel(kind);
        }
        Count();
    }
    internal void Follow(View view)
    {
        if (view.Record.Id != Tactic) return;
        Title.Text = $"{view.Record.Name} · #{Tactic}"; Subtitle.Text = L.Rules.TacticPage;
        showing = true; auto.IsChecked = view.Profile.AutoApply;
        foreach (TacticField f in TacticRules.Fields)
        {
            int now = view.Record.Value(f), baseline = view.Profile.Default(TacticRules.Key(Tactic, f)), desired = view.Profile.Desired(TacticRules.Key(Tactic, f));
            if (fields.TryGetValue(f, out var number)) number.Follow(now); else choices[f].Follow(now);
            string state = desired == baseline ? now == baseline ? L.Rules.Unchanged : L.Rules.Conflict
                : now == desired ? L.Rules.Applied : now == baseline ? L.Rules.Waiting : L.Rules.Conflict;
            details[f].Text = $"{L.Rules.Current} {now} · {L.Rules.Baseline} {baseline}\n{L.Rules.Setting} {desired} · {state}";
        }
        showing = false; Settle();
    }
    internal RuleChange[]? Changes(out string error)
    {
        error = ""; var changes = new List<RuleChange>();
        foreach (var f in fields.Where(f => f.Value.IsChanged))
        {
            if (!f.Value.TryRead(out long value, out error)) return null;
            changes.Add(new(TacticRules.Key(Tactic, f.Key), (int)f.Value.Current, (int)value));
        }
        foreach (var f in choices.Where(f => f.Value.Picked != f.Value.Current))
        {
            if (f.Value.Picked < f.Key.Input.Min || f.Value.Picked > f.Key.Maximum) { error = L.Rules.BadProfile; return null; }
            changes.Add(new(TacticRules.Key(Tactic, f.Key), f.Value.Current, f.Value.Picked));
        }
        return changes.ToArray();
    }
}

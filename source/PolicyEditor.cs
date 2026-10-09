using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

internal sealed class PolicyEditorCard : EditorCard
{
    internal sealed record View(PolicyRecord Record, PolicySnapshot Snapshot, RuleProfile Profile);
    private readonly Dictionary<PolicyField, NumberField> fields = [];
    private readonly Dictionary<PolicyField, NameChoice> choices = [];
    private readonly Dictionary<PolicyField, WC.TextBlock> details = [];
    private readonly Dictionary<PolicyField, WC.TextBlock> links = [];
    private readonly WC.CheckBox auto = new() { Content = L.Rules.AutoApply, ToolTip = L.Rules.AutoTip };
    private readonly PlateButton restore = new(L.Rules.Restore, icon: Icons.Revert) { ToolTip = L.Rules.RestoreTip };
    private PolicySnapshot? snapshot;
    private bool showing;
    internal string Table { get; }
    internal int RecordId { get; private set; }
    internal override EditorTarget? Target => RecordId == 0 ? null : new PolicyTarget(Table, RecordId);
    internal override int Pending => Target is null ? 0 : fields.Values.Count(f => f.IsChanged) + choices.Values.Count(c => c.Picked != c.Current);
    internal event Action? RestoreClicked;
    internal event Action<bool>? AutoChanged;
    internal PolicyEditorCard(string table)
    {
        Table = table; Shield.Visibility = W.Visibility.Collapsed;
        var page = new WC.StackPanel();
        page.Children.Add(new WC.TextBlock { Text = L.Rules.Note, TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 0, 0, 12) });
        page.Children.Add(auto);
        foreach (PolicyField f in PolicyRules.Fields(table))
        {
            var info = new WC.TextBlock { Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, VerticalAlignment = W.VerticalAlignment.Center }; details[f] = info;
            W.UIElement input;
            if (table == "policy")
            {
                var choice = new NameChoice(180); choices[f] = choice; input = choice.Box;
                choice.Box.SelectionChanged += (_, _) => Settle();
            }
            else
            {
                var number = new NumberField(f.Input, 80); fields[f] = number; input = number.Box; number.Edited += Settle;
            }
            var summary = new WC.StackPanel(); summary.Children.Add(info);
            if (table == "policy") { var link = new WC.TextBlock(); links[f] = link; summary.Children.Add(link); }
            page.Children.Add(Line(f.Label, input, summary));
        }
        page.Children.Add(new WC.TextBlock { Text = table == "policy" ? L.Rules.PolicyHint : L.Rules.PolicyEffectHint,
            TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 10, 0, 0) });
        AddPage(table == "policy" ? L.Rules.PolicyPage : L.Rules.PolicyEffects, page); Shortcuts.Children.Add(restore);
        restore.Click += () => RestoreClicked?.Invoke();
        auto.Checked += (_, _) => { if (!showing) AutoChanged?.Invoke(true); };
        auto.Unchecked += (_, _) => { if (!showing) AutoChanged?.Invoke(false); };
    }
    internal void SetAvailable(bool available) { restore.IsEnabled = auto.IsEnabled = available; }
    internal void Open(View view)
    {
        if (RecordId != view.Record.Id) StartOver();
        RecordId = view.Record.Id; snapshot = view.Snapshot; showing = true;
        foreach (var f in fields) f.Value.Show(view.Record.Value(f.Key));
        foreach (var f in choices)
        {
            f.Value.SetNames(view.Snapshot.Records.Where(r => r.Table == "policyeffect").Select(r => new GameName(r.Id, $"{r.Name} · #{r.Id}", "")));
            f.Value.Show(view.Record.Value(f.Key));
        }
        showing = false; Follow(view);
    }
    private void Settle()
    {
        if (showing) return;
        foreach (var c in choices)
        {
            c.Value.Mark(); links[c.Key].Inlines.Clear();
            int id = c.Value.Picked;
            if (snapshot?.Records.Any(r => r.Table == "policyeffect" && r.Id == id) is true)
                links[c.Key].Inlines.Add(Link(L.Rules.PolicyEditLevels, new PolicyTarget("policyeffect", id)));
        }
        Count();
    }
    internal void Follow(View view)
    {
        if (RecordId != view.Record.Id) return;
        snapshot = view.Snapshot; Title.Text = $"{view.Record.Name} · #{RecordId}"; Subtitle.Text = Table == "policy" ? L.Rules.PolicyPage : L.Rules.PolicyEffects;
        showing = true; auto.IsChecked = view.Profile.AutoApply;
        foreach (PolicyField f in PolicyRules.Fields(Table))
        {
            int now = view.Record.Value(f), baseline = view.Profile.Default(PolicyRules.Key(RecordId, f)), desired = view.Profile.Desired(PolicyRules.Key(RecordId, f));
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
            changes.Add(new(PolicyRules.Key(RecordId, f.Key), (int)f.Value.Current, (int)value));
        }
        foreach (var f in choices.Where(f => f.Value.Picked != f.Value.Current))
        {
            if (f.Value.Picked < 0 || f.Value.Picked > f.Key.Maximum) { error = L.Rules.BadProfile; return null; }
            changes.Add(new(PolicyRules.Key(RecordId, f.Key), f.Value.Current, f.Value.Picked));
        }
        return changes.ToArray();
    }
}

using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

internal sealed class FormationEditorCard : EditorCard
{
    internal sealed record View(FormationRecord Record, RuleProfile Profile);
    private readonly Dictionary<FormationField, NumberField> fields = [];
    private readonly Dictionary<FormationField, WC.TextBlock> details = [];
    private readonly WC.CheckBox auto = new() { Content = L.Rules.AutoApply, ToolTip = L.Rules.AutoTip };
    private readonly PlateButton restore = new(L.Rules.Restore, icon: Icons.Revert) { ToolTip = L.Rules.RestoreTip };
    private bool showing;
    internal event Action? RestoreClicked;
    internal event Action<bool>? AutoChanged;
    internal int Formation { get; private set; }
    internal override EditorTarget? Target => Formation == 0 ? null : new FormationTarget(Formation);
    internal override int Pending => Target is null ? 0 : fields.Values.Count(f => f.IsChanged);
    internal FormationEditorCard()
    {
        Shield.Visibility = W.Visibility.Collapsed;
        var page = new WC.StackPanel();
        page.Children.Add(new WC.TextBlock { Text = L.Rules.Note, TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 0, 0, 12) });
        page.Children.Add(auto);
        foreach (FormationField f in FormationRules.Fields)
        {
            var input = new NumberField(f.Input, 80);
            var info = new WC.TextBlock { Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, VerticalAlignment = W.VerticalAlignment.Center };
            fields[f] = input;
            details[f] = info;
            page.Children.Add(Line(f.Label, input.Box, info));
            input.Edited += Count;
        }
        page.Children.Add(new WC.TextBlock { Text = L.Rules.ValueHint, TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 10, 0, 0) });
        AddPage(L.Rules.Formation, page);
        Shortcuts.Children.Add(restore);
        restore.Click += () => RestoreClicked?.Invoke();
        auto.Checked += (_, _) => { if (!showing) AutoChanged?.Invoke(true); };
        auto.Unchecked += (_, _) => { if (!showing) AutoChanged?.Invoke(false); };
    }
    internal void SetAvailable(bool available) { restore.IsEnabled = auto.IsEnabled = available; }
    internal void Open(View view)
    {
        if (Formation != view.Record.Id) StartOver();
        Formation = view.Record.Id;
        foreach (var f in fields) f.Value.Show(view.Record.Value(f.Key));
        Follow(view);
    }
    internal void Follow(View view)
    {
        if (view.Record.Id != Formation) return;
        Title.Text = $"{view.Record.Name} · #{Formation}";
        Subtitle.Text = L.Rules.Formation;
        showing = true;
        auto.IsChecked = view.Profile.AutoApply;
        showing = false;
        foreach (var f in fields)
        {
            RuleKey key = FormationRules.Key(Formation, f.Key);
            int now = view.Record.Value(f.Key), baseline = view.Profile.Default(key), desired = view.Profile.Desired(key);
            f.Value.Follow(now);
            string state = desired == baseline ? now == baseline ? L.Rules.Unchanged : L.Rules.Conflict
                : now == desired ? L.Rules.Applied : now == baseline ? L.Rules.Waiting : L.Rules.Conflict;
            details[f.Key].Text = $"{L.Rules.Current} {now} · {L.Rules.Baseline} {baseline}\n{L.Rules.Setting} {desired} · {state}";
        }
        Count();
    }
    internal RuleChange[]? Changes(out string error)
    {
        error = "";
        var changes = new List<RuleChange>();
        foreach (var f in fields.Where(f => f.Value.IsChanged))
        {
            if (!f.Value.TryRead(out long value, out error)) return null;
            changes.Add(new(FormationRules.Key(Formation, f.Key), (int)f.Value.Current, (int)value));
        }
        return changes.ToArray();
    }
}

using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

internal sealed class OfficeEditorCard : EditorCard
{
    internal sealed record View(OfficeRecord Record, RuleProfile Profile);
    private readonly Dictionary<OfficeField, NumberField> fields = [];
    private readonly Dictionary<OfficeField, WC.TextBlock> details = [];
    private readonly WC.CheckBox auto = new() { Content = L.Rules.AutoApply, ToolTip = L.Rules.AutoTip };
    private readonly PlateButton restore = new(L.Rules.Restore, icon: Icons.Revert) { ToolTip = L.Rules.RestoreTip };
    private bool showing;
    internal int Office { get; private set; }
    internal override EditorTarget? Target => Office == 0 ? null : new OfficeTarget(Office);
    internal override int Pending => Target is null ? 0 : fields.Values.Count(f => f.IsChanged);
    internal event Action? RestoreClicked;
    internal event Action<bool>? AutoChanged;
    internal OfficeEditorCard()
    {
        Shield.Visibility = W.Visibility.Collapsed;
        var page = new WC.StackPanel();
        page.Children.Add(new WC.TextBlock { Text = L.Rules.Note, TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 0, 0, 12) });
        page.Children.Add(auto);
        foreach (OfficeField f in OfficeRules.Fields)
        {
            var number = new NumberField(f.Input, 80); fields[f] = number; number.Edited += Count;
            var detail = new WC.TextBlock { Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, VerticalAlignment = W.VerticalAlignment.Center }; details[f] = detail;
            page.Children.Add(Line(f.Label, number.Box, detail));
        }
        page.Children.Add(new WC.TextBlock { Text = L.Rules.OfficeHint, TextWrapping = W.TextWrapping.Wrap, Foreground = CardSkin.InkMuted, Margin = new W.Thickness(0, 10, 0, 0) });
        AddPage(L.Rules.OfficePage, page); Shortcuts.Children.Add(restore);
        restore.Click += () => RestoreClicked?.Invoke();
        auto.Checked += (_, _) => { if (!showing) AutoChanged?.Invoke(true); };
        auto.Unchecked += (_, _) => { if (!showing) AutoChanged?.Invoke(false); };
    }
    internal void SetAvailable(bool available) { restore.IsEnabled = auto.IsEnabled = available; }
    internal void Open(View view)
    {
        if (Office != view.Record.Id) StartOver();
        Office = view.Record.Id;
        foreach (var f in fields) f.Value.Show(view.Record.Value(f.Key));
        Follow(view);
    }
    internal void Follow(View view)
    {
        if (Office != view.Record.Id) return;
        Title.Text = $"{view.Record.Name} · #{Office}"; Subtitle.Text = L.Rules.OfficePage;
        showing = true; auto.IsChecked = view.Profile.AutoApply;
        foreach (OfficeField f in OfficeRules.Fields)
        {
            int now = view.Record.Value(f), baseline = view.Profile.Default(OfficeRules.Key(Office, f)), desired = view.Profile.Desired(OfficeRules.Key(Office, f));
            fields[f].Follow(now);
            string state = desired == baseline ? now == baseline ? L.Rules.Unchanged : L.Rules.Conflict
                : now == desired ? L.Rules.Applied : now == baseline ? L.Rules.Waiting : L.Rules.Conflict;
            details[f].Text = $"{L.Rules.Current} {now} · {L.Rules.Baseline} {baseline}\n{L.Rules.Setting} {desired} · {state}";
        }
        showing = false; Count();
    }
    internal RuleChange[]? Changes(out string error)
    {
        error = ""; var changes = new List<RuleChange>();
        foreach (var f in fields.Where(f => f.Value.IsChanged))
        {
            if (!f.Value.TryRead(out long value, out error)) return null;
            changes.Add(new(OfficeRules.Key(Office, f.Key), (int)f.Value.Current, (int)value));
        }
        return changes.ToArray();
    }
}

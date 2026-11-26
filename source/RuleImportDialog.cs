using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

internal sealed class RuleImportDialog : W.Window
{
    internal HashSet<RuleKey> TakeIncoming { get; } = [];

    internal RuleImportDialog(RuleImportPlan plan, Func<RuleKey, string> recordName)
    {
        Title = L.Compatibility.ImportRules;
        Width = 620; Height = 460; MinWidth = 440; MinHeight = 280;
        WindowStartupLocation = W.WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = CardSkin.Parchment; Foreground = CardSkin.Ink; FontFamily = CardSkin.Font;
        var panel = new WC.DockPanel { Margin = new W.Thickness(14) };
        Content = panel;
        var note = new WC.TextBlock { Text = L.Compatibility.ImportChoices(plan.Changes.Length, plan.Conflicts.Length),
            TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 0, 8) };
        WC.DockPanel.SetDock(note, WC.Dock.Top); panel.Children.Add(note);
        var buttons = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, HorizontalAlignment = W.HorizontalAlignment.Right,
            Margin = new W.Thickness(0, 8, 0, 0) };
        var save = new PlateButton(L.Compatibility.Import, primary: true);
        void UpdateSave() => save.IsEnabled = plan.Unavailable.Length == 0 || plan.Changes.Length > 0 || TakeIncoming.Count > 0;
        UpdateSave();
        var cancel = new PlateButton(L.Window.Cancel) { Margin = new W.Thickness(8, 0, 0, 0) };
        save.Click += () =>
        {
            try { plan.Resolve(TakeIncoming); DialogResult = true; }
            catch (InvalidDataException ex) { W.MessageBox.Show(this, ex.Message, Title); }
        };
        cancel.Click += () => DialogResult = false;
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        WC.DockPanel.SetDock(buttons, WC.Dock.Bottom); panel.Children.Add(buttons);
        var rows = new WC.ListBox();
        foreach (var key in plan.Unavailable)
            rows.Items.Add(new WC.TextBlock { Text = plan.Compatibility.RangeHeld(key)
                    ? L.EditRanges.ImportHold($"{Table(key)} #{key.Record} · {Label(key)}")
                    : L.FileComparison.SkippedField($"{Table(key)} #{key.Record}", Label(key)),
                TextWrapping = W.TextWrapping.Wrap });
        foreach (var conflict in plan.Conflicts)
        {
            var key = conflict.Key;
            var check = new WC.CheckBox { Content = L.Compatibility.ImportField($"{Table(key)} {recordName(key)}", key.Record, Label(key),
                conflict.Original, conflict.TargetDefault, conflict.Current, conflict.Incoming) };
            check.Checked += (_, _) => { TakeIncoming.Add(key); UpdateSave(); };
            check.Unchecked += (_, _) => { TakeIncoming.Remove(key); UpdateSave(); };
            rows.Items.Add(check);
        }
        panel.Children.Add(rows);
    }

    private static string Table(RuleKey key) => key.Table switch
    {
        "formation" => L.Rules.Formation, "tactic" => L.Rules.TacticPage, "policy" => L.Rules.PolicyPage,
        "policyeffect" => L.Rules.PolicyEffects, "office" => L.Rules.OfficePage, "rank" => L.Rules.RankPage, _ => L.Rules.DoctrinePage
    };
    private static string Label(RuleKey key) => key.Table switch
    {
        "formation" => FormationRules.Field(key.Field).Label, "tactic" => TacticRules.Field(key.Field) is { Slot: >= 0 } f ?
            $"{f.Label} {f.Slot + 1}" : TacticRules.Field(key.Field).Label,
        "office" => OfficeRules.Field(key.Field).Label, "rank" => RankRules.Spec.Field(key.Field).Label,
        "doctrine" => DoctrineRules.Spec.Field(key.Field).Label, _ => PolicyRules.Field(key).Label
    };
}

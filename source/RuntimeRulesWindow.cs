using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

internal sealed class RuntimeRulesWindow : W.Window
{
    private bool known;
    private readonly WC.TextBlock applied = new() { TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 6, 0, 14) };
    private readonly WC.TextBlock pending = new() { TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 8, 0, 0) };
    private RuntimeRuleSettings? current;
    private bool loading;
    private RuntimeRulePreference? saved;
    private readonly WC.TextBlock savedStatus = new() { TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 0, 12) };
    internal bool HasPendingChanges => (saved is not null || current is not null)
        && (Selection != (saved?.Settings ?? current) || ConvertProgress.IsChecked != (saved?.ConvertExperience ?? true));
    internal readonly WC.CheckBox Traits = new() { Content = L.GameSettingsUi.TraitsNine, Margin = new W.Thickness(0, 12, 0, 12) };
    internal readonly WC.CheckBox TraitList = new() { Content = L.GameSettingsUi.TraitListNine, Margin = new W.Thickness(0, 0, 0, 12) };
    internal readonly WC.ComboBox Ability = new() { ItemsSource = new[] { L.GameSettingsUi.AbilityOriginal, L.GameSettingsUi.AbilityCapTwoHundred, L.GameSettingsUi.AbilityGrowthTwoHundred }, MinWidth = 320 };
    internal readonly WC.CheckBox ConvertProgress = new() { Content = L.GameSettingsUi.ConvertProgress, IsChecked = true, Margin = new W.Thickness(0, 12, 0, 12) };
    internal readonly PlateButton Apply = new(L.GameSettingsUi.Apply, primary: true);
    internal readonly PlateButton Restore = new(L.GameSettingsUi.Restore);
    internal readonly PlateButton Import = new(L.Compatibility.Import);
    internal SettingsImportSource? ImportedSource;
    internal string? ImportTargetStamp;
    internal readonly WC.TextBlock Status = new() { TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 12, 0, 0) };
    internal RuntimeRuleSettings Selection => new(Traits.IsChecked == true, (AbilityRuleMode)Ability.SelectedIndex, TraitList.IsChecked == true);

    internal RuntimeRulesWindow(RuntimeRuleSettings? current)
    {
        Title = L.GameSettingsUi.RuntimeWindowTitle; Width = 560; MinWidth = 500; SizeToContent = W.SizeToContent.Height;
        MaxHeight = W.SystemParameters.WorkArea.Height;
        WindowStartupLocation = W.WindowStartupLocation.CenterOwner; ResizeMode = W.ResizeMode.CanResize;
        Background = CardSkin.Parchment; Foreground = CardSkin.Ink; FontFamily = CardSkin.Font; FontSize = CardSkin.FontSize;
        var panel = new WC.StackPanel { Margin = new W.Thickness(22) };
        ConvertProgress.Content = new WC.TextBlock { Text = (string)ConvertProgress.Content, TextWrapping = W.TextWrapping.Wrap };
        panel.Children.Add(new WC.TextBlock { Text = L.GameSettingsUi.AppliedHeading, FontSize = 18, FontWeight = W.FontWeights.Bold });
        panel.Children.Add(applied);
        panel.Children.Add(savedStatus);
        panel.Children.Add(new WC.TextBlock { Text = L.GameSettingsUi.SelectionHeading, FontWeight = W.FontWeights.Bold });
        panel.Children.Add(Traits); panel.Children.Add(TraitList); panel.Children.Add(Ability); panel.Children.Add(ConvertProgress);
        TraitList.Checked += (_, _) => Traits.IsChecked = true;
        Traits.Unchecked += (_, _) => TraitList.IsChecked = false;
        Traits.Checked += (_, _) => UpdateSelection(); Traits.Unchecked += (_, _) => UpdateSelection();
        TraitList.Checked += (_, _) => UpdateSelection(); TraitList.Unchecked += (_, _) => UpdateSelection();
        Ability.SelectionChanged += (_, _) => UpdateSelection();
        panel.Children.Add(new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap,
            Text = L.GameSettingsUi.PersistenceHelp });
        panel.Children.Add(new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap,
            Margin = new W.Thickness(0, 12, 0, 0), Text = L.Preparation.Help });
        var help = new WC.Expander { Header = L.GameSettingsUi.DetailedHelpHeading, Margin = new W.Thickness(0, 12, 0, 0),
            Content = new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 8, 0, 0),
            Text = L.GameSettingsUi.DetailedHelp($"{L.Window.Panel}", $"{L.Navigation.Data}") } };
        panel.Children.Add(help); panel.Children.Add(pending);
        var buttons = new WC.WrapPanel { Margin = new W.Thickness(0, 16, 0, 0) };
        Apply.Margin = new W.Thickness(0, 0, 10, 0); buttons.Children.Add(Apply); buttons.Children.Add(Restore);
        Import.Margin = new W.Thickness(10,0,0,0); buttons.Children.Add(Import);
        panel.Children.Add(buttons); panel.Children.Add(Status);
        Content = new WC.ScrollViewer { VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled, Content = panel };
        ShowSettings(current);
    }

    internal void ShowSettings(RuntimeRuleSettings? settings)
    {
        loading = true; current = settings;
        known = settings is not null;
        Traits.IsChecked = settings?.Traits9 ?? false; Ability.SelectedIndex = (int)(settings?.Abilities ?? AbilityRuleMode.Original);
        TraitList.IsChecked = settings?.TraitList9 ?? false;
        applied.Text = settings is null ? L.GameSettingsUi.StateUnknown : L.GameSettingsUi.AppliedSummary($"{settings.Slots}", $"{(settings.TraitList9 ? 9 : 5)}") + Ability.Items[(int)settings.Abilities];
        loading = false; UpdateSelection();
        Apply.IsEnabled = Restore.IsEnabled = Import.IsEnabled = settings is not null;
        Status.Text = settings is null ? L.GameSettingsUi.UnknownRules : L.GameSettingsUi.CurrentStateRead;
    }
    internal void ShowSaved(RuntimeRulePreference preference, bool select = false)
    {
        saved = preference;
        savedStatus.Text = L.GameSettingsUi.SavedSummary($"{preference.Settings.Slots}", $"{(preference.Settings.TraitList9 ? 9 : 5)}", $"{Ability.Items[(int)preference.Settings.Abilities]}");
        if (select)
        {
            loading = true;
            Traits.IsChecked = preference.Settings.Traits9; TraitList.IsChecked = preference.Settings.TraitList9;
            Ability.SelectedIndex = (int)preference.Settings.Abilities;
            ConvertProgress.IsChecked = preference.ConvertExperience;
            loading = false;
        }
        UpdateSelection();
    }
    private void UpdateSelection()
    {
        if (loading) return;
        pending.Text = current is null ? L.GameSettingsUi.SelectionUnknown
            : Selection == current ? L.GameSettingsUi.SelectionMatches : L.GameSettingsUi.SelectionPending;
    }
    internal void LoadImported(RuntimeRulePreference preference, SettingsImportSource source)
    {
        loading = true;
        Traits.IsChecked=preference.Settings.Traits9; TraitList.IsChecked=preference.Settings.TraitList9;
        Ability.SelectedIndex=(int)preference.Settings.Abilities; ConvertProgress.IsChecked=preference.ConvertExperience;
        loading=false; ImportedSource=source; UpdateSelection(); Status.Text=L.Compatibility.ImportDraft;
    }
    internal void SetBusy(bool busy)
    {
        Traits.IsEnabled = TraitList.IsEnabled = Ability.IsEnabled = ConvertProgress.IsEnabled = Apply.IsEnabled = Restore.IsEnabled = Import.IsEnabled = !busy && known;
    }
}

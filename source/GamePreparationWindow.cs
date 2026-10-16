using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

internal sealed class GamePreparationWindow : W.Window
{
    internal readonly WC.CheckBox Traits = new() { Content = L.GameSettingsUi.TraitsNine };
    internal readonly WC.CheckBox TraitList = new() { Content = L.GameSettingsUi.TraitListNine };
    internal readonly WC.ComboBox Ability = new() { ItemsSource = new[] { L.GameSettingsUi.AbilityOriginal, L.GameSettingsUi.AbilityCapTwoHundred, L.GameSettingsUi.AbilityGrowthTwoHundred } };
    internal readonly WC.CheckBox Convert = new() { Content = L.GameSettingsUi.ConvertProgress };
    internal readonly PlateButton Launch = new(L.Preparation.Launch, primary: true);
    internal readonly PlateButton Cancel = new(L.Preparation.Cancel) { IsEnabled = false };
    internal readonly WC.TextBlock Status = new() { TextWrapping = W.TextWrapping.Wrap };
    internal RuntimeRuleSettings Selection => new(Traits.IsChecked == true, (AbilityRuleMode)Ability.SelectedIndex, TraitList.IsChecked == true);

    internal GamePreparationWindow(RuntimeRulePreference? saved)
    {
        Title = L.Preparation.Menu; Width = 570; MinWidth = 500; SizeToContent = W.SizeToContent.Height;
        WindowStartupLocation = W.WindowStartupLocation.CenterOwner;
        Background = CardSkin.Parchment; Foreground = CardSkin.Ink; FontFamily = CardSkin.Font; FontSize = CardSkin.FontSize;
        var panel = new WC.StackPanel { Margin = new W.Thickness(22) };
        panel.Children.Add(new WC.TextBlock { Text = L.Preparation.Help, TextWrapping = W.TextWrapping.Wrap });
        foreach (W.FrameworkElement control in new W.FrameworkElement[] { Traits, TraitList, Ability, Convert, Launch, Cancel, Status })
        { control.Margin = new W.Thickness(0, 12, 0, 0); panel.Children.Add(control); }
        Convert.Content = new WC.TextBlock { Text = L.GameSettingsUi.ConvertProgress, TextWrapping = W.TextWrapping.Wrap };
        Traits.IsChecked = saved?.Settings.Traits9 ?? false;
        TraitList.IsChecked = saved?.Settings.TraitList9 ?? false;
        Ability.SelectedIndex = (int)(saved?.Settings.Abilities ?? AbilityRuleMode.Original);
        Convert.IsChecked = saved?.ConvertExperience ?? true;
        TraitList.Checked += (_, _) => Traits.IsChecked = true;
        Traits.Unchecked += (_, _) => TraitList.IsChecked = false;
        MaxHeight = W.SystemParameters.WorkArea.Height;
        Content = new WC.ScrollViewer { Content = panel, VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto };
    }

    internal void SetWorking(bool value)
    {
        Traits.IsEnabled = TraitList.IsEnabled = Ability.IsEnabled = Convert.IsEnabled = Launch.IsEnabled = !value;
        Cancel.IsEnabled = value;
    }
}

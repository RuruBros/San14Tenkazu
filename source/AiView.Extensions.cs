using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

internal sealed partial class AiView
{
    private readonly WC.WrapPanel extensionTabs = new();
    private readonly PaneTab basicTab = new(L.AiExtensions.Basic), extendedTab = new(L.AiExtensions.Extended);
    private readonly WC.ScrollViewer extensionPage = new() { VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled, Background = CardSkin.Paper };
    private readonly WC.CheckBox supplyProtection = new() { Content = L.AiExtensions.SupplyProtection, Margin = new W.Thickness(0, 18, 0, 6) };
    private readonly WC.ComboBox supplyFailure = EditorCard.Choice(double.NaN), extensionScope = EditorCard.Choice(double.NaN);
    private readonly WC.TextBlock extensionState = Muted();
    private readonly Func<AiExtensionSettings, Task<bool>>? writeExtensions;
    private AiExtensionSettings extensionSaved;
    private AiExtensionStatus? extensionRuntime;
    private string? extensionReadError;
    private bool extensionSelected;
    private AiExtensionSettings ExtensionDraft => new(supplyProtection.IsChecked == true,
        (AiSupplyFailure)Math.Max(0, supplyFailure.SelectedIndex), (AiExtensionScope)Math.Max(0, extensionScope.SelectedIndex));
    private bool ExtensionPending => ExtensionDraft != extensionSaved;

    private void BuildExtensions()
    {
        basicTab.Selected = true;
        extensionTabs.Children.Add(basicTab); extensionTabs.Children.Add(extendedTab);
        basicTab.Click += (_, _) => SelectExtensionTab(false);
        extendedTab.Click += (_, _) => SelectExtensionTab(true);
        var content = new WC.StackPanel { Margin = new W.Thickness(12, 10, 12, 16) };
        content.Children.Add(new WC.TextBlock { Text = L.AiExtensions.Extended, FontSize = CardSkin.FontSize + 3, FontWeight = W.FontWeights.Bold });
        content.Children.Add(Muted(L.AiExtensions.Summary));
        content.Children.Add(supplyProtection);
        content.Children.Add(Muted(L.AiExtensions.SupplyExplanation));
        content.Children.Add(Heading(L.AiExtensions.Failure));
        supplyFailure.Items.Add(L.AiExtensions.Return); supplyFailure.Items.Add(L.AiExtensions.Hold);
        content.Children.Add(supplyFailure);
        content.Children.Add(Heading(L.AiExtensions.Scope));
        extensionScope.Items.Add(L.AiExtensions.AiAndDelegated); extensionScope.Items.Add(L.AiExtensions.AiOnly);
        content.Children.Add(extensionScope);
        var note = Muted(L.AiExtensions.SavedNote); note.Margin = new W.Thickness(0, 16, 0, 10);
        content.Children.Add(note); content.Children.Add(extensionState);
        extensionPage.Content = content;
        supplyProtection.IsChecked = extensionSaved.SupplyProtection;
        supplyFailure.SelectedIndex = (int)extensionSaved.SupplyFailure;
        extensionScope.SelectedIndex = (int)extensionSaved.Scope;
        supplyProtection.Checked += (_, _) => UpdateExtensionActions();
        supplyProtection.Unchecked += (_, _) => UpdateExtensionActions();
        supplyFailure.SelectionChanged += (_, _) => UpdateExtensionActions();
        extensionScope.SelectionChanged += (_, _) => UpdateExtensionActions();
        UpdateExtensionActions(); ShowExtensionState();
    }

    private void SelectExtensionTab(bool extended)
    {
        if (writing) return;
        extensionSelected = extended;
        defaults.ToolTip = extended ? L.AiExtensions.DefaultsTip : L.Ai.DefaultsTip;
        basicTab.Selected = !extended; extendedTab.Selected = extended;
        ShowSettings();
        if (extended) ShowExtensionState();
    }

    private void UpdateExtensionActions()
    {
        supplyFailure.IsEnabled = extensionScope.IsEnabled = supplyProtection.IsChecked == true && !writing;
        if (!extensionSelected) { apply.IsEnabled = !writing && boxes.Values.Any(b => b.Box.Background == CardSkin.Pending); return; }
        apply.IsEnabled = !writing && writeExtensions is not null && game() is { Verified: true } && extensionRuntime is not null
            && (ExtensionPending || ExtensionDraft != extensionRuntime.Settings);
    }

    private void ShowExtensionState()
    {
        extensionState.Text = extensionRuntime is { } current
            ? (current.Settings.SupplyProtection ? L.AiExtensions.On : L.AiExtensions.Off) + "\n" +
                L.AiExtensions.Counts(current.Checks, current.RecoveryPlans, current.Returns, current.Holds)
            : extensionReadError is { } why ? L.AiExtensions.Failed(why) : L.Ai.LiveNotConnected;
    }

    private void ResetExtensions()
    {
        if (writing) return;
        supplyProtection.IsChecked = false;
        supplyFailure.SelectedIndex = (int)AiSupplyFailure.Return;
        extensionScope.SelectedIndex = (int)AiExtensionScope.AiAndDelegated;
        UpdateExtensionActions();
    }

    private async Task ApplyExtensionsAsync()
    {
        if (writing || writeExtensions is null) return;
        AiExtensionSettings settings = ExtensionDraft;
        writing = true;
        supplyProtection.IsEnabled = defaults.IsEnabled = reread.IsEnabled = basicTab.IsEnabled = extendedTab.IsEnabled = treeButton.IsEnabled = false;
        UpdateExtensionActions();
        try
        {
            if (await writeExtensions(settings))
            {
                extensionSaved = settings;
                await ReadGameAsync();
                status.Text = extensionRuntime?.Settings == settings ? L.AiExtensions.Applied : L.AiExtensions.NotApplied;
            }
        }
        finally
        {
            writing = false;
            supplyProtection.IsEnabled = defaults.IsEnabled = reread.IsEnabled = basicTab.IsEnabled = extendedTab.IsEnabled = treeButton.IsEnabled = true;
            UpdateExtensionActions();
        }
    }
}

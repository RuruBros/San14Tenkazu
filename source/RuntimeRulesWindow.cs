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
    internal readonly WC.CheckBox Traits = new() { Content = "개성 9개 발동", Margin = new W.Thickness(0, 12, 0, 12) };
    internal readonly WC.CheckBox TraitList = new() { Content = "게임 무장 목록에 개성 9개 표시", Margin = new W.Thickness(0, 0, 0, 12) };
    internal readonly WC.ComboBox Ability = new() { ItemsSource = new[] { "기본 상한 · 100 성장", "능력치 상한 200 · 100 성장", "능력치 상한 200 · 200 성장" }, MinWidth = 320 };
    internal readonly WC.CheckBox ConvertProgress = new() { Content = "성장 모드 변경 시 능력치와 진행률을 유지하도록 경험치 변환", IsChecked = true, Margin = new W.Thickness(0, 12, 0, 12) };
    internal readonly PlateButton Apply = new("저장 및 적용", primary: true);
    internal readonly PlateButton Restore = new("기본 규칙으로 복원");
    internal readonly PlateButton Import = new(L.Compatibility.Import);
    internal SettingsImportSource? ImportedSource;
    internal string? ImportTargetStamp;
    internal readonly WC.TextBlock Status = new() { TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 12, 0, 0) };
    internal RuntimeRuleSettings Selection => new(Traits.IsChecked == true, (AbilityRuleMode)Ability.SelectedIndex, TraitList.IsChecked == true);

    internal RuntimeRulesWindow(RuntimeRuleSettings? current)
    {
        Title = "게임 설정 · 개성·능력치"; Width = 560; MinWidth = 500; SizeToContent = W.SizeToContent.Height;
        MaxHeight = W.SystemParameters.WorkArea.Height;
        WindowStartupLocation = W.WindowStartupLocation.CenterOwner; ResizeMode = W.ResizeMode.CanResize;
        Background = CardSkin.Parchment; Foreground = CardSkin.Ink; FontFamily = CardSkin.Font; FontSize = CardSkin.FontSize;
        var panel = new WC.StackPanel { Margin = new W.Thickness(22) };
        ConvertProgress.Content = new WC.TextBlock { Text = (string)ConvertProgress.Content, TextWrapping = W.TextWrapping.Wrap };
        panel.Children.Add(new WC.TextBlock { Text = "현재 적용 상태", FontSize = 18, FontWeight = W.FontWeights.Bold });
        panel.Children.Add(applied);
        panel.Children.Add(savedStatus);
        panel.Children.Add(new WC.TextBlock { Text = "변경할 설정", FontWeight = W.FontWeights.Bold });
        panel.Children.Add(Traits); panel.Children.Add(TraitList); panel.Children.Add(Ability); panel.Children.Add(ConvertProgress);
        TraitList.Checked += (_, _) => Traits.IsChecked = true;
        Traits.Unchecked += (_, _) => TraitList.IsChecked = false;
        Traits.Checked += (_, _) => UpdateSelection(); Traits.Unchecked += (_, _) => UpdateSelection();
        TraitList.Checked += (_, _) => UpdateSelection(); TraitList.Unchecked += (_, _) => UpdateSelection();
        Ability.SelectionChanged += (_, _) => UpdateSelection();
        panel.Children.Add(new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap,
            Text = "설정을 저장하면 게임 연결·새로고침 때 적용 상태를 확인합니다. 이미 같은 상태면 유지하고, 다르면 일시정지 후 자동 적용합니다. 목록 표시를 변경하면 게임의 무장 목록·선택 창을 닫고 다시 여세요." });
        var help = new WC.Expander { Header = "적용 범위·경험치 변환·복원 안내", Margin = new W.Thickness(0, 12, 0, 0),
            Content = new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 8, 0, 0),
            Text = $"목록 표시 설정은 장수 상세 창의 개성 표시를 바꾸지 않습니다. 개성 데이터 편집은 {L.Window.Panel} → {L.Navigation.Data} → 무장·물품 → 개성에서 할 수 있습니다.\n\n상한 변경은 기본 능력치를 올리지 않습니다. 옥새의 매력 특례는 유지됩니다.\n\n경험치 변환은 현재 능력치와 다음 단계 진행률을 유지합니다. 이전 곡선으로 편집한 100 초과 장수가 있는 저장은 성장 모드를 끈 상태에서 불러온 뒤 적용하세요.\n\n편집기를 닫아도 적용한 규칙은 게임 종료까지 유지됩니다. 기본 규칙으로 복원해도 6~9번째 개성과 성장한 능력치·경험치를 삭제하지 않습니다. 기본 게임의 이후 처리에서는 100 초과 값이 제한될 수 있습니다." } };
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
        applied.Text = settings is null ? "확인할 수 없음" : $"개성 발동 {settings.Slots}개 · 게임 목록 {(settings.TraitList9 ? 9 : 5)}개\n" + Ability.Items[(int)settings.Abilities];
        loading = false; UpdateSelection();
        Apply.IsEnabled = Restore.IsEnabled = Import.IsEnabled = settings is not null;
        Status.Text = settings is null ? "규칙을 확인할 수 없습니다. 다른 패치와의 충돌을 확인하세요." : "현재 메모리 상태를 읽었습니다.";
    }
    internal void ShowSaved(RuntimeRulePreference preference, bool select = false)
    {
        saved = preference;
        savedStatus.Text = $"저장됨 · 연결·새로고침 때 자동 확인\n개성 {preference.Settings.Slots}개 · 목록 {(preference.Settings.TraitList9 ? 9 : 5)}개 · {Ability.Items[(int)preference.Settings.Abilities]}";
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
        pending.Text = current is null ? "현재 상태를 확인한 뒤 변경할 수 있습니다."
            : Selection == current ? "현재 적용 상태와 같습니다." : "아직 적용하지 않은 변경이 있습니다.";
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

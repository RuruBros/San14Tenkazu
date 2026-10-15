using W = System.Windows;
using WC = System.Windows.Controls;
using WP = System.Windows.Controls.Primitives;
using WI = System.Windows.Input;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private readonly PlateButton settingsButton = new("게임 설정 ▾") { ToolTip = "개성·능력치 규칙과 환경 설정" };
    private WP.Popup? settingsPopup;
    private PlateButton BuildSettingsMenu()
    {
        var button = settingsButton;
        var options = new WC.StackPanel();
        WI.KeyboardNavigation.SetTabNavigation(options, WI.KeyboardNavigationMode.Cycle);
        System.Windows.Documents.TextElement.SetFontFamily(options, CardSkin.Font);
        System.Windows.Documents.TextElement.SetFontSize(options, CardSkin.FontSize);
        System.Windows.Documents.TextElement.SetForeground(options, CardSkin.Ink);
        var popup = settingsPopup = new WP.Popup
        {
            PlacementTarget = button, Placement = WP.PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true,
            Child = new WC.Border { Background = CardSkin.Parchment, BorderBrush = CardSkin.Gold,
                BorderThickness = new W.Thickness(1), Padding = new W.Thickness(10), Child = options }
        };
        foreach (PlateButton option in new[] { BuildRuntimeRulesMenu(), BuildEnvironmentMenu() })
        {
            option.Margin = new W.Thickness(0, 3, 0, 3);
            options.Children.Add(option);
            option.Click += () => popup.IsOpen = false;
        }
        button.Click += () => { popup.IsOpen = !popup.IsOpen; };
        popup.Opened += (_, _) => options.MoveFocus(new WI.TraversalRequest(WI.FocusNavigationDirection.First));
        options.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != WI.Key.Escape) return;
            popup.IsOpen = false; button.Focus(); e.Handled = true;
        };
        Deactivated += (_, _) => popup.IsOpen = false;
        Closed += (_, _) => popup.IsOpen = false;
        return button;
    }
}

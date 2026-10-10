using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

internal sealed partial class OfficerEditorCard
{
    private readonly WC.StackPanel revivalLine = new();
    private readonly WC.ComboBox revivalMode = Choice(190), revivalBase = Choice(260);
    private readonly NumberField revivalDeath = new(OfficerFields.Death, 80);
    private readonly PlateButton revivalButton = new(L.Revival.Revive, icon: Icons.Revert);
    private readonly WC.TextBlock revivalError = Hint("");
    private readonly WC.Border revivalHeading = Heading(L.Revival.Title);
    private readonly WC.TextBlock revivalNote = Hint(L.Revival.ValuesNote);
    private GameSnapshot? revivalData;
    private IReadOnlyList<OfficerRevival.Destination> revivalTargets = [];
    private int revivalOfficer;
    internal event Action<OfficerRevival.Request>? RevivalClicked;
    internal bool RevivalEnabled { set => revivalLine.IsEnabled = value; }

    private W.UIElement RevivalPanel()
    {
        revivalLine.Visibility = W.Visibility.Collapsed;
        revivalLine.Children.Add(revivalHeading);
        revivalMode.ItemsSource = new[] { L.Revival.Serving, L.Revival.Free };
        revivalMode.SelectedIndex = 0;
        revivalLine.Children.Add(Line(L.Revival.Mode, revivalMode));
        revivalLine.Children.Add(Line(L.Officer.TargetBase, revivalBase));
        revivalLine.Children.Add(Line(L.Revival.DeathYear, revivalDeath.Box));
        revivalLine.Children.Add(revivalNote);
        revivalLine.Children.Add(Hint(L.Revival.UndoNote));
        revivalLine.Children.Add(revivalError);
        revivalButton.HorizontalAlignment = W.HorizontalAlignment.Left;
        revivalButton.Margin = new W.Thickness(0, 8, 0, 0);
        revivalLine.Children.Add(revivalButton);
        revivalMode.SelectionChanged += (_, _) => FillRevivalBases();
        revivalBase.SelectionChanged += (_, _) => CheckRevival();
        revivalDeath.Edited += () => CheckRevival();
        revivalButton.Click += () => { if (CheckRevival() is { } request) RevivalClicked?.Invoke(request); };
        return revivalLine;
    }

    internal void FollowRevival(GameSnapshot data, OfficerRecord officer, bool opening = false)
    {
        revivalData = data;
        bool appearance = officer.Status == 7;
        revivalLine.Visibility = officer.Status is 7 or 9 ? W.Visibility.Visible : W.Visibility.Collapsed;
        ((WC.TextBlock)revivalHeading.Child).Text = appearance ? L.Revival.AppearanceTitle : L.Revival.Title;
        revivalNote.Text = appearance ? L.Revival.AppearanceNote : L.Revival.ValuesNote;
        revivalButton.Text = appearance ? L.Revival.Appear : L.Revival.Revive;
        if (opening || revivalOfficer != officer.Id)
        {
            revivalOfficer = officer.Id;
            revivalMode.SelectedIndex = 0;
            revivalDeath.Show(officer[OfficerFields.Death]);
        }
        else revivalDeath.Follow(officer[OfficerFields.Death]);
        FillRevivalBases();
    }

    private void FillRevivalBases()
    {
        int selected = revivalBase.SelectedIndex is >= 0 and var index && index < revivalTargets.Count
            ? revivalTargets[index].Base : 0;
        if (revivalData is not { } data || revivalMode.SelectedIndex < 0) return;
        var targets = OfficerRevival.Targets(data, (RevivalMode)revivalMode.SelectedIndex);
        if (!targets.SequenceEqual(revivalTargets))
        {
            revivalTargets = targets;
            revivalBase.ItemsSource = targets.Select(b => b.Force > 0 ? b.Name + " · " + data.ForceName(b.Force) : b.Name).ToArray();
            int found = targets.ToList().FindIndex(b => b.Base == selected);
            revivalBase.SelectedIndex = found >= 0 ? found : targets.Count > 0 ? 0 : -1;
        }
        CheckRevival();
    }

    private OfficerRevival.Request? CheckRevival()
    {
        string error = "";
        OfficerRevival.Request? request = null;
        if (revivalData is { } data && revivalBase.SelectedIndex is >= 0 and var index && index < revivalTargets.Count)
        {
            if (!revivalDeath.IsValid) error = OfficerFields.Death.RangeMessage;
            else
            {
                request = new(revivalOfficer, (RevivalMode)revivalMode.SelectedIndex, revivalTargets[index].Base, (int)revivalDeath.Value);
                var plan = OfficerRevival.Plan(data, request);
                if (plan.Refused) { error = plan.Refusal; request = null; }
            }
        }
        else error = L.Transfers.PickBase;
        revivalError.Text = error;
        revivalButton.IsEnabled = request is not null;
        return request;
    }
}

using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

internal sealed partial class OfficerEditorCard
{
    private readonly WC.StackPanel statusChangeLine = new();
    private readonly WC.ComboBox statusChoice = Choice(170), statusTargetForce = Choice(260), statusTargetBase = Choice(260);
    private readonly WC.TextBlock statusForceLabel = Label(L.StatusChange.Force), statusDetention = Value();
    private readonly WC.TextBlock statusChangeNote = Hint(""), statusChangeError = Hint("");
    private readonly PlateButton statusChangeButton = new(L.StatusChange.Apply, icon: Icons.Details);
    private WC.DockPanel statusForceLine = null!, statusBaseLine = null!, statusDetentionLine = null!;
    private GameSnapshot? statusChangeData;
    private OfficerRecord? statusChangeOfficer;
    private IReadOnlyList<Transfers.ForceBases> statusPosts = [];
    private IReadOnlyList<OfficerCaptivity.Target> statusCaptors = [];
    private bool fillingStatus, statusModeTyped;
    internal event Action<OfficerStatusChanges.Request>? StatusChangeClicked;
    internal bool StatusChangeEnabled { set => statusChangeLine.IsEnabled = value; }

    private W.UIElement StatusChangePanel()
    {
        statusChangeLine.Visibility = W.Visibility.Collapsed;
        statusChangeLine.Children.Add(Heading(L.StatusChange.Title));
        statusChoice.ItemsSource = new[] { L.Terms.StatusRegular, L.Terms.StatusFree, L.Terms.StatusPrisoner };
        statusChangeLine.Children.Add(Line(L.StatusChange.Mode, statusChoice));
        statusForceLine = Line(statusForceLabel, statusTargetForce);
        statusTargetForce.DisplayMemberPath = "Name";
        statusTargetForce.SelectedValuePath = "Force";
        statusBaseLine = Line(L.Officer.TargetBase, statusTargetBase);
        statusDetentionLine = Line(L.StatusChange.Detention, statusDetention);
        statusChangeLine.Children.Add(statusForceLine);
        statusChangeLine.Children.Add(statusBaseLine);
        statusChangeLine.Children.Add(statusDetentionLine);
        statusChangeLine.Children.Add(statusChangeNote);
        statusChangeLine.Children.Add(statusChangeError);
        statusChangeButton.HorizontalAlignment = W.HorizontalAlignment.Left;
        statusChangeButton.Margin = new W.Thickness(0, 8, 0, 0);
        statusChangeLine.Children.Add(statusChangeButton);
        statusChoice.SelectionChanged += (_, _) =>
        {
            if (fillingStatus) return;
            statusModeTyped = statusChangeOfficer is { } officer && statusChoice.SelectedIndex != StatusMode(officer.Status);
            FillStatusTargets();
        };
        statusTargetForce.SelectionChanged += (_, _) => { if (!fillingStatus) FillStatusBases(); };
        statusTargetBase.SelectionChanged += (_, _) => { if (!fillingStatus) CheckStatusChange(); };
        statusChangeButton.Click += () => { if (CheckStatusChange() is { } request) StatusChangeClicked?.Invoke(request); };
        return statusChangeLine;
    }

    private static int StatusMode(int status) => status switch { 2 or 3 or 4 => 0, 5 => 1, 6 => 2, _ => -1 };

    internal void FollowStatusChange(GameSnapshot data, OfficerRecord officer, bool opening = false)
    {
        bool reset = opening || statusChangeOfficer?.Id != officer.Id;
        statusChangeData = data;
        statusChangeOfficer = officer;
        statusChangeLine.Visibility = StatusMode(officer.Status) >= 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        fillingStatus = true;
        if (reset || !statusModeTyped) statusChoice.SelectedIndex = StatusMode(officer.Status);
        if (reset || statusChoice.SelectedIndex == StatusMode(officer.Status)) statusModeTyped = false;
        fillingStatus = false;
        FillStatusTargets(reset);
    }

    private int SelectedStatusForce() => statusTargetForce.SelectedValue is int force ? force : 0;

    private void FillStatusTargets(bool reset = false)
    {
        if (statusChangeData is not { } data || statusChangeOfficer is not { } officer) return;
        int selected = reset ? 0 : SelectedStatusForce();
        bool prisoner = statusChoice.SelectedIndex == (int)OfficerStatusMode.Prisoner;
        bool free = statusChoice.SelectedIndex == (int)OfficerStatusMode.Free;
        statusPosts = Transfers.Targets(data);
        statusCaptors = OfficerCaptivity.Targets(data);
        var forces = prisoner ? statusCaptors.Select(t => (t.Force, t.Name)).ToArray()
            : statusPosts.Select(t => (t.Force, t.Name)).ToArray();
        int current = prisoner ? OfficerCaptivity.BaseForce(data, officer.BaseId) : data.ForceOfCorps(officer.Corps);
        int index = Array.FindIndex(forces, f => f.Force == selected);
        if (index < 0) index = Array.FindIndex(forces, f => f.Force == current);
        fillingStatus = true;
        statusTargetForce.ItemsSource = forces.Select(f => new { f.Force, f.Name }).ToArray();
        statusTargetForce.SelectedIndex = index >= 0 ? index : forces.Length > 0 ? 0 : -1;
        statusForceLabel.Text = prisoner ? L.StatusChange.Captor : L.StatusChange.Force;
        statusForceLine.Visibility = free ? W.Visibility.Collapsed : W.Visibility.Visible;
        statusBaseLine.Visibility = !free && !prisoner ? W.Visibility.Visible : W.Visibility.Collapsed;
        statusDetentionLine.Visibility = prisoner ? W.Visibility.Visible : W.Visibility.Collapsed;
        statusChangeNote.Text = prisoner ? L.StatusChange.CaptureNote + "\n" + L.StatusChange.UndoNote : "";
        fillingStatus = false;
        FillStatusBases(reset);
    }

    private void FillStatusBases(bool reset = false)
    {
        if (statusChangeOfficer is not { } officer) return;
        int selected = reset ? officer.BaseId : statusTargetBase.SelectedValue is int value ? value : officer.BaseId;
        fillingStatus = true;
        if (statusChoice.SelectedIndex == (int)OfficerStatusMode.Prisoner)
            statusDetention.Text = statusTargetForce.SelectedIndex is >= 0 and var i && i < statusCaptors.Count
                ? statusCaptors[i].BaseName : "-";
        else
        {
            var bases = statusTargetForce.SelectedIndex is >= 0 and var i && i < statusPosts.Count
                ? statusPosts[i].Bases : [];
            statusTargetBase.DisplayMemberPath = "Name";
            statusTargetBase.SelectedValuePath = "Base";
            statusTargetBase.ItemsSource = bases.Select(b => new { b.Base, b.Name }).ToArray();
            int index = bases.ToList().FindIndex(b => b.Base == selected);
            statusTargetBase.SelectedIndex = index >= 0 ? index : bases.Count > 0 ? 0 : -1;
        }
        fillingStatus = false;
        CheckStatusChange();
    }

    private OfficerStatusChanges.Request? CheckStatusChange()
    {
        string error = L.StatusChange.Unavailable;
        OfficerStatusChanges.Request? request = null;
        if (statusChangeData is { } data && statusChangeOfficer is { } officer && statusChoice.SelectedIndex >= 0)
        {
            request = new(officer.Id, (OfficerStatusMode)statusChoice.SelectedIndex, SelectedStatusForce(),
                statusTargetBase.SelectedValue is int value ? value : 0);
            var change = OfficerStatusChanges.Plan(data, request);
            error = change.Plan.Refused ? change.Plan.Refusal : "";
            if (change.Plan.Refused) request = null;
        }
        statusChangeError.Text = error;
        statusChangeButton.IsEnabled = request is not null;
        return request;
    }
}

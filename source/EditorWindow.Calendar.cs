namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private readonly PlateButton calendarButton = Tool(L.Calendar.Title, Icons.Calendar, L.Calendar.Tip);

    private void UpdateCalendarControls(bool connected)
    {
        bool supported = game is { } identity && GameMemory.SupportsCalendar(identity);
        calendarButton.IsEnabled = !busy && connected && supported && layout is { State: not 0 } &&
            snapshot is { Date: { } date, Hour: >= 0 and <= 23 } && new GameCalendarDate(date.Year, date.Month, date.Day).IsValid;
        calendarButton.ToolTip = game is not null && !supported ? L.Calendar.VerifiedOnly : L.Calendar.Tip;
    }

    private async Task ChangeCalendarAsync()
    {
        if (!CanWrite(out _) || game is not { } identity || !GameMemory.SupportsCalendar(identity) ||
            layout is not { State: not 0 } found) return;
        SetBusy(true, scanning: false);
        try
        {
            GameClock before = await Task.Run(() => GameMemory.ReadCalendar(identity, found));
            var dialog = new CalendarDialog(before.Date) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Target is not { } target) return;
            AddLog(L.Calendar.Requested(identity.ProcessId, $"0x{found.State:X}", before.Date, target, before.Hour));
            GameClock after = await RunNativeCommandAsync(NativeCommandKind.Calendar, identity, found, target,
                () => GameMemory.ChangeCalendar(identity, found, before, target));
            status.Text = L.Calendar.Completed(after.Date, after.Hour);
            AddLog(status.Text);
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            // The normal refresh merges records without dropping pending card inputs.
            SetBusy(false, scanning: false);
            await LiveRefreshAsync();
            UpdateControls();
        }
    }
}

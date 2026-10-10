namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private async Task ReviveOfficerAsync(OfficerRevival.Request request)
    {
        if (!CanWrite(out _) || request.Officer != officerEditor.OfficerId) return;
        if (officerEditor.Pending > 0 && !await LeaveAsync(officerEditor)) return;
        GameIdentity identity = game!.Value;
        GameLayout found = layout!;
        SetBusy(true, scanning: false);
        bool executionStarted = false;
        try
        {
            // Applying pending card edits can replace the old snapshot. Re-read before planning revival.
            GameSnapshot data = await Task.Run(() => GameMemory.ReadSnapshot(identity, found));
            PreparedRevival prepared = await Task.Run(() => GameMemory.PrepareRevival(identity, found, data, request));
            if (!ConfirmWrite(prepared.Plan.Summary)) return;
            AddLog(prepared.Plan.Summary);
            executionStarted = true;
            foreach (string line in await Task.Run(() => GameMemory.ApplyRevival(identity, found, data, request))) AddLog(line);
            status.Text = L.Revival.Completed;
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            // A dispatched native add can have completed even when the acknowledgement failed.
            if (executionStarted) undoHistory.Clear();
            try
            {
                snapshot = await Task.Run(() => GameMemory.ReadSnapshot(identity, found));
                map.SetSnapshot(snapshot);
                AfterWrite();
            }
            catch (Exception ex) { ReportError(ex); }
            SetBusy(false, scanning: false);
        }
    }
}

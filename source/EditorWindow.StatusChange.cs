namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private async Task ChangeOfficerStatusAsync(OfficerStatusChanges.Request request)
    {
        if (!CanWrite(out _) || request.Officer != officerEditor.OfficerId) return;
        if (officerEditor.Pending > 0 && !await LeaveAsync(officerEditor)) return;
        if (!CanWrite(out GameSnapshot current)) return;
        var change = OfficerStatusChanges.Plan(current, request);
        if (change.Plan.Refused) { status.Text = change.Plan.Refusal; Tell(change.Plan.Refusal); return; }
        if (change.Transfer is { } transfer) { await TransferAsync(transfer, request.Base); return; }
        if (change.Capture is not { } capture) return;
        GameIdentity identity = game!.Value;
        GameLayout found = layout!;
        SetBusy(true, scanning: false);
        bool executionStarted = false;
        try
        {
            GameSnapshot data = await Task.Run(() => GameMemory.ReadSnapshot(identity, found));
            PreparedCaptivity prepared = await Task.Run(() => GameMemory.PrepareCaptivity(identity, found, data, capture));
            if (!ConfirmWrite(prepared.Plan.Summary)) return;
            AddLog(prepared.Plan.Summary);
            executionStarted = true;
            foreach (string line in await Task.Run(() => GameMemory.ApplyCaptivity(identity, found, prepared))) AddLog(line);
            status.Text = L.StatusChange.Completed;
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
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

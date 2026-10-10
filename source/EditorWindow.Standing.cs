namespace San14Tenkazu;

// The officer card's [소속] page: the core plans the transfer (Transfers); here it is asked about, then written.
internal sealed partial class EditorWindow
{
    // Native succession and unit orders cannot be undone by restoring a few record fields.
    private async Task TransferAsync(Transfer kind, int target)
    {
        if (!CanWrite(out GameSnapshot data) || !data.OfficerById.TryGetValue(officerEditor.OfficerId, out OfficerRecord? officer)) return;
        if (officerEditor.Pending > 0 && !await LeaveAsync(officerEditor)) return;
        // Applying pending fields replaces the snapshot; plan the transfer against those current values.
        if (!CanWrite(out data) || !data.OfficerById.TryGetValue(officerEditor.OfficerId, out officer)) return;
        EditPlan plan = Transfers.Plan(data, officer, kind, target);
        if (plan.Refused)
        {
            status.Text = plan.Refusal;
            Tell(plan.Refusal);
            return;
        }
        if (!Transfers.RequiresNative(data, officer, kind))
        {
            if (ConfirmWrite(plan.Summary)) await WriteEditsAsync([.. plan.Edits]);
            return;
        }
        GameIdentity identity = game!.Value;
        GameLayout found = layout!;
        SetBusy(true, scanning: false);
        try
        {
            PreparedTransfer prepared = await Task.Run(() => GameMemory.PrepareTransfer(identity, found, data, officer, kind, target, plan));
            foreach (string line in prepared.Evidence) AddLog(line);
            if (!ConfirmWrite(plan.Summary)) return;
            AddLog(plan.Summary);
            foreach (string line in await Task.Run(() => GameMemory.ApplyTransfer(identity, found, data, officer, kind, target, plan))) AddLog(line);
            // Existing undo entries can refer to governors/policy slots changed by native succession.
            undoHistory.Clear();
            status.Text = L.Transfers.NativeCompleted;
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
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

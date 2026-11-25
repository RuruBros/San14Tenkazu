using W = System.Windows;

namespace San14Tenkazu;

// [일괄 부활]: the dialog draws the requests; each is then planned again on a fresh read and written by the same native
// add as the officer card's single revival, one officer at a time, stopping at the first failure or at [중지].
internal sealed partial class EditorWindow
{
    private const int ConfirmNames = 20;
    // Set while a batch runs: the toolbar's stop button asks it to end after the officer being written.
    private CancellationTokenSource? batchStop;

    private async Task ReviveOfficersAsync()
    {
        if (busy || layoutRecovering || game is not { } identity || layout is not { } found || snapshot is null) return;
        SetBusy(true, scanning: false);
        bool started = false;
        int completed = 0, total = 0;
        try
        {
            if (NativeWorldWriteBlock(identity) is { } blocked) throw new InvalidOperationException(blocked);
            if (await Task.Run(() => GameMemory.ReadPace(identity, found)) != GamePace.Paused)
                throw new InvalidOperationException(L.Transfers.PauseRequired);
            GameSnapshot data = await ReadBatchRevivalSnapshotAsync(identity, found);
            if (OfficerBatchRevival.Find(data, includeHidden: true).Officers.Count == 0) { Tell(L.BatchRevival.NoCandidates); return; }
            var dialog = new BatchRevivalDialog(data) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            IReadOnlyList<OfficerRevival.Request> requests = dialog.Requests;
            total = requests.Count;
            if (!ConfirmDiscard(() => Confirm(BatchRevivalConfirmation(data, requests), W.MessageBoxImage.Warning,
                defaultNo: true, title: L.BatchRevival.Button))) return;
            // Validate the whole selection before the first write, then plan each officer again on a fresh read.
            SetOperationProgress(L.BatchRevival.Checking(total), completed, total);
            GameSnapshot fresh = await ReadBatchRevivalSnapshotAsync(identity, found);
            foreach (var request in requests)
                if (OfficerRevival.Plan(fresh, request) is { Refused: true } refused)
                    throw new InvalidOperationException(L.BatchRevival.Refused(fresh.OfficerName(request.Officer), refused.Refusal));
            batchStop = new CancellationTokenSource();
            UpdateControls();
            foreach (var request in requests)
            {
                if (batchStop.IsCancellationRequested) break;
                SetOperationProgress(L.BatchRevival.Progress(completed, total, data.OfficerName(request.Officer)), completed, total);
                if (completed > 0) fresh = await ReadBatchRevivalSnapshotAsync(identity, found);
                GameSnapshot planned = fresh;
                foreach (string line in await RunNativeCommandAsync(NativeCommandKind.Revival, identity, found, request,
                    () => { started = true; return GameMemory.ApplyRevival(identity, found, planned, request); }, single: total == 1))
                    AddLog(line);
                completed++;
                SetOperationProgress(L.BatchRevival.Completed(completed, total), completed, total);
            }
            status.Text = completed < total ? L.BatchRevival.Stopped(completed, total) : L.BatchRevival.Done(completed);
            AddLog(status.Text);
        }
        catch (Exception ex)
        {
            ReportError(started ? new InvalidOperationException(L.BatchRevival.Failed(completed, total, ex.Message), ex) : ex);
        }
        finally
        {
            batchStop?.Dispose();
            batchStop = null;
            if (started)
            {
                // A dispatched native add can have completed even when its acknowledgement failed.
                undoHistory.Clear();
                try
                {
                    SetOperationProgress(L.BatchRevival.Refreshing(completed, total));
                    snapshot = await Task.Run(() => GameMemory.ReadSnapshot(identity, found));
                    map.SetSnapshot(snapshot);
                    AfterWrite();
                }
                catch (Exception readError) { ReportError(readError); }
            }
            SetBusy(false, scanning: false);
        }
    }

    private static async Task<GameSnapshot> ReadBatchRevivalSnapshotAsync(GameIdentity identity, GameLayout expected)
    {
        GameLayout current = await Task.Run(() => GameMemory.ReadCurrentLayout(identity));
        if (!expected.SameStorage(current)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        return await Task.Run(() => GameMemory.ReadSnapshot(identity, current));
    }

    // The count, the first names with where each goes, and what cannot be taken back.
    private static string BatchRevivalConfirmation(GameSnapshot data, IReadOnlyList<OfficerRevival.Request> requests)
    {
        string names = string.Join(" · ", requests.Take(ConfirmNames).Select(r => data.OfficerName(r.Officer)));
        if (requests.Count > ConfirmNames) names += " " + L.Fills.AndMore(requests.Count - ConfirmNames);
        return L.BatchRevival.Confirm(requests.Count, names) + "\n\n" + L.Revival.ValuesNote + "\n" + L.Revival.UndoNote +
            "\n\n" + L.BatchRevival.ConfirmGoOn;
    }
}

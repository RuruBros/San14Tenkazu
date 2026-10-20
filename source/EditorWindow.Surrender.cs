using W = System.Windows;

namespace San14Tenkazu;

// Each native surrender transfers the whole force and cannot be rolled back by restoring editor bytes.
internal sealed partial class EditorWindow
{
    private void WireSurrender()
    {
        forceEditor.SurrenderClicked += () => _ = SurrenderAsync(forceEditor.Force);
        batchSurrenderButton.Click += () => _ = SurrenderAsync(0, batch: true);
    }

    private async Task SurrenderAsync(int from, bool batch = false)
    {
        if (busy || game is not { } identity || layout is not { } found || snapshot is not { } data || !batch && from == 0) return;
        SetBusy(true, scanning: false);
        bool called = false;
        int completed = 0, total = 0, to = 0;
        try
        {
            if (WriteBlock([GameChecks.World]) is { } blocked) throw new InvalidOperationException(blocked);
            if (!batch && from == data.PlayerForce) { Tell(L.Surrender.PlayerForce); return; }
            if (await Task.Run(() => GameMemory.ReadPace(identity, found)) != GamePace.Paused)
                throw new InvalidOperationException(L.Surrender.PauseFirst);
            int[] sources;
            if (batch)
            {
                if (BaseGrants.Takers(data).Count(c => c.Force >= 1) < 2) { Tell(L.Surrender.NoSources); return; }
                var dialog = new SurrenderDialog(data) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                sources = dialog.Sources; to = dialog.Target;
            }
            else
            {
                if (ChooseSurrenderTarget(data, from) is not int target) return;
                sources = [from]; to = target;
            }
            List<ForceSurrenders.Request> requests = ForceSurrenders.Prepare(data, sources, to);
            total = requests.Count;
            string toName = data.ForceName(to);
            string confirmation = batch ? L.Surrender.BatchConfirm(total, string.Join("\n", sources.Select(data.ForceName)), toName)
                : L.Surrender.Confirm(data.ForceName(from), toName);
            if (!ConfirmDiscard() || !Confirm(confirmation, W.MessageBoxImage.Warning, defaultNo: true,
                title: batch ? L.Surrender.BatchButton : L.Surrender.Button)) return;
            // Validate the entire selection before the first call, then recheck each remaining ruler before dispatch.
            SetOperationProgress(L.Surrender.Checking(total), completed, total);
            GameSnapshot fresh = await ReadSurrenderSnapshotAsync(identity, found);
            foreach (var request in requests) ForceSurrenders.Validate(fresh, request);
            foreach (var request in requests)
            {
                SetOperationProgress(L.Surrender.Progress(completed, total, data.ForceName(request.From), toName), completed, total);
                if (completed > 0) fresh = await ReadSurrenderSnapshotAsync(identity, found);
                ForceSurrenders.Validate(fresh, request);
                AddLog(L.Surrender.Request(data.ForceName(request.From), toName));
                called = true;
                await Task.Run(() => GameMemory.SurrenderForce(identity, found, request));
                completed++;
                SetOperationProgress(L.Surrender.Completed(completed, total), completed, total);
                AddLog(L.Surrender.Done(data.ForceName(request.From), toName));
            }
            status.Text = batch ? L.Surrender.BatchDone(completed, toName) : L.Surrender.Done(data.ForceName(from), toName);
            if (batch) AddLog(status.Text);
        }
        catch (Exception ex)
        {
            ReportError(batch && called ? new InvalidOperationException(L.Surrender.BatchStopped(completed, total, ex.Message), ex) : ex);
        }
        finally
        {
            if (called)
            {
                undoHistory.RemoveAll(h => h.Rule is null);
                // Even the failed native call may have changed the world. Refresh without retrying or rolling back.
                try
                {
                    SetOperationProgress(L.Surrender.Refreshing(completed, total));
                    layout = await Task.Run(() => GameMemory.ReadCurrentLayout(identity));
                    snapshot = await Task.Run(() => GameMemory.ReadSnapshot(identity, layout));
                    map.SetSnapshot(snapshot);
                    lists.TableDrafts.Clear(); aiView?.DiscardDrafts();
                    FillLists();
                    if (!Show(new ForceTarget(to))) map.HideEditors();
                }
                catch (Exception readError) { ReportError(readError); }
            }
            SetBusy(false, scanning: false);
        }
    }

    private static async Task<GameSnapshot> ReadSurrenderSnapshotAsync(GameIdentity identity, GameLayout expected)
    {
        GameLayout current = await Task.Run(() => GameMemory.ReadCurrentLayout(identity));
        if (!expected.SameStorage(current)) throw new InvalidOperationException(L.Surrender.Changed);
        return await Task.Run(() => GameMemory.ReadSnapshot(identity, current));
    }

    private int? ChooseSurrenderTarget(GameSnapshot data, int from)
    {
        var choices = BaseGrants.Takers(data).Where(c => c.Force >= 1 && c.Force != from).ToList();
        if (choices.Count == 0)
        {
            Tell(L.Surrender.NoTarget);
            return null;
        }
        List<string> items = choices.Select(c => L.Auto.Choice(data.ForceName(c.Force), c.Cities) +
            (c.Force == data.PlayerForce ? " " + L.Auto.CurrentPlayer : "")).ToList();
        int player = choices.FindIndex(c => c.Force == data.PlayerForce);
        return PickFromList(L.Surrender.Button, L.Surrender.PickTarget(data.ForceName(from)), L.Surrender.Button, items, Math.Max(player, 0))
            is int picked ? choices[picked].Force : null;
    }
}

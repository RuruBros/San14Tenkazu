namespace San14Tenkazu;

// Writing to the game: what waits to be written, the write itself and its undo.
internal sealed partial class EditorWindow
{
    // Typed values wait in the open editor card; reading the game again or leaving drops them, after asking.
    private bool HasPendingEdits => OpenEditor is { Pending: > 0 } || lists.Pending > 0 || aiView?.Pending == true;
    private bool ConfirmDiscard() =>
        !HasPendingEdits || Confirm(L.Apply.DiscardAndGoOn);

    // Writes the edits, reads the game back and reports what the game kept; true when the write went through.
    private async Task<bool> WriteAsync(List<RecordEdit> edits, bool undoable = true)
    {
        if (WriteBlock(edits.SelectMany(e => e.Changes).Select(c => GameChecks.GroupOf(c.Field))) is { } blocked)
        {
            ReportError(new InvalidOperationException(blocked));
            return false;
        }
        // The write must leave the records tied as the game keeps them (a 태수 in his city, spouses both ways, ...).
        if (snapshot is { } now && GameChecks.Broken(now, edits, catalog) is { Count: > 0 } broken)
        {
            ReportError(new InvalidOperationException(L.Apply.BreaksTies(string.Join("\n", broken))));
            return false;
        }
        GameIdentity identity = game!.Value;
        GameLayout found = layout!;
        SetBusy(true, scanning: false);
        SetOperationProgress(L.OperationProgress.Checking);
        try
        {
            // Linked values wait for [정지]; plain ones go in while the game runs (GameChecks.PaceBlock). Telling the
            // pace watches the clock for up to 0.8 s (all of it while paused), so only a linked value waits for it.
            bool linked = edits.Any(e => e.Changes.Any(c => LinkedFields.Contains(c.Field)));
            GamePace pace = linked ? await Task.Run(() => GameMemory.ReadPace(identity, found)) : GamePace.Unknown;
            if (GameChecks.PaceBlock(pace, edits.SelectMany(e => e.Changes)) is { } running)
            {
                ReportError(new InvalidOperationException(running));
                return false;
            }
            status.Text = L.Apply.Writing;
            SetOperationProgress(L.Apply.Writing);
            if (!linked && snapshot?.Pace == GamePace.Running) AddLog(L.Apply.WritingWhileRunning);
            foreach (RecordEdit edit in edits)
                AddLog(L.Apply.Request(edit.Label, string.Join(", ", edit.Changes.Select(c => $"{c.Field.Label} {c.Field.Read(edit.Expected):N0} → {c.Value:N0}"))));
            WarnAboveCaps(edits);
            _ = GameMemory.AgreementPairs(found.Forces, edits);

            IReadOnlyList<string> overwritten = await Task.Run(() => GameMemory.Apply(identity, edits));
            foreach (string item in overwritten) AddLog("  " + L.Apply.WroteOverGame(item));
            if (undoable) RememberUndo(edits);
            SetOperationProgress(L.OperationProgress.Synchronizing);
            foreach (string line in await Task.Run(() => GameMemory.SyncForceAgreements(identity, found, edits))) AddLog("  " + line);
            // A 명품 counts for the game's rules only on its list of held ones, which the game itself puts right.
            foreach (string line in await Task.Run(() => GameMemory.SyncOwnedItems(identity, edits))) AddLog("  " + line);
            SetOperationProgress(L.OperationProgress.Reading);
            await Task.Delay(300);
            GameSnapshot data = await Task.Run(() => GameMemory.ReadSnapshot(identity, found));
            snapshot = data;
            map.SetSnapshot(data);

            // The game keeps running, so report any value it changed again right after the write.
            var drift = new List<string>();
            foreach (RecordEdit edit in edits)
            {
                byte[]? fresh = data.DataAt(edit.Kind, edit.Address);
                foreach (FieldChange change in edit.Changes)
                    if (fresh is not null && change.Field.Read(fresh) is var actual && actual != change.Value)
                        drift.Add($"{edit.Label} {change.Field.Label} {change.Value:N0} → {actual:N0}");
            }

            int total = edits.Sum(e => e.Changes.Count);
            status.Text = drift.Count == 0
                ? L.Apply.Written(total)
                : L.Apply.WrittenButChanged;
            AddLog(status.Text);
            foreach (string item in drift) AddLog("  " + L.Apply.GameChangedBack(item));
            return true;
        }
        catch (Exception ex) when (ex is StaleEditException or WriteRecoveryException)
        {
            // A failed recovery can leave part of the change in memory. Show the freshly read state and
            // preserve its full diagnostic; only a verified stale rollback gets the "all undone" message.
            try
            {
                SetOperationProgress(L.OperationProgress.Reading);
                snapshot = await Task.Run(() => GameMemory.ReadSnapshot(identity, found));
                map.SetSnapshot(snapshot);
                MergeSnapshot();
            }
            catch (Exception) { /* The regular refresh reads it again. */ }
            ReportError(ex is StaleEditException ? new InvalidOperationException(L.Apply.LinkedMoved(ex.Message)) : ex);
            return false;
        }
        catch (Exception ex)
        {
            ReportError(ex);
            return false;
        }
        finally { SetBusy(false, scanning: false); }
    }

    private void WarnAboveCaps(List<RecordEdit> edits)
    {
        foreach (RecordEdit edit in edits.Where(e => e.Kind == RecordKind.City))
        {
            long scale = CityFields.Scale.Read(edit.Expected);
            foreach (FieldChange change in edit.Changes)
                if (change.Field == CityFields.Scale) scale = change.Value;
            if (CityFields.Caps(scale) is not { } caps) continue;
            foreach (FieldChange change in edit.Changes)
            {
                long? cap = change.Field == CityFields.Gold ? caps.Gold : change.Field == CityFields.Food ? caps.Food
                    : change.Field == CityFields.Troops ? caps.Troops : null;
                if (cap is not null && change.Value > cap)
                    AddLog("  " + L.Apply.AboveCap(change.Field.Label, change.Value.ToString("N0"), cap));
            }
        }
    }

    // ---- 되돌리기 ----------------------------------------------------------------------------------------

    // The values a write replaced (Undo.Of), newest last.
    private void RememberUndo(List<RecordEdit> edits)
    {
        List<RecordEdit> undo = Undo.Of(edits);
        if (undo.Count == 0) return;
        string summary = string.Join(", ", edits.SelectMany(e => e.Changes.Select(c => $"{e.Label} {c.Field.Label} {c.Value:N0} → {c.Field.Read(e.Expected):N0}")));
        undoHistory.Add(new(summary, undo));
        if (undoHistory.Count > UndoLimit) undoHistory.RemoveAt(0);
        UpdateControls();
    }

    private async Task UndoAsync()
    {
        if (busy || layoutRecovering || game is null || layout is null || snapshot is null || undoHistory.Count == 0) return;
        if (undoHistory[^1].Rule is not null) { await UndoRuleAsync(); return; }
        (string summary, List<RecordEdit> undo) = undoHistory[^1];
        // The records as they are now against what the write left (Undo.Compare).
        // An AI write's records are not in the snapshot: they are read from the game.
        Undo.Check check = Undo.Compare(undo, edit => IsAi(edit) ? GameMemory.ReadAiRecord(game.Value, edit) : snapshot.DataAt(edit.Kind, edit.Address));
        foreach (string label in check.Missing) AddLog(L.Undo.Missing(label));
        List<string> linkedMoved = check.LinkedMoved;
        var moved = check.Moved;
        List<RecordEdit> edits = check.Edits;
        if (linkedMoved.Count > 0)
        {
            AddLog(L.Undo.StoppedLinked(string.Join(", ", linkedMoved)));
            if (Confirm(L.Undo.LinkedMoved(string.Join("\n", linkedMoved.Take(12)) + (linkedMoved.Count > 12 ? "\n" + L.Undo.AndMore(linkedMoved.Count - 12) : ""),
                    summary.Split(", ")[0]),
                    System.Windows.MessageBoxImage.Warning) && undoHistory.Count > 0 && undoHistory[^1].Edits == undo)
            {
                undoHistory.RemoveAt(undoHistory.Count - 1);
                AddLog(L.Undo.Dropped(summary));
            }
            UpdateControls();
            return;
        }
        if (moved.Count > 0)
        {
            bool? answer = Choose(L.Undo.Moved(string.Join("\n", moved.Take(12).Select(m => m.Note)) + (moved.Count > 12 ? "\n" + L.Undo.AndMore(moved.Count - 12) : "")));
            if (answer is null) return;
            if (answer == false) edits = check.KeepingMoved();
        }
        else if (!Confirm(L.Undo.Ask(summary.Replace(", ", "\n")))) return;
        AddLog(L.Undo.Undoing(summary));
        bool written = edits.Count > 0 && await WriteAsync(edits, undoable: false);
        // The entry goes once it is written back, or when nothing of it is left to write: a write that failed
        // can be tried again.
        if ((written || edits.Count == 0) && undoHistory.Count > 0 && undoHistory[^1].Edits == undo) undoHistory.RemoveAt(undoHistory.Count - 1);
        if (written) AfterWrite();
        if (written && edits.Any(IsAi)) await KeepAiAsync();
        UpdateControls();
    }
}

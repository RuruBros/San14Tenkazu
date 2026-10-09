using W = System.Windows;

namespace San14Tenkazu;

// The city and gate cards' [소속 주기]: a base no force holds goes to a force the user picks, the player's by default,
// through the game's own 거점 소유자 변경 (GameMemory.GiveBase). The game hands the base over as a capture does, so the
// values it changed on the way are written back (BaseGrants.KeptValues) and only the owner changes. The game's change
// is not undone as the editor's writes are.
internal sealed partial class EditorWindow
{
    private void WireGive()
    {
        cityEditor.GiveClicked += () => _ = GiveAsync(cityEditor.City);
        gateEditor.GiveClicked += () => _ = GiveAsync(gateEditor.Gate);
    }

    private async Task GiveAsync(int baseId)
    {
        if (busy || game is not { } identity || layout is not { } found || snapshot is not { } data || baseId == 0) return;
        if (WriteBlock([GameChecks.World]) is { } blocked)
        {
            ReportError(new InvalidOperationException(blocked));
            return;
        }
        string place = CityNames.BaseName(baseId);
        if (BaseGrants.ForceOf(data, baseId) is > 0 and var owner)
        {
            Tell(L.Give.HasForce(place, data.ForceName(owner)));
            return;
        }
        if (await Task.Run(() => GameMemory.ReadPace(identity, found)) == GamePace.Running)
        {
            ReportError(new InvalidOperationException(L.Give.PauseFirst));
            return;
        }
        if (ChooseTaker(data, place) is not int force) return;
        string forceName = data.ForceName(force);
        if (!ConfirmDiscard() || !Confirm(L.Give.Confirm(place, forceName), W.MessageBoxImage.Warning, defaultNo: true, title: L.Give.Button)) return;
        EditPlan kept;
        SetBusy(true, scanning: false);
        try
        {
            AddLog(L.Give.Request(place, forceName));
            // The values as the game holds them just before: the ones written back afterwards.
            GameSnapshot before = await Task.Run(() => GameMemory.ReadSnapshot(identity, found));
            if (BaseGrants.Refusal(before, baseId, force) is { } refused) throw new InvalidOperationException(refused);
            OfficerRecord ruler = BaseGrants.Ruler(before, force)!;
            ulong address = BaseGrants.Address(before, baseId);
            await Task.Run(() => GameMemory.GiveBase(identity, baseId, address, ruler.Id, ruler.Address));
            GameSnapshot after = await Task.Run(() => GameMemory.ReadSnapshot(identity, found));
            snapshot = after;
            map.SetSnapshot(after);
            if (BaseGrants.ForceOf(after, baseId) != force) throw new InvalidOperationException(L.Give.Unconfirmed(place));
            kept = BaseGrants.KeptValues(before, after, baseId);
        }
        catch (Exception ex)
        {
            ReportError(ex);
            AfterWrite();
            return;
        }
        finally { SetBusy(false, scanning: false); }
        List<RecordEdit> back = [.. kept.Edits.Where(e => e.Changes.Count > 0)];
        if (back.Count > 0)
        {
            AddLog(L.Give.KeepValues);
            await WriteAsync(back, undoable: false);
        }
        AfterWrite();
        status.Text = L.Give.Done(place, forceName);
        AddLog(status.Text);
    }

    // The forces that can take the base (with a ruler, not tribes), biggest first; the player's is picked.
    private int? ChooseTaker(GameSnapshot data, string place)
    {
        List<BaseGrants.Taker> takers = BaseGrants.Takers(data);
        if (takers.Count == 0)
        {
            Tell(L.Give.NoTarget);
            return null;
        }
        List<string> items = takers.Select(t => L.Auto.Choice(data.ForceName(t.Force), t.Cities) +
            (t.Force == data.PlayerForce ? " " + L.Auto.CurrentPlayer : "")).ToList();
        int player = takers.FindIndex(t => t.Force == data.PlayerForce);
        return PickFromList(L.Give.Button, L.Give.PickTarget(place), L.Give.Button, items, Math.Max(player, 0))
            is int picked ? takers[picked].Force : null;
    }
}

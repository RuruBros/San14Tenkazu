using W = System.Windows;

namespace San14Tenkazu;

// The force card's [항복시키기]: the game's own 세력 항복 (GameMemory.SurrenderForce) for the force on the card, to a
// force the user picks, the player's by default. The game moves the cities, officers and units itself, so it is not
// undone as the editor's writes are.
internal sealed partial class EditorWindow
{
    private void WireSurrender() => forceEditor.SurrenderClicked += () => _ = SurrenderAsync(forceEditor.Force);

    private async Task SurrenderAsync(int from)
    {
        if (busy || game is not { } identity || layout is not { } found || snapshot is not { } data || from == 0) return;
        if (WriteBlock([GameChecks.World]) is { } blocked)
        {
            ReportError(new InvalidOperationException(blocked));
            return;
        }
        if (from == data.PlayerForce)
        {
            Tell(L.Surrender.PlayerForce);
            return;
        }
        if (await Task.Run(() => GameMemory.ReadPace(identity, found)) == GamePace.Running)
        {
            ReportError(new InvalidOperationException(L.Surrender.PauseFirst));
            return;
        }
        if (ChooseSurrenderTarget(data, from) is not int to) return;
        string fromName = data.ForceName(from), toName = data.ForceName(to);
        if (!ConfirmDiscard() || !Confirm(L.Surrender.Confirm(fromName, toName), W.MessageBoxImage.Warning, defaultNo: true, title: L.Surrender.Button)) return;
        SetBusy(true, scanning: false);
        try
        {
            AddLog(L.Surrender.Request(fromName, toName));
            await Task.Run(() => GameMemory.SurrenderForce(identity, found, from, to));
            status.Text = L.Surrender.Done(fromName, toName);
            AddLog(status.Text);
            snapshot = await Task.Run(() => GameMemory.ReadSnapshot(identity, found));
            map.SetSnapshot(snapshot);
            FillLists();
            // The card of the force that surrendered shows the one that took it in.
            if (!Show(new ForceTarget(to))) map.HideEditors();
        }
        catch (Exception ex) { ReportError(ex); }
        finally { SetBusy(false, scanning: false); }
    }

    // The forces that can take the force in (with a ruler, not tribes), biggest first; the player's is picked.
    private int? ChooseSurrenderTarget(GameSnapshot data, int from)
    {
        var choices = data.Forces.Values
            .Where(f => f.Id != from && f.Id <= GameMemory.MaxPlayerForce && f.RulerId is not (0 or 0xFFFF))
            .Select(f => (Force: f, Cities: data.CitiesOf(f.Id).Count()))
            .OrderByDescending(c => c.Cities).ThenBy(c => c.Force.Id)
            .ToList();
        if (choices.Count == 0)
        {
            Tell(L.Surrender.NoTarget);
            return null;
        }
        List<string> items = choices.Select(c => L.Auto.Choice(data.ForceName(c.Force.Id), c.Cities) +
            (c.Force.Id == data.PlayerForce ? " " + L.Auto.CurrentPlayer : "")).ToList();
        int player = choices.FindIndex(c => c.Force.Id == data.PlayerForce);
        return PickFromList(L.Surrender.Button, L.Surrender.PickTarget(data.ForceName(from)), L.Surrender.Button, items, Math.Max(player, 0))
            is int picked ? choices[picked].Force.Id : null;
    }
}

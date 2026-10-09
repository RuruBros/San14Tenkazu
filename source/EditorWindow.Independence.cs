using W = System.Windows;

namespace San14Tenkazu;

internal sealed partial class EditorWindow
{
    private void WireIndependence() => cityEditor.IndependenceClicked += () => _ = MakeCityIndependentAsync(cityEditor.City);

    private async Task MakeCityIndependentAsync(int city)
    {
        if (busy || game is not { } identity || layout is not { } found || snapshot is not { } data || city == 0) return;
        SetBusy(true, scanning: false);
        bool called = false;
        try
        {
            if (WriteBlock([GameChecks.World]) is { } blocked) throw new InvalidOperationException(blocked);
            if (!identity.Verified) throw new InvalidOperationException(L.Independence.NotRun(L.Calls.WhyUnverified));
            if (await Task.Run(() => GameMemory.ReadPace(identity, found)) != GamePace.Paused)
                throw new InvalidOperationException(L.Independence.PauseFirst);
            // Preview the applied governor, not an uncommitted dropdown selection.
            GameSnapshot before = await Task.Run(() => GameMemory.ReadSnapshot(identity, found));
            CityIndependence.Request request = CityIndependence.Prepare(before, city);
            if (before.Cities[city].Governor != data.Cities[city].Governor || before.ForceOfCity(city) != data.ForceOfCity(city))
                throw new InvalidOperationException(L.Independence.Changed);
            string place = CityNames.ById[city], governor = before.OfficerName(request.Governor);
            if (!ConfirmDiscard() || !Confirm(L.Independence.Confirm(place, governor), W.MessageBoxImage.Warning,
                defaultNo: true, title: L.Independence.Button)) return;
            AddLog(L.Independence.Request(place, governor, before.ForceName(request.FromForce)));
            called = true;
            GameSnapshot after = await Task.Run(() => GameMemory.MakeCityIndependent(identity, found, request));
            snapshot = after;
            map.SetSnapshot(after);
            AfterWrite();
            status.Text = L.Independence.Done(place, governor, after.ForceName(request.NewForce));
            AddLog(status.Text);
            AddLog(L.Independence.Verified(request.NewForce, after.Cities[city].Corps, request.Governor,
                after.OfficersOf(request.NewForce).Count()));
        }
        catch (Exception ex)
        {
            ReportError(ex);
            if (called)
            {
                // A native event can partially change the world before failing.
                // Display its actual state and never retry or write old bytes back.
                try { snapshot = await Task.Run(() => GameMemory.ReadSnapshot(identity, found)); map.SetSnapshot(snapshot); AfterWrite(); }
                catch (Exception readError) { ReportError(readError); }
            }
        }
        finally
        {
            if (called) undoHistory.RemoveAll(h => h.Rule is null);
            SetBusy(false, scanning: false);
        }
    }
}

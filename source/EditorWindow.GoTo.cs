namespace San14Tenkazu;

// The cards' [위치로 가기] and a map right click: the game's camera flies to the chosen hex, as the game's lists send
// it (GameMemory.ShowTileInGame). Nothing is written to the game's data, so there is nothing to undo.
internal sealed partial class EditorWindow
{
    private void WireGoTo()
    {
        foreach (EditorCard card in Editors) card.GoToClicked += () => _ = GoToAsync(card);
        map.TileRightClicked += tile => _ = GoToTileAsync(tile, L.Map.ClickedPosition);
    }

    private async Task GoToAsync(EditorCard card)
    {
        if (busy || game is null || snapshot is not { } data || card.Target is not { } target) return;
        if (PlaceOf(data, target) is not ({ } tile, { } place))
        {
            status.Text = L.Card.NoPlace;
            return;
        }
        await GoToTileAsync(tile, place);
    }

    private async Task GoToTileAsync(int tile, string place)
    {
        if (busy || game is not { } identity || snapshot is null) return;
        // One game call at a time (writes call the game too): busy for the moment it takes.
        SetBusy(true, scanning: false);
        try
        {
            string? why = await Task.Run(() => GameMemory.ShowTileInGame(identity, tile));
            status.Text = why is null ? L.Card.WentTo(place) : L.Card.NotWent(why);
            if (why is not null) AddLog(status.Text);
        }
        catch (Exception ex) { ReportError(ex); }
        finally { SetBusy(false, scanning: false); }
    }

    // The hex a card's city, gate or unit stands on and its name; nulls when it has none on the map now.
    private static (int? Tile, string? Name) PlaceOf(GameSnapshot data, EditorTarget target) => target switch
    {
        CityTarget(int city) when city is >= 1 and <= GameMemory.CityCount => (MapTerrain.CityTiles[city], CityNames.ById[city]),
        GateTarget(int id) when data.Gates.FirstOrDefault(g => g.Id == id) is { Tile: >= 0 and < GameMemory.TileCount } gate => (gate.Tile, gate.Name),
        FacilityTarget facility when facility.Find(data) is { Tile: >= 0 and < GameMemory.TileCount } found => (found.Tile, MapArt.FacilityName(found.Kind)),
        UnitTarget(int slot, int commander) when data.Unit(slot, commander) is { Tile: < GameMemory.TileCount } unit =>
            (unit.Tile, L.Terms.UnitOf(data.CommanderName(commander))),
        _ => (null, null)
    };
}

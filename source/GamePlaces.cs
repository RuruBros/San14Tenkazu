namespace San14Tenkazu;

// Where things are on the map, for the editor's cards and the map: these need the map's city hexes
// (MapTerrain.CityTiles) and facility names (MapArt), so they live beside the map rather than in the core.
internal static class GamePlaces
{
    // The hex a unit is marching to, null when it stands still or the target is not understood.
    internal static int? Destination(this GameSnapshot data, UnitRecord unit)
    {
        int? tile = unit.TargetKind switch
        {
            UnitFields.BaseTarget => data.BaseTile(unit.Target),
            UnitFields.TileTarget when unit.Target < GameMemory.TileCount => unit.Target,
            UnitFields.UnitTarget => data.TargetUnit(unit)?.Tile,
            UnitFields.BuildingTarget => data.TargetFacility(unit)?.Tile,
            _ => null
        };
        return tile == unit.Tile ? null : tile;
    }

    // What the unit is going to, for its hover card: a base, a place, or another unit.
    internal static string DestinationName(this GameSnapshot data, UnitRecord unit, int destination) =>
        data.TargetUnit(unit) is { } other ? L.Terms.UnitOf(data.CommanderName(other.Commander))
        : data.TargetFacility(unit) is { } facility ? $"{MapArt.FacilityName(facility.Kind)} ({data.TileName(destination)})"
        : data.TileName(destination);

    // A base's hex: a city's centre, a tribe's castle or a gate.
    internal static int? BaseTile(this GameSnapshot data, int id) => id switch
    {
        _ when data.CityTile(id) is { } tile => tile,
        >= GameMemory.TribeFirst and <= GameMemory.TribeLast when data.CastleByCity.TryGetValue(id, out BuildingRecord? castle) => castle.Tile,
        >= GameMemory.GateFirst and <= GameMemory.GateLast when data.Gates.FirstOrDefault(g => g.Id == id) is { Tile: >= 0 } gate => gate.Tile,
        _ => null
    };

    // A hex's name: the base on it, or its county and the county's city, e.g. "계교 (업)".
    internal static string TileName(this GameSnapshot data, int tile)
    {
        foreach (int id in data.CityIds)
            if (data.CityTile(id) == tile) return data.CityName(id);
        if (data.Gates.FirstOrDefault(g => g.Tile == tile) is { } gate) return gate.Name;
        if (tile < 0 || tile >= data.TileCounties.Length || data.County(data.TileCounties[tile]) is not { } county) return "";
        string city = data.CityName(county.HomeCity);
        return county.Name == city ? city : $"{county.Name} ({city})";
    }
}

namespace San14Tenkazu;

internal sealed partial class GameSnapshot
{
    // CHexData +0x10, copied from the loaded world, not the installation's MapExCe.bin.
    internal byte[]? Terrain { get; init; }

    // CCityData +0xCE names its state. The native state table contains the seventeen
    // ordinary states followed by the five tribal states; mods can move cities between them.
    internal IEnumerable<int> CityIds => Cities.Where(c => c is { CityId: > 0, IsTribe: false }).Select(c => c.CityId);
    internal bool IsCity(int id) => id > 0 && id < Cities.Length && Cities[id] is { IsTribe: false };
    internal string CityName(int id) => id > 0 && id < Cities.Length && Cities[id] is { } city
        ? city.Name is { Length: > 0 } name ? name : $"{L.Columns.City} {id}" : CityNames.BaseName(id);
    internal string BaseName(int id) => Gates.FirstOrDefault(g => g.Id == id) is { } gate ? gate.Name : CityName(id);
    internal int? CityTile(int id) => CastleByCity.TryGetValue(id, out var castle) && castle.Tile < GameMemory.TileCount ? castle.Tile : null;
}

namespace San14Tenkazu;

// Which of the game's field models stand where on the map, and how they are turned. Read from the running
// game's model instance records (2026-10-01): every instance stands exactly on its hex centre, unscaled.
//   Cities: "obj-mc01-{size}-city-{style}-nm-001", the style fixed per city, the size from its scale
//     (1 s, 2 and 3 m, 4 l; the game seems to switch a little after the scale changes). Unrotated.
//   Tribes: the large d and e layouts. County seats: "prefecture", unrotated. Gates and ports: turned.
//   Facilities forces build: "obj-mb01-<kind>-001", unrotated and 1.5 times as large.
//   A city or gate no force holds stands in ruins: the game shows "-brk-" variants for exactly those
//   (seen 2026-10-01 for every unowned city and gate in view, brk-002 for 14 of 16; brk-001 twice).
// Yaw is in degrees, counter-clockwise on the map (+x east, +y south).
internal static class MapModels
{
    internal readonly record struct Placement(string Model, int Yaw, int Tile, double Scale = 1);

    // City style by city ID (index 1~46): a square walls, b rounded, c angled. All 46 seen in the game.
    private const string CityStyles = "-bbbbbbbaaaaaaaaaabababbbccccccc" + "aaaacccccbacccc";
    // Gates 52~61 (호관 … 면죽관).
    private static readonly int[] GateYaws = [90, 90, 90, 270, 60, 240, 180, 120, 120, 180];
    // Tribes 47~51 (오환, 선비, 강: d; 산월, 남만: e), all seen in the game.
    private static readonly string[] TribeStyles = ["d", "d", "d", "e", "e"];
    // Ports by hex, all 40 seen in the game. PortYaw's rule is kept for a map with other ports.
    private static readonly Dictionary<int, int> PortYaws = new()
    {
        [8741] = 90, [9007] = 150, [11166] = 240, [12034] = 30, [12889] = 30, [23962] = 30, [25597] = 0, [25915] = 90,
        [27370] = 180, [27601] = 330, [28595] = 90, [29228] = 270, [29647] = 0, [29900] = 150, [30268] = 0, [30909] = 150,
        [31643] = 210, [31976] = 0, [32281] = 120, [32901] = 120, [33429] = 180, [33753] = 300, [34457] = 240, [34496] = 240,
        [35510] = 150, [35980] = 240, [37172] = 60, [37718] = 90, [43095] = 60, [47001] = 0, [47825] = 30,
        // Seen later (2026-10-01): 8 of these 9 matched PortYaw; 12838 is 90 where the rule gives 60.
        [5013] = 60, [5256] = 30, [12838] = 90, [13505] = 300, [13785] = 180, [14619] = 180, [15081] = 210, [15262] = 180, [19776] = 30
    };

    // Facility models by building type, as MapArt's icons: 3 jin, 5 yumiyagura and 9 dorui were seen on their
    // hexes in the game; the others follow the order of the executable's facility lists (see MapArt).
    // Types without a model (traps) are not drawn.
    private static readonly Dictionary<int, string> FacilityModels = new()
    {
        [3] = "jin", [4] = "toride", [5] = "yumiyagura", [6] = "tousekidai", [7] = "gungakudai", [8] = "sekiheihachijin",
        [9] = "dorui", [10] = "ishikabe", [11] = "hiwana", [12] = "moukawana", [13] = "kasen", [14] = "otoshiana",
        [15] = "shadai", [16] = "tetusaku", [17] = "rakuseki"
    };
    private const double FacilityScale = 1.5;

    internal static string CityModel(int cityId, long scale, bool ruined = false)
    {
        string size = scale switch { >= 4 => "l", >= 2 => "m", _ => "s" };
        char style = cityId > 0 && cityId < CityStyles.Length ? CityStyles[cityId] : 'a';
        return $"obj-mc01-{size}-city-{style}-{(ruined ? "brk-002" : "nm-001")}";
    }

    // Everything on the map. Cities, tribes and gates need the game's data; county seats and ports do not.
    internal static List<Placement> All(GameSnapshot? data)
    {
        var list = new List<Placement>();
        foreach (int tile in MapTerrain.SeatTiles) list.Add(new Placement("obj-mc01-prefecture-nm-001", 0, tile));
        foreach (int tile in MapTerrain.PortTiles) list.Add(new Placement("obj-mc01-port-nm-001", PortYaw(tile), tile));
        if (data is null) return list;
        for (int id = 1; id <= GameMemory.CityCount; id++)
            list.Add(new Placement(CityModel(id, data.Cities[id][CityFields.Scale], ruined: data.ForceOfCity(id) == 0), 0, MapTerrain.CityTiles[id]));
        foreach (CityRecord tribe in data.Tribes)
            if (data.CastleByCity.TryGetValue(tribe.CityId, out BuildingRecord? castle) && castle.Tile < GameMemory.TileCount)
                list.Add(new Placement($"obj-mc01-l-city-{TribeStyles[tribe.CityId - GameMemory.TribeFirst]}-nm-001", 0, castle.Tile));
        foreach (GateRecord gate in data.Gates)
        {
            if (gate.Tile < 0) continue;
            bool ruined = data.ForceOfGate(gate) == 0;
            list.Add(new Placement(ruined ? "obj-mc01-gate-brk-002" : "obj-mc01-gate-nm-001", GateYaws[gate.Id - GameMemory.GateFirst], gate.Tile));
        }
        foreach (Facility facility in data.Facilities)
            if (FacilityModels.TryGetValue(facility.Kind, out string? kind) && facility.Tile < GameMemory.TileCount)
                list.Add(new Placement($"obj-mb01-{kind}-001", 0, facility.Tile, FacilityScale));
        return list;
    }

    // A port faces along its shore: 90° on from the mean direction of its water neighbours, in 30° steps.
    // This matched 28 of the 31 ports seen (the rest within 30°).
    private static int PortYaw(int tile)
    {
        if (PortYaws.TryGetValue(tile, out int yaw)) return yaw;
        int column = tile % MapTerrain.Columns, row = tile / MapTerrain.Columns;
        double x = 0, y = 0;
        int[] angles = [90, 30, -30, -90, -150, 150];      // N, NE, SE, S, SW, NW, counter-clockwise from east
        for (int direction = 0; direction < 6; direction++)
            if (MapTerrain.Neighbour(column, row, direction) is >= 0 and var neighbour && MapTerrain.IsWater(MapTerrain.Terrain[neighbour]))
            {
                x += Math.Cos(angles[direction] * Math.PI / 180);
                y += Math.Sin(angles[direction] * Math.PI / 180);
            }
        double mean = Math.Atan2(y, x) * 180 / Math.PI;
        return ((int)Math.Round((mean + 90) / 30) * 30 % 360 + 360) % 360;
    }
}

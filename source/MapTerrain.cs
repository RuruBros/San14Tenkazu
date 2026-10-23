using System.IO.Compression;

namespace San14Tenkazu;

// Static map data captured from the running game (SAN14CE_KO.exe, same build as the pinned hash).
// The world is a 220 x 220 grid of flat-topped hexes; even columns sit half a hex lower.
internal static class MapTerrain
{
    internal const int Columns = GameMemory.MapColumns;
    internal const int Rows = GameMemory.MapRows;

    // World units: a hex's circumradius is 1.
    internal static readonly float Sqrt3 = MathF.Sqrt(3);
    internal static readonly float WorldWidth = 1.5f * Columns + 0.5f;
    internal static readonly float WorldHeight = Sqrt3 * (Rows + 0.5f);

    // Terrain types as the game numbers them (MapView.TerrainColors lists them all); these the code asks for by name.
    internal const int Woods = 5, Jungle = 6, MarshForest = 7, Seat = 15, Gate = 17, Port = 18, Mountain = 19;

    // Streams, great rivers, the map's edge, the sea and wetland.
    internal static bool IsWater(int type) => type is 13 or 14 or 22 or 23 or 24;

    // The map's edge and the sea.
    internal static bool IsSea(int type) => type is 22 or 23;

    internal static PointF TileCenter(int tile)
    {
        int row = tile / Columns, column = tile % Columns;
        return new PointF(1.5f * column + 1f, Sqrt3 * (row + (column % 2 == 0 ? 1f : 0.5f)));
    }

    // Match the ground shader's HexAt: the nearest centre in the two surrounding
    // columns. Do not clamp decorative land outside the hex grid onto an edge tile.
    internal static int? TileAt(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > WorldWidth || y > WorldHeight) return null;
        int column = (int)Math.Floor((x - 1) / 1.5), bestColumn = -1, bestRow = -1;
        double nearest = double.PositiveInfinity;
        for (int c = column; c <= column + 1; c++)
        {
            double offset = (c & 1) == 0 ? 1 : 0.5;
            int row = (int)Math.Round(y / Sqrt3 - offset);
            double dx = x - (1.5 * c + 1), dy = y - Sqrt3 * (row + offset);
            double distance = dx * dx + dy * dy;
            if (distance < nearest) (nearest, bestColumn, bestRow) = (distance, c, row);
        }
        return bestColumn >= 0 && bestColumn < Columns && bestRow >= 0 && bestRow < Rows
            ? bestRow * Columns + bestColumn : null;
    }

    // Neighbour tile in direction 0..5 = N, NE, SE, S, SW, NW (flat-topped, even columns lower); -1 past the map's edge.
    internal static int Neighbour(int column, int row, int direction)
    {
        bool even = column % 2 == 0;
        (int dc, int dr) = direction switch
        {
            0 => (0, -1),
            1 => (1, even ? 0 : -1),
            2 => (1, even ? 1 : 0),
            3 => (0, 1),
            4 => (-1, even ? 1 : 0),
            _ => (-1, even ? 0 : -1)
        };
        int c = column + dc, r = row + dr;
        return c < 0 || r < 0 || c >= Columns || r >= Rows ? -1 : r * Columns + c;
    }

    // The neighbours a tile has on the map, in the same order.
    internal static IEnumerable<int> Neighbours(int tile)
    {
        int row = tile / Columns, column = tile % Columns;
        for (int direction = 0; direction < 6; direction++)
            if (Neighbour(column, row, direction) is >= 0 and var next) yield return next;
    }

    // Terrain type per tile (index = row * Columns + column), zlib-compressed.
    internal static readonly byte[] Terrain = Decode("""
        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
        +28i/R8AEyec
        """);

    // Centre tile of each city's castle (index = city ID).
    internal static readonly int[] CityTiles =
    [
        0,
        2841, // 1 양평
        3907, // 2 북평
        3215, // 3 계
        7807, // 4 진양
        7634, // 5 남피
        10905, // 6 업
        11373, // 7 평원
        10971, // 8 북해
        18225, // 9 하비
        17102, // 10 소패
        22195, // 11 광릉
        23037, // 12 수춘
        28327, // 13 여강
        14882, // 14 복양
        17946, // 15 진류
        21899, // 16 허창
        25873, // 17 여남
        16379, // 18 낙양
        21655, // 19 완
        15468, // 20 장안
        24053, // 21 상용
        10610, // 22 안정
        14542, // 23 천수
        6386, // 24 무위
        27251, // 25 건업
        30351, // 26 오
        35846, // 27 회계
        32935, // 28 시상
        41543, // 29 건안
        45678, // 30 남해
        47814, // 31 교지
        29396, // 32 강하
        26947, // 33 신야
        29578, // 34 양양
        33544, // 35 강릉
        38188, // 36 장사
        37265, // 37 무릉
        43243, // 38 계양
        42770, // 39 영릉
        31089, // 40 영안
        20728, // 41 한중
        25778, // 42 자동
        35467, // 43 강주
        31691, // 44 성도
        42488, // 45 건녕
        47091, // 46 운남
    ];

    // The hexes of the county seats and of the ports.
    internal static readonly int[] SeatTiles = TilesOf(Seat), PortTiles = TilesOf(Port);

    private static int[] TilesOf(int type) => Enumerable.Range(0, Terrain.Length).Where(tile => Terrain[tile] == type).ToArray();

    internal static IEnumerable<int> TilesOf(int type, byte[]? terrain) => terrain is null ? TilesOf(type)
        : Enumerable.Range(0, terrain.Length).Where(tile => terrain[tile] == type);

    private static byte[] Decode(string base64)
    {
        using var input = new MemoryStream(Convert.FromBase64String(base64));
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        byte[] data = output.ToArray();
        if (data.Length != Columns * Rows) throw new InvalidDataException(L.Errors.TerrainSize);
        return data;
    }
}

namespace San14Tenkazu.Formulas;

// 0x2412D0: tile ownership, persistent player territory achievement and scenery repaint requests.
internal sealed class TileOwnership(World w, ProfileAchievements achievements, bool repaintAvailable, Action requestRepaint)
{
    internal static bool SceneryValid(World w, Rec scenery) => scenery.Index is >= 1 and <= 100 &&
        w.Entry(Tables.Tiles, scenery.U16(0x5C), 48399).U16(0x12) != 0;

    internal void Apply(Rec tile, int owner)
    {
        owner = Math.Clamp(owner, 0, 51);
        tile.Bytes[0x14] = (byte)owner;
        if (tile.U8(0x10) is >= 15 and <= 18 && owner != 0 &&
            owner == w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 51).Index)
            achievements.Territory(1, 1, 0);
        if (!repaintAvailable) return;
        Rec scenery = w.Entry(Tables.Scenery, tile.U8(0x1E), 100);
        if (SceneryValid(w, scenery) && (scenery.U8(0x5F) & 1) != 0) requestRepaint();
    }
}

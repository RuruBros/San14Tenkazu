namespace San14Tenkazu;

internal readonly record struct AiPathNode(uint Hex, int Cost);
internal sealed record AiComposedPath(uint[] Hexes, int Cost, bool NullHeadFault)
{
    internal bool Success => !NullHeadFault && Hexes.Length > 0;
}

// Compose actual segment results; the native search remains a separate operation.
internal static class AiPathComposition
{
    internal const int OutputCapacity = 1320;
    internal const int SearchNodeBytes = 40;
    internal const int SearchNodeCount = 48400;
    internal const int SegmentClearBytes = 48400;

    internal static AiComposedPath Compose(IReadOnlyList<IReadOnlyList<AiPathNode>> segments)
    {
        List<uint> hexes = [];
        int cost = 0;
        for (int segment = 0; segment < segments.Count; segment++)
        {
            IReadOnlyList<AiPathNode> chain = segments[segment];
            if (chain.Count == 0) continue;
            int first = segment > 0 ? 1 : 0;
            // Both original wrappers dereference the skipped head without testing it.
            if (first == chain.Count) return new(hexes.ToArray(), cost, true);
            cost = unchecked(cost + chain[first].Cost);
            for (int index = first; index < chain.Count; index++) hexes.Add(chain[index].Hex);
        }
        return new(hexes.ToArray(), cost, false);
    }

    // The requested waypoint remains the next source even when the previous search failed.
    internal static uint Source(uint initial, IReadOnlyList<uint> targets, int segment) =>
        segment == 0 ? initial : targets[segment - 1];

    internal static int OutputOffset(int slot) => checked(0x1BB3D20 + slot * 0x14A0);
}

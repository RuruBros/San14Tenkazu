namespace San14Tenkazu;

// The routing engine supplies the path/cost. This models the verified predicate wrapper only.
internal static class AiPathPredicates
{
    internal static bool Evaluate(int predicate, bool valid, bool cacheEligible, int cached, bool pathSuccess,
        int count, int cost, int limit, float mobility, bool hostile) => valid && (predicate switch
        {
            43 or 55 => (cacheEligible && cached != 0 || pathSuccess) && cost <= limit,
            56 => pathSuccess,
            127 => pathSuccess && cost < MobilityLimit(mobility),
            // P130 intentionally ignores the routing return value and scans the produced list.
            130 => hostile,
            132 or 134 => pathSuccess && count > 0 && hostile,
            _ => throw new ArgumentOutOfRangeException(nameof(predicate)),
        });

    // Native cvttss2si followed by int32 imul: truncate before multiplying, wrap on overflow.
    internal static int MobilityLimit(float mobility)
    {
        int truncated = float.IsFinite(mobility) && mobility >= -2147483648f && mobility < 2147483648f
            ? (int)mobility : int.MinValue;
        return unchecked(truncated * 120);
    }

    // This is an ordered cache entry; reversing the two area IDs addresses a different entry.
    internal static int CacheOffset(ushort sourceArea, ushort targetArea) =>
        checked(0xC0 + (sourceArea * 500 + targetArea) * 4);
}

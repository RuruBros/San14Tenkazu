namespace San14Tenkazu;

// Exact storage/list transformations of previously unnamed info kinds; no invented display names.
internal static class AiInfoRules
{
    internal static int ForceIndex(byte stored) => stored < 52 ? stored : 0;

    internal static ulong[] SelectList(IReadOnlyList<ulong> members, IReadOnlyList<bool> selected)
    {
        if (members.Count != selected.Count) throw new ArgumentException("Unequal list lengths.");
        return members.Where((_, i) => selected[i]).ToArray();
    }

    internal static int StatusWithoutOutput(string family, int kind) => (family, kind) switch
    {
        ("District", 3) => 2,
        ("Force", 20) or ("Area", 9) => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

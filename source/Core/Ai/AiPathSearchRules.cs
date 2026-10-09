namespace San14Tenkazu;

// Numeric node transitions shared by the original full 183A30 / 184EB0 search backends.
// Costs, distance and the heuristic factor are observed native inputs; no world search is substituted.
internal static class AiPathSearchRules
{
    internal static int Cost(int parent, int step) => unchecked(parent + step);
    internal static bool CanRelax(byte flags, int oldCost, int newCost) => (flags & 3) == 0 || oldCost > newCost;
    internal static byte LinkedFlags(byte flags) => (byte)(flags & 0xFD);
    internal static bool UsesHeuristic(int options) => (options & 5) != 0;
    // 183B22..183B39 and 184FA2..184FAD: force51 or unsigned (force-46)<=4.
    internal static bool IsMinorOrSpecialForce(int force) => force == 51 || unchecked((uint)(force - 46)) <= 4;
    // The unbounded backend also compares home when original232730 returns nonzero.
    // The bounded backend skips that comparison for every minor/special force.
    internal static bool ChecksCommanderHome(bool boundedBackend, int force, bool originalMinorPredicate)
        => !IsMinorOrSpecialForce(force) || (!boundedBackend && originalMinorPredicate);
    // Original Unit_Create assignment 2D2A93..2D2AAA; later writes/loading are outside this contract.
    internal static ushort CreatedCommanderId(int request) => (ushort)Math.Clamp(request, 0, 6000);
    // After generic admission fails, under intact city/gate and building tables.
    // Valid armies need a city/gate ID which the later non-base facility filter skips.
    // This is only a necessary condition; neutral terrain16 still has other constraints.
    internal static bool CanEnterNonBaseFacilityFallback(bool validArmy, int terrain) => !validArmy && terrain == 16;
    // Unbounded 183A30 at 184079..184084, after native admission.
    // Source candidates and options & 0xC bypass this block; 184EB0 has no equivalent.
    internal static bool RejectsFire(int contextFlag, ushort remainingTimer) => contextFlag != 0 && remainingTimer > 0;
    // Successful world constructor 326B5A..326BBF and array dispatcher FF945C.
    // Describes an intact completed table, not allocation failure or load transitions.
    internal static bool HasConstructedTileTable(ReadOnlySpan<ulong> pointers)
    {
        const int count = 48_400;
        const ulong stride = 32;
        if (pointers.Length != count || pointers[0] == 0 || pointers[0] > ulong.MaxValue - (count * stride - 1)) return false;
        for (int i = 0; i < count; i++)
            if (pointers[i] != pointers[0] + (ulong)i * stride) return false;
        return true;
    }
    // 18A199..18A1A5 after generic admission returns zero; exception blocks have further gates.
    internal static bool HasTerrainAdmissionException(int terrain) => terrain is 16 or 17;
    // 183A30 at 183FCD and 18408A..184096; source candidates bypass this block.
    // 184EB0 has no corresponding context +2C terrain penalty.
    internal static int ApplyTerrainPenalty(int cost, int contextFlag, int terrain, int options)
        => (options & 0xC) == 0 && contextFlag != 0 && terrain == 7 ? unchecked(cost + 10_000) : cost;
    // After native tile admission and only for a valid non-city/gate facility.
    // Source candidates bypass the filter; options & 0xC use the separate 2C6850 route.
    internal static bool PassesFacilityFilter(bool boundedBackend, int category, int owner, int searchOwner, int options)
    {
        if ((options & 0xC) != 0) return true;
        if ((options & 0x40) != 0) return !boundedBackend || owner != searchOwner;
        return category is not (2 or 3);
    }
    internal static int Priority(int cost, int distance, float factor, int options)
    {
        if (!UsesHeuristic(options)) return cost;
        float value = (float)distance * factor;
        int truncated = float.IsFinite(value) && value >= -2147483648f && value < 2147483648f ? (int)value : int.MinValue;
        return unchecked(cost + truncated);
    }
}

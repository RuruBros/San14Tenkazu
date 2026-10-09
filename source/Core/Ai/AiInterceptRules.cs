namespace San14Tenkazu;

// CExecuteInterceptNode caches p0 at +48 and p1 at +44. These are semantic descriptions of native
// consumers, not recovered original variable names. Routing and mobility calculation remain native inputs.
internal static class AiInterceptRules
{
    internal static bool SortMembers(int count, int option) => count > 1 && option != 0;
    internal static bool CollectHostileCenters(int option, ushort firstRelay) => option != 0 && firstRelay == 48400;
    // Comparator 97330, used by 5D3B0: native list traversal places larger mobility factors first.
    internal static bool MobilityPrecedes(float first, float second) => first > second;
}

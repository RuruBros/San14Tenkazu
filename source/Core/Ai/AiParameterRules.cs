namespace San14Tenkazu;

// Rules observed in native parameter consumers; command execution stays in the game.
internal static class AiParameterRules
{
    internal static sbyte DistrictPurpose(int value) => (sbyte)Math.Clamp(value, sbyte.MinValue, sbyte.MaxValue);

    internal static int[] DeparturePolicies(IReadOnlyList<int> before, bool kind1, bool kind2, bool kind3)
    {
        if (before.Count != 5) throw new ArgumentException("Five policy slots are required.", nameof(before));
        int[] result = before.ToArray();
        if (kind1) { result[0] = 2; result[1] = 0; }
        if (kind2) { result[0] = 1; result[1] = 1; result[2] = 0; }
        if (kind3) { result[2] = 1; result[3] = 2; }
        return result;
    }

    internal static bool FacilityBorderMode(bool homeArea, bool gateArea, bool adjacentToGate, int option) =>
        !homeArea && option == 0 && (gateArea || adjacentToGate);

    internal static bool TradeTroopsAllowed(int requested, int troops) => requested < troops;
}

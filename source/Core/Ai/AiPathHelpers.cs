namespace San14Tenkazu;

// Native 18A080. Facts from nested world/terrain/route helpers are inputs, not guessed replacements.
internal sealed record AiPathAdmission(
    bool HasTile, bool HasGround, bool HasArmy, int Terrain, bool PrimaryAllowed, bool BoatAllowed,
    bool ValidArmy, bool ValidCity, bool AtCenter, bool WaypointsEnforced, bool ArmyAtCenter,
    bool PositiveGoalCount, bool GoalMatch, bool AttackOrder, bool SameTargetCity, bool Distant,
    bool SameForce, bool FirstRelayMissing, bool ReturnOrder, bool ReturnNeighbor)
{
    internal bool Allowed()
    {
        if (!HasTile || !HasGround) return false;
        if (PrimaryAllowed) return HasArmy || Terrain != 24 || BoatAllowed;
        // Both the matching-goal and nonmatching-goal exits for a valid gate center return 1.
        if (Terrain == 17) return ValidCity && AtCenter;
        if (Terrain != 16) return false;
        if (!ValidArmy) return true;
        if (!ValidCity) return false;
        if (WaypointsEnforced && (ArmyAtCenter || PositiveGoalCount && GoalMatch)) return true;
        if (!AttackOrder && !SameTargetCity) return false;
        if (Distant) return true;
        return SameForce && FirstRelayMissing && ReturnOrder && ReturnNeighbor;
    }
}

internal static class AiPathStepCost
{
    // 1864F0 tests pointer presence and option bit 0. A non-null object's validity is not checked here.
    // The native cache supplies a float factor keyed only by the terrain-definition pointer.
    internal static int Evaluate(int cost, int options, bool hasTile, bool hasArmy, float cachedFactor)
    {
        if (!hasTile || !hasArmy || (options & 1) == 0) return cost;
        float value = (float)cost / cachedFactor;
        return float.IsFinite(value) && value >= -2147483648f && value < 2147483648f ? (int)value : int.MinValue;
    }
}

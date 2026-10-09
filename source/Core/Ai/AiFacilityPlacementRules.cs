namespace San14Tenkazu;

internal readonly record struct AiFacilityPlacementChecks(bool ValidForce, bool ValidHex, bool SceneryClear,
    bool ValidDefinition, bool TerrainAllowed, bool ExistingFacilityAllowed, bool HexOwnershipAllowed,
    bool AreaCapacityAvailable, bool AreaOwnershipAllowed, bool PrerequisiteAllowed, bool SpacingAllowed);

// Native definition fields and placement gates; unexpanded helpers remain result inputs.
internal static class AiFacilityPlacementRules
{
    internal static uint TerrainMask(ulong parameterBytes, uint legacyMask, uint encodedBytes, int facilityIndex)
    {
        if ((parameterBytes & ~3UL) != 76) return legacyMask;
        uint mask = 0;
        for (int i = 0; i < 4; i++)
        {
            uint value = (encodedBytes >> (i * 8)) & 255;
            uint decoded = (((value >> 1) | (value << 7)) & 255) ^ ((uint)facilityIndex & 255);
            mask |= decoded << (i * 8);
        }
        return mask;
    }

    internal static bool AllowsTerrain(uint mask, uint terrain) => terrain <= 30 && (mask & (1U << (int)terrain)) != 0;

    // Spacing byte read at 0x27BEA0..0x27BED8. Only the decoder's low byte is consumed.
    internal static byte SpacingParameter(long parameterBytes, byte legacySpacing, byte encodedByte, int facilityIndex) =>
        parameterBytes >> 2 == 19
            ? (byte)((((uint)encodedByte >> 5) | ((uint)encodedByte << 3)) ^ ((uint)facilityIndex & 255))
            : legacySpacing;

    // Original 0x27BD50 scans exact rings 1..4 and ignores these categories.
    internal static bool BlocksSpacing(int distance, bool validBuilding, bool validDefinition,
        byte category, int mode, byte spacing) => distance is >= 1 and <= 4 && validBuilding && validDefinition
        && category is not (3 or 4) && (mode != 0 || category != 2) && distance <= spacing;

    internal static bool CountsForAreaCapacity(byte category) => category is 2 or 3 or 4;
    internal static bool AreaCapacityFull(bool validArea, int relevantCount, byte capacity) => validArea && relevantCount >= capacity;
    internal static bool ExistingFacilityAllowed(bool present, byte category, bool hostileOwner) => !present || category == 4 && hostileOwner;
    internal static bool HexOwnershipAllowed(bool sameForce, bool hostileOwner) => sameForce || hostileOwner;
    internal static bool AreaOwnershipAllowed(byte category, bool validArea, bool sameForce) => category != 4 || !validArea || sameForce;
    internal static bool NeedsPrerequisite(uint option) => option != 0;
    internal static int SpacingMode(byte category) => category == 2 ? 1 : 0;

    private static byte PrerequisiteParameter(ulong parameterBytes, byte legacy, byte encoded, int facilityIndex, int rotation) =>
        (parameterBytes & ~3UL) == 76
            ? (byte)((((uint)encoded >> (8 - rotation)) | ((uint)encoded << rotation)) ^ ((uint)facilityIndex & 255))
            : legacy;

    internal static byte PrerequisitePolicyIndex(ulong parameterBytes, byte legacy, byte encoded, int facilityIndex) =>
        PrerequisiteParameter(parameterBytes, legacy, encoded, facilityIndex, 1);
    internal static byte PrerequisiteRequiredValue(ulong parameterBytes, byte legacy, byte encoded, int facilityIndex) =>
        PrerequisiteParameter(parameterBytes, legacy, encoded, facilityIndex, 3);
    internal static int PolicyEffectLookupIndex(byte index) => index <= 100 ? index : 0;

    // Original 0x2C4990 global-state exception. Display names of these states are unknown.
    internal static bool PrerequisiteException(bool globalPresent, uint globalValue, bool featurePresent, byte flags, int facilityIndex) =>
        globalPresent && globalValue != 0 && featurePresent && (flags & 8) != 0 && facilityIndex is >= 15 and <= 17;
    internal static bool AllowsPrerequisite(bool validForce, bool exception, bool validPolicy, int effectValue, byte required) =>
        validForce && (exception || !validPolicy || effectValue >= required);

    // The original 0x279190 checks these in order; each helper's result remains an input.
    internal static bool AllowsPlacement(AiFacilityPlacementChecks value) => value.ValidForce && value.ValidHex
        && value.SceneryClear && value.ValidDefinition && value.TerrainAllowed && value.ExistingFacilityAllowed
        && value.HexOwnershipAllowed && value.AreaCapacityAvailable && value.AreaOwnershipAllowed
        && value.PrerequisiteAllowed && value.SpacingAllowed;
}

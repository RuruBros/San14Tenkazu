namespace San14Tenkazu;

// Numeric rules of P25/P26/P27/P108/P110/P111 after resolving their force, district or sortie object.
// Resolution and Obj_IsValid are native preconditions, represented here by validObject.
internal static class AiDistrictPredicates
{
    internal static bool ForceGoalIs(sbyte goal, int wanted, bool validObject = true) => validObject && goal == wanted;
    internal static bool DistrictTypeIs(sbyte type, int wanted, bool validObject = true) => validObject && type == wanted;
    internal static bool TroopsKindIs(ushort purpose, int wanted, bool validObject = true) => validObject && purpose == wanted;

    // P108 maps permission numbers to two separate storage fields; out-of-range selectors return false.
    internal static bool PermissionAllows(byte flags1B, uint flags1C, int permission, bool validObject = true)
    {
        if (!validObject) return false;
        return permission switch
        {
            >= 0 and <= 4 => (flags1B & (1 << (permission + 2))) != 0,
            >= 5 and <= 12 => (flags1C & (1u << (permission - 2))) != 0,
            13 => (flags1C & (1u << 14)) != 0,
            >= 14 and <= 16 => (flags1C & (1u << (permission - 3))) != 0,
            17 => (flags1B & 0x80) != 0,
            _ => false,
        };
    }

    // P110 accepts selectors 0..4, comparing them with the stored directive 1..5.
    internal static bool DirectiveIs(byte directive, int selector, bool validObject = true) =>
        validObject && (uint)selector <= 4 && directive == selector + 1;

    // P111's field order is +20/+22/+21. Its comparison argument is normalized to 0 unless it is 1 or 2.
    internal static bool SettingIs(byte setting20, byte setting21, byte setting22, int selector, int wanted,
        bool validObject = true)
    {
        if (!validObject) return false;
        int selected = selector switch { 0 => setting20, 1 => setting22, 2 => setting21, _ => -1 };
        int normalized = wanted is 1 or 2 ? wanted : 0;
        return selected == normalized;
    }
}

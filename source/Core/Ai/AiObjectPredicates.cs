namespace San14Tenkazu;

// Direct field rules of P120/P121/P146/P159. These names describe verified storage, not guessed flag meanings.
internal static class AiObjectPredicates
{
    // P120/P121 require a successful blackboard lookup, but do not call Obj_IsValid.
    // Their callers must provide a readable non-null record even when its game validity is false.
    internal static bool ForceBit1(byte flags12, bool lookupSucceeded = true) => lookupSucceeded && (flags12 & 2) != 0;

    internal static bool RankHasFlag(byte rank, ReadOnlySpan<byte> flagsByRank, int selector, bool lookupSucceeded = true)
    {
        if (!lookupSucceeded || (uint)selector > 3) return false;
        if (flagsByRank.Length < 13) throw new ArgumentException("Ranks 0..12 are required.", nameof(flagsByRank));
        return (flagsByRank[rank <= 12 ? rank : 0] & (1 << selector)) != 0;
    }

    // P146/P159 additionally require Obj_IsValid before accessing their field.
    internal static bool ArmyByte140IsZero(byte value, bool validObject, bool lookupSucceeded = true) =>
        lookupSucceeded && validObject && value == 0;

    internal static bool PersonBit14(ushort flags15E, bool validObject, bool lookupSucceeded = true) =>
        lookupSucceeded && validObject && (flags15E & 0x4000) != 0;
}

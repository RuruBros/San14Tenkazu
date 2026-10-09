namespace San14Tenkazu;

// Native 0x26BD40: cooperation between two valid forces. Predicate 160 delegates here for different forces;
// its same-force district rules are separate. Mode 3 is the stock AI's exchange-candidate guard.
// The game labels +0x17A as Han inclination (ai_field_names.py); force +0x12 bit 1's meaning remains unverified.
internal static class AiCooperation
{
    private static readonly int[][] Weights = [[2, 6, 4, 2, 0], [2, 2, 0, 2, 4], [4, 0, 0, 0, 4], [0, 0, 2, 4, 2]];

    internal static bool WithOtherForce(int mode, byte friendship, byte agreement, byte ownFlags, byte otherFlags,
        short ownHan, short otherHan, bool hatedRuler, bool sameForce,
        short warlike, short duty, short fame, short diplomacy, short ambition)
    {
        if ((uint)mode >= Weights.Length) throw new ArgumentOutOfRangeException(nameof(mode));
        if (sameForce || ((agreement & 0x7F) <= 5 && (ownFlags & otherFlags & 2) == 0) || friendship < 100 || hatedRuler)
            return false;
        int[] w = Weights[mode];
        int penalty = ownHan > 19 && otherHan > 19 ? -10 : -60;
        return w[0] * warlike + w[1] * duty + w[2] * fame + w[3] * diplomacy + w[4] * ambition + penalty > 0;
    }
}

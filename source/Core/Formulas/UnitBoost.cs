namespace San14Tenkazu.Formulas;

internal static class UnitBoost
{
    private static int Abs(int value) => unchecked((value ^ (value >> 31)) - (value >> 31));

    // 0x2EC3D0: merge signed boost levels by level-squared duration. Opposite signs cancel weighted time;
    // equal weights retain the old level. The native code clamps only after the merge and signed division.
    internal static void Set(Rec unit, int boost, int level, int time)
    {
        if (!Objects.UnitValid(unit)) return;
        int oldLevel = unit.I8(0x4A + boost), oldTime = unit.U8(0x50 + boost);
        int chosen = oldLevel, duration = oldTime;
        unchecked
        {
            if (oldLevel == 0)
            {
                if (oldTime == 0) { chosen = level; duration = time; }
            }
            else if (oldTime != 0 && time != 0)
            {
                int weight;
                if (oldLevel > 0 && level > 0 || oldLevel < 0 && level < 0)
                {
                    if (Abs(oldLevel) < Abs(level)) chosen = level;
                    weight = oldLevel * oldLevel * oldTime + level * level * time;
                }
                else
                {
                    int a = (oldLevel > 0 ? 1 : -1) * oldTime * oldLevel * oldLevel;
                    int b = (level > 0 ? 1 : -1) * time * level * level;
                    if (Abs(a) < Abs(b)) chosen = level;
                    weight = Abs(a + b);
                }
                duration = weight / (chosen * chosen);
            }
        }
        if ((uint)boost < 6)
        {
            unit.Bytes[0x4A + boost] = unchecked((byte)Math.Clamp(chosen, -10, 10));
            unit.Bytes[0x50 + boost] = (byte)Math.Clamp(duration, 0, 240);
        }
    }
}

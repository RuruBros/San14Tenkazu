namespace San14Tenkazu.Formulas;

// The game's random numbers (rva 0x3FBB00): one global state (exe + 0x1A47B90) that every draw moves on. Knowing the
// state and the order of the draws, a formula with chance in it gives the game's own result.
internal sealed class Rng(int state)
{
    internal const int StateRva = 0x1A47B90;

    internal int State { get; private set; } = state;

    // A number in 0..n-1; n below 2 gives 0 without a draw.
    internal int Next(int n)
    {
        if (n < 2) return 0;
        uint s = (uint)State;
        for (uint k = (s & 0xF) + 1; k > 0; k--)
            s = RotateHalves((s - 0x7B) * 0x693D4B5 + 0x1E240);
        s = RotateHalves(s * 0x41C64E6D + 0x3039);
        State = (int)s;
        return (int)(s & 0x7FFFFFFF) % n;
    }

    private static uint RotateHalves(uint v) => v << 16 | v >> 16;
}

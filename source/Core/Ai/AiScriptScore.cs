namespace San14Tenkazu;

// Integer expressions read from the live CSymboScript table. Native-answer fixtures are reproduced by
// Reverse/ai_scripts.py in copied game pages. Candidate selection and caller context belong to the trees.
internal static class AiScriptScore
{
    // Complete live table: slot 0 and 101..895 are empty, 14..100 explicitly hold the integer constant zero.
    // The stock behavior trees use only 4/8/10/11/12/13; meanings of unused formulas are not inferred.
    internal static int Evaluate(int script, ReadOnlySpan<int> args)
    {
        if ((uint)script >= 896) throw new ArgumentOutOfRangeException(nameof(script));
        int required = script switch { 1 or 3 or 5 or 7 or 8 or 9 or 10 or 11 or 13 => 2,
            2 => 3, 4 => 7, 6 => 1, 12 => 6, _ => 0 };
        if (args.Length < required) throw new ArgumentException("Not enough script arguments.", nameof(args));
        unchecked
        {
            return script switch
            {
                1 or 5 => args[0] * (100 + args[1]) / 100,
                2 => args[0] - args[1] + args[2],
                3 => Divide(args[0] * 100, args[1]),
                4 => SumSeven(args[..7]),
                6 => 90 + args[0] * 100 / 100,
                7 => args[0] * (100 - args[1]),
                8 => args[0] * args[1],
                9 => args[0] - args[1],
                10 => args[0] + args[1],
                11 => Divide(args[0], args[1]),
                12 => Divide(100 * args[0], args[1]) - args[2] - args[3] + args[4] + args[5],
                13 => PowerByWarlike(args[0], args[1]),
                _ => 0,
            };
        }
    }

    // Native 0x92E70 returns 1 for divisor zero. INT_MIN/-1 traps, as C# division throws OverflowException.
    private static int Divide(int numerator, int denominator) => denominator == 0 ? 1 : numerator / denominator;

    // Script 13, corps #519/1957/2092: perceived hostile power, weighted by an officer's Warlike (+0x16A).
    internal static int PowerByWarlike(int power, int warlike) => unchecked(power * (100 - warlike)) / 100;

    // Script 10, corps #779 and its two copies: PersonInfo kind 19 + kind 15.
    internal static int EmployFocus(int talent, int fame) => unchecked(talent + fame);

    // Script 4 adds seven ints. The stock callers provide six hashes; the constructor initializes hash 7 to 0.
    // 0 hash means a literal argument 0 in CCallScriptNode, not a blackboard lookup.
    internal static int SumSeven(ReadOnlySpan<int> args)
    {
        if (args.Length != 7) throw new ArgumentException("Script 4 requires seven arguments.", nameof(args));
        int result = 0;
        foreach (int value in args) result = unchecked(result + value);
        return result;
    }
}

namespace San14Tenkazu;

// Remaining relational predicate composition. Native path/command/success helper internals are separate.
internal static class AiStructuralPredicates
{
    internal static bool Evaluate(int predicate, bool validInputs, bool a, bool b = false, bool c = false,
        int own = 0, int hostile = 0, int required = 0, short oldLevel = 0, short nextLevel = 0, int increment = 0) =>
        validInputs && (predicate switch
        {
            6 or 15 or 41 or 42 or 89 or 109 or 126 => a,
            60 => a || b,
            87 => a && b && c,
            112 => a && b,
            122 => a || b || c,
            133 => a && b,
            139 => a && AiRemainingPredicates.StateAdvantage(own, hostile, required),
            157 => a && AiCommandPredicates.NextSceneryLevel(oldLevel, nextLevel, increment),
            161 => a && b,
            162 or 164 => a,
            // Its wrapper rejects missing/invalid objects; its helper returns zero for both valid objects.
            163 => false,
            _ => throw new ArgumentOutOfRangeException(nameof(predicate)),
        });
}

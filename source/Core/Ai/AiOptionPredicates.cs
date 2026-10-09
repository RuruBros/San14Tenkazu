namespace San14Tenkazu;

// P148/P149 use the custom storage only when options +0xE58 is zero, otherwise the preset storage.
// Game dialog labels 7F3/7F4: enemy stratagems present / enemy alliance breaks frequent (ai_field_names.py).
internal static class AiOptionPredicates
{
    internal static bool Evaluate(int predicate, int presetMode, int customValue, int presetValue)
    {
        int value = presetMode == 0 ? customValue : presetValue;
        return predicate switch
        {
            148 => value == 0,
            149 => value == 1,
            _ => throw new ArgumentOutOfRangeException(nameof(predicate)),
        };
    }
}

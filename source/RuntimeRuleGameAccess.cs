namespace San14Tenkazu;

internal sealed record RuntimeRuleApplyResult(GameLayout Layout, GameSnapshot Snapshot, int Converted);
internal sealed record RuntimeRuleGameAccess(
    Func<GameIdentity, RuntimeRuleSettings?> Read,
    Func<GameIdentity, GameLayout, GamePace> Pace,
    Func<GameIdentity, RuntimeRuleSettings, string, bool, bool, RuntimeRuleApplyResult> Apply)
{
    internal static readonly RuntimeRuleGameAccess Live = new(GameMemory.ReadRuntimeRules, GameMemory.ReadPace,
        (identity, settings, journal, convert, reapply) =>
        {
            int count = GameMemory.ApplyRuntimeRules(identity, settings, journal, convert, reapply);
            var layout = GameMemory.ReadCurrentLayout(identity);
            return new(layout, GameMemory.ReadSnapshot(identity, layout), count);
        });
}

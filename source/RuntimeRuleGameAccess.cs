namespace San14Tenkazu;

internal sealed record RuntimeRuleApplyResult(GameLayout Layout, GameSnapshot Snapshot, int Converted, int Restored = 0, int TraitsRestored = 0, string? SaveName = null);
internal sealed record RuntimeRuleGameAccess(
    Func<GameIdentity, RuntimeRuleSettings?> Read,
    Func<GameIdentity, GameLayout, GamePace> Pace,
    Func<GameIdentity, RuntimeRuleSettings, string, bool, bool, RuntimeRuleApplyResult> Apply,
    Func<GameIdentity, GameLayout, RuntimeRuleSettings, bool>? NeedsExperienceReapply = null)
{
    internal static readonly RuntimeRuleGameAccess Live = new(GameMemory.ReadRuntimeRules, GameMemory.ReadPace,
        (identity, settings, journal, convert, reapply) =>
        {
            var counts = GameMemory.ApplyRuntimeRules(identity, settings, journal, convert, reapply);
            var layout = GameMemory.ReadCurrentLayout(identity);
            return new(layout, GameMemory.ReadSnapshot(identity, layout), counts.Converted, counts.Restored, counts.TraitsRestored, counts.SaveName);
        }, GameMemory.RuntimeRulesNeedReapply);
}

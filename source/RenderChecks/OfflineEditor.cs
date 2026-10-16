using San14Tenkazu;

internal static partial class RenderChecks
{
    // All render fixtures explicitly isolate automatic discovery from the user's game.
    private static readonly AutomaticGameAccess OfflineGameAccess = new(() => null, _ => false,
        _ => throw new InvalidOperationException("Unexpected offline rule read"),
        _ => throw new InvalidOperationException("Unexpected offline data read"),
        (_, _, _) => throw new InvalidOperationException("Unexpected offline preparation"));
    private static EditorWindow OfflineEditor(Func<bool>? registeredGameRunning = null, string? seasonStorageRoot = null,
        string? aiStorageRoot = null, string? ruleStorageRoot = null, RuleGameAccess? ruleAccess = null,
        RuntimeRuleGameAccess? runtimeRuleAccess = null, GamePreparationAccess? gamePreparationAccess = null)
        => new(registeredGameRunning, seasonStorageRoot, aiStorageRoot, ruleStorageRoot, ruleAccess,
            runtimeRuleAccess, gamePreparationAccess, OfflineGameAccess);
}

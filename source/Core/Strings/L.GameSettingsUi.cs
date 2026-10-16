using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class GameSettingsUi
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(GameSettingsUi)+"."+key);
        internal static string Menu => T();
        internal static string MenuTip => T();
        internal static string RuntimeMenu => T();
        internal static string RuntimeMenuTip => T();
        internal static string ConnectionChanged => T();
        internal static string PendingEdits => T();
        internal static string SavedWaitingForPause => T();
        internal static string SavedAndApplied(string p0, string p1) => Format(T(), (nameof(p0), p0), (nameof(p1), p1));
        internal static string AppliedLog(string p0, string p1, string p2, string p3) => Format(T(), (nameof(p0), p0), (nameof(p1), p1), (nameof(p2), p2), (nameof(p3), p3));
        internal static string AdoptedCurrentRules => T();
        internal static string UnknownRulesDeferred => T();
        internal static string WaitingForPause => T();
        internal static string Reapplied(string p0) => Format(T(), (nameof(p0), p0));
        internal static string AbilitiesRestored(string p0) => Format(T(), (nameof(p0), p0));
        internal static string TraitsRestored(string p0, string p1) => Format(T(), (nameof(p0), p0), (nameof(p1), p1));
        internal static string AutomaticApplyFailed => T() + " ";
        internal static string RetryAfterRefresh => " " + T();
        internal static string TraitsNine => T();
        internal static string TraitListNine => T();
        internal static string AbilityOriginal => T();
        internal static string AbilityCapTwoHundred => T();
        internal static string AbilityGrowthTwoHundred => T();
        internal static string ConvertProgress => T();
        internal static string Apply => T();
        internal static string Restore => T();
        internal static string RuntimeWindowTitle => T();
        internal static string AppliedHeading => T();
        internal static string SelectionHeading => T();
        internal static string PersistenceHelp => T();
        internal static string DetailedHelpHeading => T();
        internal static string DetailedHelp(string p0, string p1) => Format(T(), (nameof(p0), p0), (nameof(p1), p1));
        internal static string StateUnknown => T();
        internal static string AppliedSummary(string p0, string p1) => Format(T(), (nameof(p0), p0), (nameof(p1), p1));
        internal static string UnknownRules => T();
        internal static string CurrentStateRead => T();
        internal static string SavedSummary(string p0, string p1, string p2) => Format(T(), (nameof(p0), p0), (nameof(p1), p1), (nameof(p2), p2));
        internal static string SelectionUnknown => T();
        internal static string SelectionMatches => T();
        internal static string SelectionPending => T();
    }
}

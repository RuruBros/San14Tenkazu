using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class AutomaticSettings
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(AutomaticSettings) + "." + key);
        internal static string Help => T();
        internal static string LoadTiming => T();
        internal static string Waiting => T();
        internal static string Starting => T();
        internal static string RulesApplied => T();
        internal static string Saved => T();
        internal static string Failed => T();
        internal static string PendingEdits => T();
    }
}

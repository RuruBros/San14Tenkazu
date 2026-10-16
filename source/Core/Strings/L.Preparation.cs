using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class Preparation
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Preparation) + "." + key);
        internal static string Menu => T();
        internal static string Help => T();
        internal static string Launch => T();
        internal static string Cancel => T();
        internal static string Waiting => T();
        internal static string Ready => T();
        internal static string Cancelled => T();
        internal static string AlreadyRunning => T();
        internal static string Unsupported => T();
        internal static string ChangedRules => T();
        internal static string VerificationFailed => T();
        internal static string Timeout => T();
        internal static string Failed => T();
    }
}

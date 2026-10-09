using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class Navigation
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Navigation) + "." + key);

        internal static string Game => T();
        internal static string Data => T();
        internal static string GameTip => T();
        internal static string DataTip => T();
        internal static string BackToList => T();
        internal static string Resume => T();
        internal static string ResumeTip(object? category, object? name, object? count) => Format(T(), (nameof(category), category), (nameof(name), name), (nameof(count), count));
        internal static string Location(object? category, object? name) => Format(T(), (nameof(category), category), (nameof(name), name));
        internal static string AncientTool => T();
        internal static string AncientNote => T();
        internal static string RegisteredNote => T();
    }
}

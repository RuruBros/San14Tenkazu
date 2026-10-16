using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class DataUi
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(DataUi)+"."+key);
        internal static string OfficerAndItems => T();
        internal static string Combat => T();
        internal static string Domestic => T();
        internal static string PreviewBadge => T();
        internal static string PreviewTooltip => T();
        internal static string PreviewLegend => T();
    }
}

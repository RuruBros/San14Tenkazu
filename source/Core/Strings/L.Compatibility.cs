using System.Runtime.CompilerServices;
namespace San14Tenkazu;
internal static partial class L
{
    internal static class Compatibility
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Compatibility)+"."+key);
        internal static string Unavailable => T();
        internal static string CurveUnknown => T();
    }
}

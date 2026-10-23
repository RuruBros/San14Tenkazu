using System.Runtime.CompilerServices;
namespace San14Tenkazu;
internal static partial class L
{
    internal static class Compatibility
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Compatibility)+"."+key);
        internal static string Unavailable => T();
        internal static string MissingCastle(object? city) => Format(T(), (nameof(city), city));
        internal static string CurveUnknown => T();
        internal static string Import => T();
        internal static string ImportRules => T();
        internal static string MigrationUnsupported => T();
        internal static string ImportDraft => T();
        internal static string ImportSaved => T();
        internal static string ImportConfirm => T();
    }
}

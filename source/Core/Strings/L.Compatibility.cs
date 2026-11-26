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
        internal static string ImportChoices(object count, object conflicts) => Format(T(), (nameof(count), count), (nameof(conflicts), conflicts));
        internal static string ImportField(object name, object id, object field, object original, object baseline, object current, object incoming) =>
            Format(T(), (nameof(name), name), (nameof(id), id), (nameof(field), field), (nameof(original), original), (nameof(baseline), baseline), (nameof(current), current), (nameof(incoming), incoming));
        internal static string RuleDatasetChanged => T();
        internal static string BindLegacy(object file, object hash) => Format(T(), (nameof(file), file), (nameof(hash), hash));
        internal static string RuleDatasetSelected(object hash, object file) => Format(T(), (nameof(hash), hash), (nameof(file), file));
    }
}

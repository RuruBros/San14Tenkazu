using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class FileComparison
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(FileComparison) + "." + key);
        internal static string Title => T();
        internal static string Start => T();
        internal static string Target => T();
        internal static string Choose => T();
        internal static string Installed => T();
        internal static string Compare => T();
        internal static string Input => T();
        internal static string Field => T();
        internal static string Name => T();
        internal static string Result => T();
        internal static string All => T();
        internal static string Waiting => T();
        internal static string Loading => T();
        internal static string KnownFile(object id) => Format(T(), (nameof(id), id));
        internal static string OtherFile => T();
        internal static string DecodedFile => T();
        internal static string UnknownTarget => T();
        internal static string Detected(object id) => Format(T(), (nameof(id), id));
        internal static string MissingCache(object id) => Format(T(), (nameof(id), id));
        internal static string NeedOriginal(object id) => Format(T(), (nameof(id), id));
        internal static string WrongOriginal(object id) => Format(T(), (nameof(id), id));
        internal static string FileFilter => T();
        internal static string Summary(object total, object update, object fromBase, object fromTarget) => Format(T(), (nameof(total), total), (nameof(update), update), (nameof(fromBase), fromBase), (nameof(fromTarget), fromTarget));
        internal static string Same => T();
        internal static string Changed => T();
        internal static string Overlap => T();
        internal static string OnlyUpdate => T();
        internal static string MatchesUpdate => T();
        internal static string Identity => T();
        internal static string IdentityField => T();
        internal static string Table => T();
        internal static string ProfileFilter => T();
        internal static string DependentChoice(object table, object id, object field) => Format(T(), (nameof(table), table), (nameof(id), id), (nameof(field), field));
        internal static string PolicyFlag => T();
        internal static string DescriptionDigest => T();
        internal static string Permission(object id) => Format(T(), (nameof(id), id));
        internal static string CannotImport(object ids) => Format(T(), (nameof(ids), ids));
        internal static string SkippedField(object id, object field) => Format(T(), (nameof(id), id), (nameof(field), field));
        internal static string ReadOnly => T();
        internal static string FormatError => T();
        internal static string TableError => T();
        internal static string ManifestError => T();
        internal static string MissingEntry => T();
    }
}

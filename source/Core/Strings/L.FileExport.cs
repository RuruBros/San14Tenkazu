using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class FileExport
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(FileExport) + "." + key);
        internal static string Title => T();
        internal static string Instructions => T();
        internal static string ConflictChoices => T();
        internal static string Profile => T();
        internal static string Input => T();
        internal static string FileFilter => T();
        internal static string Preview => T();
        internal static string Save => T();
        internal static string ChooseFolder => T();
        internal static string Ready => T();
        internal static string Already => T();
        internal static string Identity => T();
        internal static string Dependency => T();
        internal static string Unsupported => T();
        internal static string ContainerOnly => T();
        internal static string NoChanges => T();
        internal static string InstallFolder => T();
        internal static string InputChanged => T();
        internal static string Verification => T();
        internal static string Summary(int apply, int already, int skip) => Format(T(), (nameof(apply), apply), (nameof(already), already), (nameof(skip), skip));
        internal static string Saved(string file, string hash, string backup) => Format(T(), (nameof(file), file), (nameof(hash), hash), (nameof(backup), backup));
    }
}

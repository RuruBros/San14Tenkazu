using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class InstallationAudit
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(InstallationAudit) + "." + key);
        internal static string Title => T();
        internal static string Description => T();
        internal static string GameFolder => T();
        internal static string PackageFolders => T();
        internal static string ClearPackages => T();
        internal static string Packages => T();
        internal static string Scan => T();
        internal static string Cancel => T();
        internal static string ChooseGame => T();
        internal static string NoneSelected => T();
        internal static string Ready => T();
        internal static string Reading => T();
        internal static string Cancelled => T();
        internal static string ShowMatches => T();
        internal static string Evidence => T();
        internal static string Installed => T();
        internal static string Result => T();
        internal static string File => T();
        internal static string DepotClaims => T();
        internal static string Content => T();
        internal static string PackageFile => T();
        internal static string DiskMatch => T();
        internal static string Runtime => T();
        internal static string Sources => T();
        internal static string Source => T();
        internal static string Matches => T();
        internal static string Changed => T();
        internal static string Added => T();
        internal static string Missing => T();
        internal static string Unknown => T();
        internal static string NoDiskFile => T();
        internal static string DepotOverlap => T();
        internal static string ContentFound => T();
        internal static string NoMatch => T();
        internal static string DiskNames => T();
        internal static string PackageNames => T();
        internal static string PackageOverlap => T();
        internal static string RelativeOverlap => T();
        internal static string NameOverlap => T();
        internal static string Unobserved => T();
        internal static string DepotScope => T();
        internal static string ScopeDetail => T();
        internal static string ConflictingClaims => T();
        internal static string KnownMatch => T();
        internal static string Shared => T();
        internal static string SharedUnresolved => T();
        internal static string Read => T();
        internal static string InvalidManifest => T();
        internal static string WrongInstallation => T();
        internal static string NoDepots => T();
        internal static string InvalidPin => T();
        internal static string MissingCache => T();
        internal static string Encrypted => T();
        internal static string PinMismatch => T();
        internal static string SteamState => T();
        internal static string InstallationChanged => T();
        internal static string LinkSkipped => T();
        internal static string FileChanged => T();
        internal static string OverlappingRoots => T();
        internal static string UnreadablePath => T();
        internal static string IncompleteDepots => T();
        internal static string NoContentHash => T();
        internal static string IncompleteDisk => T();
    }
}

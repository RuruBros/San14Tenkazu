using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class StatusChange
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(StatusChange) + "." + key);
        internal static string Title => T();
        internal static string Mode => T();
        internal static string Force => T();
        internal static string Captor => T();
        internal static string Detention => T();
        internal static string Apply => T();
        internal static string InvalidMode => T();
        internal static string Unavailable => T();
        internal static string Unchanged => T();
        internal static string CaptureOnly => T();
        internal static string Generated => T();
        internal static string CaptureNote => T();
        internal static string UndoNote => T();
        internal static string Completed => T();
        internal static string Unconfirmed => T();
        internal static string CaptureSummary(string officer, string before, string force, string place) =>
            Format(T(), (nameof(officer), officer), (nameof(before), before), (nameof(force), force), (nameof(place), place));
        internal static string CaptureEvidence(string officer, string force, string home, int place, int corps, int origin) =>
            Format(T(), (nameof(officer), officer), (nameof(force), force), (nameof(home), home),
                (nameof(place), place), (nameof(corps), corps), (nameof(origin), origin));
    }
}

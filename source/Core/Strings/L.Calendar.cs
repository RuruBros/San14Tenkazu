using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class Calendar
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Calendar) + "." + key);
        internal static string Title => T();
        internal static string Tip => T();
        internal static string Year => T();
        internal static string Month => T();
        internal static string Day => T();
        internal static string Apply => T();
        internal static string Note => T();
        internal static string Range => T();
        internal static string InvalidClock => T();
        internal static string Stale => T();
        internal static string WriteFailed => T();
        internal static string RollbackFailed => T();
        internal static string VerifiedOnly => T();
        internal static string PauseFirst => T();
        internal static string Unchanged => T();
        internal static string Date(object? year, object? month, object? day) => Format(T(), (nameof(year), year), (nameof(month), month), (nameof(day), day));
        internal static string Preview(object? before, object? after) => Format(T(), (nameof(before), before), (nameof(after), after));
        internal static string Requested(object? pid, object? state, object? before, object? after, object? hour) =>
            Format(T(), (nameof(pid), pid), (nameof(state), state), (nameof(before), before), (nameof(after), after), (nameof(hour), hour));
        internal static string Completed(object? date, object? hour) => Format(T(), (nameof(date), date), (nameof(hour), hour));
    }
}

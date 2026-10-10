using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class Revival
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Revival) + "." + key);
        internal static string Title => T();
        internal static string AppearanceTitle => T();
        internal static string Mode => T();
        internal static string Serving => T();
        internal static string Free => T();
        internal static string DeathYear => T();
        internal static string Revive => T();
        internal static string Appear => T();
        internal static string InactiveOnly => T();
        internal static string InvalidMode => T();
        internal static string GeneratedOfficer => T();
        internal static string HasUnit => T();
        internal static string ValuesNote => T();
        internal static string AppearanceNote => T();
        internal static string UndoNote => T();
        internal static string AlreadyListed => T();
        internal static string ListInvalid => T();
        internal static string ListFull => T();
        internal static string Unconfirmed => T();
        internal static string Completed => T();
        internal static string DeathYearAfter(object? year) => Format(T(), (nameof(year), year));
        internal static string Summary(object? officer, object? before, object? status, object? place, object? year) =>
            Format(T(), (nameof(officer), officer), (nameof(before), before), (nameof(status), status),
                (nameof(place), place), (nameof(year), year));
        internal static string Evidence(object? officer, object? status, object? corps, object? home, object? year, object? count) =>
            Format(T(), (nameof(officer), officer), (nameof(status), status), (nameof(corps), corps),
                (nameof(home), home), (nameof(year), year), (nameof(count), count));
    }
}

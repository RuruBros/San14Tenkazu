using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class Independence
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Independence) + "." + key);
        internal static string Button => T();
        internal static string ButtonTip => T();
        internal static string Confirm(object? place, object? governor) => Format(T(), (nameof(place), place), (nameof(governor), governor));
        internal static string Request(object? place, object? governor, object? force, object? slot) => Format(T(), (nameof(place), place), (nameof(governor), governor), (nameof(force), force), (nameof(slot), slot));
        internal static string Done(object? place, object? governor, object? force) => Format(T(), (nameof(place), place), (nameof(governor), governor), (nameof(force), force));
        internal static string Verified(object? force, object? corps, object? governor, object? officers) => Format(T(), (nameof(force), force), (nameof(corps), corps), (nameof(governor), governor), (nameof(officers), officers));
        internal static string NoCity => T();
        internal static string NoForce => T();
        internal static string NoGovernor => T();
        internal static string AlreadyRuler => T();
        internal static string InvalidGovernor => T();
        internal static string OutsideCity => T();
        internal static string SpecialOfficer => T();
        internal static string RulerCity => T();
        internal static string NoSlot => T();
        internal static string NoCorps => T();
        internal static string PauseFirst => T();
        internal static string Changed => T();
        internal static string Refused => T();
        internal static string NotRun(object? why) => Format(T(), (nameof(why), why));
        internal static string Unconfirmed => T();
    }
}

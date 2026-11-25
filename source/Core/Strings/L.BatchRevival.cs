using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class BatchRevival
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(BatchRevival) + "." + key);
        internal static string Button => T();
        internal static string ButtonTip => T();
        internal static string Place => T();
        internal static string RandomCity => T();
        internal static string FixedCity => T();
        internal static string RandomStatus => T();
        internal static string YearsBefore => T();
        internal static string YearsAfter => T();
        internal static string IncludeHidden => T();
        internal static string PickUnit => T();
        internal static string Pick => T();
        internal static string PickTip => T();
        internal static string Shuffle => T();
        internal static string ShuffleTip => T();
        internal static string InvalidCount => T();
        internal static string NoCities => T();
        internal static string NoCandidates => T();
        internal static string ConfirmGoOn => T();
        internal static string Stop => T();
        internal static string StopTip => T();
        internal static string SelectedCount(object? count) => Format(T(), (nameof(count), count));
        internal static string Skipped(object? count) => Format(T(), (nameof(count), count));
        internal static string InvalidYears(object? max) => Format(T(), (nameof(max), max));
        internal static string Refused(object? officer, object? why) => Format(T(), (nameof(officer), officer), (nameof(why), why));
        internal static string Confirm(object? count, object? names) => Format(T(), (nameof(count), count), (nameof(names), names));
        internal static string Checking(object? total) => Format(T(), (nameof(total), total));
        internal static string Progress(object? done, object? total, object? officer) =>
            Format(T(), (nameof(done), done), (nameof(total), total), (nameof(officer), officer));
        internal static string Completed(object? done, object? total) => Format(T(), (nameof(done), done), (nameof(total), total));
        internal static string Refreshing(object? done, object? total) => Format(T(), (nameof(done), done), (nameof(total), total));
        internal static string Done(object? count) => Format(T(), (nameof(count), count));
        internal static string Stopped(object? done, object? total) => Format(T(), (nameof(done), done), (nameof(total), total));
        internal static string Failed(object? done, object? total, object? why) =>
            Format(T(), (nameof(done), done), (nameof(total), total), (nameof(why), why));
    }
}

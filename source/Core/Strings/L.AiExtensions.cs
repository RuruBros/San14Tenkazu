using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class AiExtensions
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(AiExtensions) + "." + key);
        internal static string Basic => T();
        internal static string Extended => T();
        internal static string Summary => T();
        internal static string SupplyProtection => T();
        internal static string SupplyExplanation => T();
        internal static string Failure => T();
        internal static string Return => T();
        internal static string Hold => T();
        internal static string Scope => T();
        internal static string AiAndDelegated => T();
        internal static string AiOnly => T();
        internal static string SavedNote => T();
        internal static string Off => T();
        internal static string On => T();
        internal static string InvalidSettings => T();
        internal static string UnsupportedTree => T();
        internal static string ForeignNode => T();
        internal static string NotApplied => T();
        internal static string Applied => T();
        internal static string DefaultsTip => T();
        internal static string Counts(object? checks, object? recovered, object? returns, object? holds) =>
            Format(T(), (nameof(checks), checks), (nameof(recovered), recovered), (nameof(returns), returns), (nameof(holds), holds));
        internal static string Failed(object? why) => Format(T(), (nameof(why), why));
    }
}

using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class EditRanges
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(EditRanges) + "." + key);
        internal static string Files(object paths) => Format(T(), (nameof(paths), paths));
        internal static string Settings(object values) => Format(T(), (nameof(values), values));
        internal static string Incomplete => T();
        internal static string NoSettings => T();
        internal static string NoFiles => T();
        internal static string CityBasis => T();
        internal static string CityFill => T();
        internal static string CityFillTip => T();
        internal static string CityHold => T();
        internal static string AboveOfficial(object field, object value, object cap) => Format(T(), (nameof(field), field), (nameof(value), value), (nameof(cap), cap));
        internal static string FormationBasis => T();
        internal static string FormationHold => T();
        internal static string ImportHold(object fields) => Format(T(), (nameof(fields), fields));
        internal static string EffectBasis => T();
        internal static string EffectHold(object kind) => Format(T(), (nameof(kind), kind));
        internal static string EventSlotHold(object kind, object value) => Format(T(), (nameof(kind), kind), (nameof(value), value));
    }
}

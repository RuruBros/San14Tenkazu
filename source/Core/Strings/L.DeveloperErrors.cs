using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class DeveloperErrors
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(DeveloperErrors)+"."+key);
        internal static string PauseRequired => T();
        internal static string CompatibilityChanged => T();
        internal static string CompatibilityCancelled => T();
        internal static string NoticeReadChanged => T() + " ";
        internal static string NoticeReadUnconfirmed => T();
        internal static string NoticePointerMissing => T();
        internal static string NoticeReadIncomplete => T();
        internal static string NoticeCodeChanged => T();
        internal static string NoticeCountInvalid => T();
        internal static string MultipleNotices => T();
        internal static string NoOpenNotice => T();
        internal static string NoticeClassChanged => T();
        internal static string NoticeTextChanged => T();
        internal static string TextWidgetChanged => T();
        internal static string TextVtableChanged => T();
        internal static string FontGetterRangeChanged => T();
        internal static string StringStorageChanged => T();
        internal static string StringGetterChanged => T();
        internal static string StringIndexInvalid => T();
        internal static string StringEntryChanged => T();
        internal static string FontScaleInvalid => T();
        internal static string FontFieldsUnverified => T();
        internal static string StringBoundaryChanged => T();
        internal static string StringUnterminated => T();
        internal static string StringMissing => T();
        internal static string StringFieldsUnverified => T();
        internal static string RenderClassChanged => T();
        internal static string RenderScaleInvalid => T();
        internal static string RenderObjectMissing => T();
        internal static string WidgetScaleInvalid => T();
        internal static string PauseForTraits => T();
        internal static string TraitReadChanged => T();
        internal static string TraitFinalCheckFailed => T() + " ";
        internal static string TraitReadIncomplete => T();
        internal static string TraitReadsDiffer => T();
    }
}

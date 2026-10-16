using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class RuntimeErrors
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(RuntimeErrors)+"."+key);
        internal static string ExperienceGrowthHint => T();
        internal static string GrowthUnknown => T();
        internal static string PauseRequired => T();
        internal static string TraitOwnershipChanged => T();
        internal static string Loading => T();
        internal static string PaceChanged => T();
        internal static string OfficerTableChanged => T();
        internal static string OfficerConversionError(string p0, string p1, string p2) => Format(T(), (nameof(p0), p0), (nameof(p1), p1), (nameof(p2), p2));
        internal static string ConversionRequired => T();
        internal static string UnusedAllocationReleaseFailed => T();
        internal static string AllocationFailed => T();
        internal static string AllocationProtectionFailed => T();
        internal static string InitializationReleaseFailed => T();
        internal static string AllocationOwnershipChanged => T();
        internal static string PageProtectionFailed => T();
        internal static string PageProtectionUnconfirmed => T();
        internal static string MemoryProtectionFailed => T();
        internal static string InstructionCacheFailed => T();
        internal static string ProtectionRestoreFailed(string p0) => Format(T(), (nameof(p0), p0));
        internal static string RuntimeStateUnknown => T();
        internal static string SuspendFailed(string p0) => Format(T(), (nameof(p0), p0));
        internal static string ThreadsChanged => T();
        internal static string FunctionExecuting => T();
        internal static string ThreadQueryFailed(string p0) => Format(T(), (nameof(p0), p0));
        internal static string StackRangeUnknown => T();
        internal static string ReturnAddressExecuting => T();
        internal static string ResumeFailed(string p0) => Format(T(), (nameof(p0), p0));
        internal static string RecoveryIncomplete => T();
        internal static string EmptyRecoveryRecord => T();
        internal static string TraitRuleStateChanged => T();
        internal static string PatchConflict(string p0) => Format(T(), (nameof(p0), p0));
        internal static string ApplyAndRestoreFailed => T();
        internal static string MemoryChangedBeforeApply(string p0) => Format(T(), (nameof(p0), p0));
        internal static string WriteReadbackFailed(string p0) => Format(T(), (nameof(p0), p0));
        internal static string RestoreReadbackFailed(string p0) => Format(T(), (nameof(p0), p0));
        internal static string PreferenceInvalid => T();
        internal static string PreferenceBuildChanged => T();
        internal static string PreferenceEmpty => T();
        internal static string NineTraitsRequired => T();
        internal static string ExperienceOutOfRange => T();
        internal static string ExperienceCurveMismatch(string p0, string p1) => Format(T(), (nameof(p0), p0), (nameof(p1), p1));
        internal static string TraitImageRangeInvalid => T();
        internal static string TraitRelocationInvalid => T();
        internal static string TraitPointerInvalid => T();
        internal static string TraitRelocationMissing(string p0) => Format(T(), (nameof(p0), p0));
        internal static string TraitOriginalConflict(string p0) => Format(T(), (nameof(p0), p0));
        internal static string TraitCodeChanged => T();
        internal static string TraitAllocationChanged => T();
        internal static string TraitColumnsChanged => T();
        internal static string TraitExpansionTooLarge => T();
    }
}

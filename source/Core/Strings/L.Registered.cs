using System.Runtime.CompilerServices;

namespace San14Tenkazu;

internal static partial class L
{
    internal static class Registered
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Registered) + "." + key);
        internal static string Title => T();
        internal static string Button => T();
        internal static string RunningTitle => T();
        internal static string ReadOnlyTitle => T();
        internal static string Tip => T();
        internal static string RunningTip => T();
        internal static string Note => T();
        internal static string RunningNote => T();
        internal static string Open => T();
        internal static string Save => T();
        internal static string SaveBlocked => T();
        internal static string Basic => T();
        internal static string Traits => T();
        internal static string Relations => T();
        internal static string Biography => T();
        internal static string Surname => T();
        internal static string GivenName => T();
        internal static string StyleName => T();
        internal static string Face => T();
        internal static string Birth => T();
        internal static string Death => T();
        internal static string Clan => T();
        internal static string ClanMembers => T();
        internal static string SwornMembers => T();
        internal static string SwornNumber => T();
        internal static string GroupNumbers => T();
        internal static string GroupNote => T();
        internal static string AddRelation => T();
        internal static string SelfRelation => T();
        internal static string DuplicateRelation => T();
        internal static string DuplicateSlot => T();
        internal static string PersonalityLimit => T();
        internal static string PersonalityNote => T();
        internal static string TacticNote => T();
        internal static string FaceNote => T();
        internal static string CurrentGameNote => T();
        internal static string Father => T();
        internal static string Mother => T();
        internal static string Voice => T();
        internal static string Male => T();
        internal static string Female => T();
        internal static string Search => T();
        internal static string NoFile => T();
        internal static string FileFilter => T();
        internal static string BadFormat => T();
        internal static string ReservedChanged => T();
        internal static string NameRequired => T();
        internal static string FileChanged => T();
        internal static string VerifyFailed => T();
        internal static string GameRunning => T();
        internal static string Unsaved => T();
        internal static string BadText(object? field) => Format(T(), (nameof(field), field));
        internal static string TextLimit(object? field, object? count) => Format(T(), (nameof(field), field), (nameof(count), count));
        internal static string RelationLimit(object? count) => Format(T(), (nameof(count), count));
        internal static string Total(object? total) => Format(T(), (nameof(total), total));
        internal static string Saved(object? count, object? backup) => Format(T(), (nameof(count), count), (nameof(backup), backup));
        internal static string Loaded(object? count, object? path) => Format(T(), (nameof(count), count), (nameof(path), path));
        internal static string Count(object? count, object? changed) => Format(T(), (nameof(count), count), (nameof(changed), changed));
        internal static string VoiceNumber(object? number) => Format(T(), (nameof(number), number));
        internal static string Slot(object? slot, object? name, object? total) => Format(T(), (nameof(slot), slot), (nameof(name), name), (nameof(total), total));
    }
}

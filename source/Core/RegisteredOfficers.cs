using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace San14Tenkazu;

// CEditPersonData vtable +0x28 (0x219410 -> 0x219620): version 4's packed file record,
// not CPersonData's memory layout. 0x20A800 copies its five abilities into CPersonData +0xEC.
// The unused word at file +0x27 and every other reserved byte are deliberately preserved.
internal static class RegisteredOfficerFields
{
    internal const int Stride = 0x45D, Slots = 150, FirstId = 3001, AbilityOffset = 0x4A;
    // 0x2305D0 returns zero past slot 4, although the packed record has nine words.
    internal const int PersonalityActiveMax = 5;
    internal sealed record Text(string Label, int Offset, int Characters);
    internal static readonly Text Surname = new(L.Registered.Surname, 4, 2), GivenName = new(L.Registered.GivenName, 10, 2),
        StyleName = new(L.Registered.StyleName, 16, 2), Biography = new(L.Registered.Biography, 0x73, 500);
    internal static readonly Text[] Texts = [Surname, GivenName, StyleName, Biography];
    private static FieldDef Byte(string label, int offset, int max) => new(label, RecordKind.Officer, offset, FieldKind.Byte, 0, max);
    private static FieldDef Word(string label, int offset, int max) => new(label, RecordKind.Officer, offset, FieldKind.UInt16, 0, max);
    internal static readonly FieldDef Face = new(L.Registered.Face, RecordKind.Officer, 0x16, FieldKind.UInt16, 0, ushort.MaxValue, L.Registered.FaceNote), Gender = Byte(L.Officer.Gender, 0x18, 1),
        Birth = Word(L.Registered.Birth, 0x19, 350), Death = Word(L.Registered.Death, 0x1B, 350),
        Clan = Word(L.Registered.Clan, 0x1D, 6000), Father = Word(L.Registered.Father, 0x1F, 6000),
        Mother = Word(L.Registered.Mother, 0x21, 6000), Spouse = Word(L.Officer.Spouse, 0x23, 6000),
        Sworn = Word(L.Registered.SwornNumber, 0x25, 6000), Compatibility = Byte(L.Officer.Compatibility, 0x29, 149),
        Formations = new(L.Officer.Formations, RecordKind.Officer, 0x61, FieldKind.UInt32, 0, uint.MaxValue),
        Ideology = Byte(L.Officer.Ideology, 0x6F, 10), Policy = Byte(L.Officer.Policy, 0x70, 120),
        PolicyLevel = Byte(L.Officer.PolicyLevel, 0x71, 6), Voice = Byte(L.Registered.Voice, 0x72, 16);
    internal static readonly FieldDef[] Abilities = OfficerFields.Abilities.Select((f, i) => Byte(f.Label, AbilityOffset + i, 100)).ToArray();
    internal static readonly FieldDef[] Personalities = Enumerable.Range(0, 9).Select(i => Word(L.Fields.PersonalitySlot(i + 1), 0x4F + i * 2, 355)).ToArray();
    internal static readonly FieldDef[] Tactics = Enumerable.Range(0, 10).Select(i => Byte(L.Fields.TacticSlot(i + 1), 0x65 + i, 200)).ToArray();
    internal static readonly FieldDef[] Likes = Enumerable.Range(0, 8).Select(i => Word(L.Fields.LikeSlot(i + 1), 0x2A + i * 2, 6000)).ToArray();
    internal static readonly FieldDef[] Dislikes = Enumerable.Range(0, 8).Select(i => Word(L.Fields.DislikeSlot(i + 1), 0x3A + i * 2, 6000)).ToArray();
    internal static readonly FieldDef[] Numbers = [Face, Gender, Birth, Death, Clan, Father, Mother, Spouse, Sworn, Compatibility,
        .. Abilities, .. Personalities, Formations, .. Tactics, Ideology, Policy, PolicyLevel, Voice, .. Likes, .. Dislikes];
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);

    internal static string ReadText(ReadOnlySpan<byte> record, Text field)
    {
        ReadOnlySpan<byte> data = record.Slice(field.Offset, (field.Characters + 1) * 2);
        int end = 0;
        while (end < data.Length && BinaryPrimitives.ReadUInt16LittleEndian(data[end..]) != 0) end += 2;
        if (end == data.Length) throw new InvalidDataException(L.Registered.BadText(field.Label));
        return Utf16.GetString(data[..end]);
    }

    internal static void WriteText(byte[] record, Text field, string value)
    {
        if (value.Length > field.Characters || value.Contains('\0')) throw new ArgumentException(L.Registered.TextLimit(field.Label, field.Characters));
        byte[] encoded = Utf16.GetBytes(value);
        if (ReadText(record, field) == value) return;
        record.AsSpan(field.Offset, (field.Characters + 1) * 2).Clear();
        encoded.CopyTo(record, field.Offset);
    }
}

internal sealed record RegisteredOfficer(int Slot, int Id, byte[] Data)
{
    internal string Name => RegisteredOfficerFields.ReadText(Data, RegisteredOfficerFields.Surname) + RegisteredOfficerFields.ReadText(Data, RegisteredOfficerFields.GivenName);
    internal int Total => RegisteredOfficerFields.Abilities.Sum(f => (int)f.Read(Data));
}

internal sealed class RegisteredOfficerFile
{
    private readonly Func<bool> gameIsRunning;
    internal const string FileName = "editdataexceKO.s14";
    internal string Path { get; }
    internal byte[] Expected { get; }
    internal RegisteredOfficer[] Officers { get; }
    internal RegisteredOfficerFile(string path, byte[] data, Func<bool>? gameIsRunning = null)
    {
        this.gameIsRunning = gameIsRunning ?? GameIsRunning;
        Path = System.IO.Path.GetFullPath(path);
        Expected = (byte[])data.Clone();
        Officers = Parse(data);
    }
    internal static RegisteredOfficerFile Read(string path, Func<bool>? gameIsRunning = null) => new(path, File.ReadAllBytes(path), gameIsRunning);

    internal static RegisteredOfficer[] Parse(byte[] data)
    {
        if (data.Length != RegisteredOfficerFields.Stride * RegisteredOfficerFields.Slots) throw new InvalidDataException(L.Registered.BadFormat);
        var officers = new List<RegisteredOfficer>();
        for (int slot = 0; slot < RegisteredOfficerFields.Slots; slot++)
        {
            byte[] record = data.AsSpan(slot * RegisteredOfficerFields.Stride, RegisteredOfficerFields.Stride).ToArray();
            int id = BinaryPrimitives.ReadUInt16LittleEndian(record);
            if (BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(2)) != 4 || id != 0 && id != RegisteredOfficerFields.FirstId + slot)
                throw new InvalidDataException(L.Registered.BadFormat);
            if (id == 0) continue;
            foreach (var field in RegisteredOfficerFields.Texts) _ = RegisteredOfficerFields.ReadText(record, field);
            officers.Add(new(slot, id, record));
        }
        return officers.ToArray();
    }

    internal byte[] Compose(IEnumerable<RegisteredOfficer> edited)
    {
        byte[] result = (byte[])Expected.Clone();
        var seen = new HashSet<int>();
        foreach (RegisteredOfficer officer in edited)
        {
            RegisteredOfficer original = Officers.Single(o => o.Id == officer.Id && o.Slot == officer.Slot);
            if (!seen.Add(officer.Id) || officer.Data.Length != RegisteredOfficerFields.Stride) throw new InvalidDataException(L.Registered.BadFormat);
            var allowed = new bool[RegisteredOfficerFields.Stride];
            foreach (var field in RegisteredOfficerFields.Numbers)
            {
                long value = field.Read(officer.Data);
                // An untouched value from another game's editor is preserved; a new one must fit the game's range.
                if (value != field.Read(original.Data) && (value < field.Min || value > field.Max)) throw new ArgumentOutOfRangeException(field.Label, field.RangeMessage);
                Array.Fill(allowed, true, field.Offset, field.Size);
            }
            foreach (var field in RegisteredOfficerFields.Texts)
            {
                _ = RegisteredOfficerFields.ReadText(officer.Data, field);
                Array.Fill(allowed, true, field.Offset, (field.Characters + 1) * 2);
            }
            if (((RegisteredOfficerFields.Formations.Read(officer.Data) ^ RegisteredOfficerFields.Formations.Read(original.Data)) & ~((1L << 21) - 1)) != 0)
                throw new InvalidDataException(L.Registered.ReservedChanged);
            if (Enumerable.Range(0, allowed.Length).Any(i => !allowed[i] && officer.Data[i] != original.Data[i])) throw new InvalidDataException(L.Registered.ReservedChanged);
            if (string.IsNullOrWhiteSpace(officer.Name)) throw new InvalidDataException(L.Registered.NameRequired);
            RegisteredOfficerRelations.Validate(officer, original);
            officer.Data.CopyTo(result, officer.Slot * RegisteredOfficerFields.Stride);
        }
        return result;
    }

    // The caller checks the running game as well. The original is locked against writers until replacement;
    // both the exact backup and replacement are flushed first. The backup can be on another volume;
    // ReplaceFile's native backup argument cannot, so replacement uses the separately secured copy.
    internal string Save(byte[] replacement, string backupDirectory)
    {
        if (!Compose(Parse(replacement)).AsSpan().SequenceEqual(replacement)) throw new InvalidDataException(L.Registered.ReservedChanged);
        if (replacement.AsSpan().SequenceEqual(Expected)) return "";
        if (gameIsRunning()) throw new InvalidOperationException(L.Registered.GameRunning);
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        string temporary = System.IO.Path.Combine(directory, ".tenkazu-" + Guid.NewGuid().ToString("N") + ".tmp");
        string backup = System.IO.Path.Combine(backupDirectory, System.IO.Path.GetFileName(Path) + "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".bak");
        try
        {
            using (var original = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                byte[] current = new byte[original.Length];
                original.ReadExactly(current);
                if (!current.AsSpan().SequenceEqual(Expected)) throw new InvalidOperationException(L.Registered.FileChanged);
                Directory.CreateDirectory(backupDirectory);
                using (var recovery = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { recovery.Write(current); recovery.Flush(true); }
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(replacement); output.Flush(true); }
                File.Replace(temporary, Path, null);
            }
            if (!File.ReadAllBytes(Path).AsSpan().SequenceEqual(replacement))
            {
                File.Copy(backup, Path, true);
                throw new IOException(L.Registered.VerifyFailed);
            }
            return backup;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool GameIsRunning()
    {
        bool running = false;
        foreach (Process process in Process.GetProcesses())
            using (process)
                try { running |= IsGameProcessName(process.ProcessName); }
                catch (InvalidOperationException) { }
        return running;
    }
    internal static bool IsGameProcessName(string name) => Regex.IsMatch(name, @"^SAN14(?:CE|PK)?(?:_(?:KO|SC|TC|EN))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Steam installations and accounts are discovered from their directories; no account is silently chosen.
    internal static string[] FindFiles(string? gameDirectory, IEnumerable<string>? configuredRoots = null)
    {
        var found = new List<string>();
        foreach (string root in SteamInstallation.FindRoots(gameDirectory, configuredRoots))
        {
            string userdata = System.IO.Path.Combine(root, "userdata");
            if (!Directory.Exists(userdata)) continue;
            string[] accounts;
            try { accounts = Directory.GetDirectories(userdata); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string account in accounts)
            {
                string file = System.IO.Path.Combine(account, SteamInstallation.AppId, "remote", FileName);
                if (File.Exists(file)) found.Add(file);
            }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
    }
}

// Offline choices captured from the validated CE Korean runtime tables; live catalog names override them.
internal static class RegisteredOfficerNames
{
    private static readonly Lazy<Dictionary<string, GameName[]>> names = new(() =>
    {
        using Stream stream = typeof(RegisteredOfficerNames).Assembly.GetManifestResourceStream("RegisteredNames.txt")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.TrimEnd('\r').Split('\t'))
            .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Select(p => new GameName(int.Parse(p[1]), p[2], p.Length > 3 ? p[3] : "")).ToArray());
    });
    internal static GameName[] Get(string kind) => names.Value.GetValueOrDefault(kind, []);
}

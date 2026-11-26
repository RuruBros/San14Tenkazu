using System.Buffers.Binary;
using System.Security.Cryptography;

namespace San14Tenkazu;

internal enum FileEditStatus { Apply, Already, Conflict, Identity, Dependency, RangeHeld, Unsupported }
internal sealed record FileRuleEdit(RuleKey Key, RuleFileCell? Cell, int Baseline, int Desired, FileEditStatus Status);
internal sealed record RuleFileOutput(string File, string Original, string Sha256);

internal sealed class RuleFileExport
{
    internal string Input { get; }
    internal string InputHash { get; }
    private readonly string profilePath, profileHash;
    private readonly RuleProfile source;
    private readonly RuleFileSnapshot target;
    internal IReadOnlyList<FileRuleEdit> Edits { get; }
    internal bool CanExport => Edits.Any(e => e.Status is FileEditStatus.Apply or FileEditStatus.Already);
    private const string FileName = "fixdataexce.s14";

    private RuleFileExport(string input, string inputHash, string profile, string stamp,
        RuleProfile source, RuleFileSnapshot target, IReadOnlySet<RuleKey>? choices = null)
    {
        Input = input; InputHash = inputHash; profilePath = profile; profileHash = stamp;
        this.source = source; this.target = target;
        Edits = Array.AsReadOnly(Classify(source, target, choices));
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    internal static RuleFileExport Preview(string input, string profile)
    {
        input = Path.GetFullPath(input);
        profile = Path.GetFullPath(profile);
        using var profileGuard = OpenRead(profile);
        if (profileGuard.Length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException(L.Rules.BadProfile);
        var saved = new RuleProfileFile(profile).Read();
        RuleProfile source = saved.Profile ?? throw new InvalidDataException(L.Rules.BadProfile);
        // This path has no executable target. Read() validated the saved schema and catalog;
        // the destination's file layout, row identities and dependencies establish which cells can move.
        using var inputGuard = OpenRead(input);
        var data = FormationFileData.Read(input);
        if (data.Decoded) throw new InvalidDataException(L.FileExport.ContainerOnly);
        if (inputGuard.Length != data.Length) throw new IOException(L.FileExport.InputChanged);
        byte[] bytes = new byte[checked((int)data.Length)];
        inputGuard.ReadExactly(bytes);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != data.Sha256) throw new IOException(L.FileExport.InputChanged);
        // A translated header can still be identified from the Korean official formation names.
        if (!bytes.AsSpan(0x1a6).StartsWith(RuleFileContainer.KoreanTitle)
            && !OfficialFormation.Known.Any(reference => data.Rows.Any(row =>
                reference.Rows[row.Id - 1].NameBytes == row.NameBytes && reference.Rows[row.Id - 1].Kind == row.Kind)))
            throw new InvalidDataException(L.FileComparison.FormatError);
        var target = RuleFileSnapshot.Read(source.Build, data, Decode(bytes));
        return new(input, data.Sha256, profile, saved.Stamp, source, target);
    }

    private static byte[] Decode(byte[] bytes)
    {
        // Equal lengths designate the alternate stored form in CE. Only compressed containers have
        // been established for this path. FormationFileData already validated the exact outer bounds.
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(RuleFileContainer.Block + 4)) ==
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(RuleFileContainer.Block + 8)))
            throw new InvalidDataException(L.FileComparison.FormatError);
        return LwcCodec.Decode(bytes, RuleFileContainer.Block, requireCompletePayload: true);
    }

    internal RuleFileExport WithConflictChoices(IReadOnlySet<RuleKey> choices) =>
        new(Input, InputHash, profilePath, profileHash, source, target, choices);

    internal static FileRuleEdit[] Classify(RuleProfile source, RuleFileSnapshot target, IReadOnlySet<RuleKey>? choices = null)
    {
        var compatibility = new RuleImportCompatibility(source, target.Profile);
        var edits = new Dictionary<RuleKey, FileRuleEdit>();
        foreach (var edit in source.Overrides)
        {
            var key = edit.Key;
            int baseline = source.Default(key);
            target.Cells.TryGetValue(key, out var cell);
            var state = key.Table is not ("formation" or "tactic" or "policy" or "policyeffect") ? FileEditStatus.Unsupported
                : cell == null ? FileEditStatus.Identity
                : compatibility.RangeHeld(key) ? FileEditStatus.RangeHeld
                : !compatibility.Eligible(edit) ? FileEditStatus.Identity
                : cell.Value == edit.Value ? FileEditStatus.Already
                : cell.Value == baseline ? FileEditStatus.Apply : FileEditStatus.Conflict;
            edits.Add(key, new(key, cell, baseline, edit.Value, state));
        }
        if (choices != null)
            foreach (var key in choices)
            {
                if (!edits.TryGetValue(key, out var e) || e.Status != FileEditStatus.Conflict)
                    throw new InvalidDataException(L.Rules.Incompatible);
                edits[key] = e with { Status = FileEditStatus.Apply };
            }
        var eligible = edits.Values.Where(e => e.Status is FileEditStatus.Apply or FileEditStatus.Already).ToArray();
        var candidates = eligible.ToDictionary(e => e.Key, e => e.Desired);
        compatibility.KeepCompatible(candidates, target.Profile.Default);
        foreach (var e in eligible)
            if (!candidates.ContainsKey(e.Key)) edits[e.Key] = e with { Status = FileEditStatus.Dependency };
        return edits.Values.ToArray();
    }

    internal RuleFileOutput Save(string parentDirectory, string? gameDirectory)
    {
        if (!CanExport) throw new InvalidOperationException(L.FileExport.NoChanges);
        string parent = PhysicalDirectory(parentDirectory);
        string? game = gameDirectory == null || !Directory.Exists(gameDirectory) ? null : PhysicalDirectory(gameDirectory);
        for (var dir = new DirectoryInfo(parent); dir != null; dir = dir.Parent)
            if (dir.FullName.Equals(game, StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(dir.FullName, "SAN14CE_KO.exe")))
                throw new IOException(L.FileExport.InstallFolder);
        using var profileGuard = OpenRead(profilePath);
        if (Convert.ToHexString(SHA256.HashData(profileGuard)) != profileHash) throw new IOException(L.Rules.ProfileMoved);
        using var inputGuard = OpenRead(Input);
        if (Convert.ToHexString(SHA256.HashData(inputGuard)) != InputHash) throw new IOException(L.FileExport.InputChanged);
        inputGuard.Position = 0;
        byte[] original = new byte[checked((int)inputGuard.Length)];
        inputGuard.ReadExactly(original);
        var changes = Edits.Where(e => e.Status == FileEditStatus.Apply).OrderBy(e => e.Cell!.Offset).ToArray();
        byte[] before = Decode(original);
        byte[] body = (byte[])before.Clone();
        foreach (var e in changes)
        {
            var cell = e.Cell!;
            if (cell.Width == 1) body[cell.Offset] = checked((byte)e.Desired);
            else BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(cell.Offset), checked((ushort)e.Desired));
        }
        var output = changes.Length == 0 ? original : RuleFileContainer.Pack(original.AsSpan(0, RuleFileContainer.Block), body);

        // Publish the byte-exact original and the verified output together by renaming a new directory.
        string id = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(parent, ".San14Tenkazu-" + id + ".tmp");
        string final = Path.Combine(parent, $"San14Tenkazu-{DateTime.Now:yyyyMMdd-HHmmss}-{id[..8]}");
        string backupFolder = Path.Combine(staging, "original");
        string backup = Path.Combine(backupFolder, FileName), file = Path.Combine(staging, FileName);
        Directory.CreateDirectory(staging);
        try
        {
            Directory.CreateDirectory(backupFolder);
            WriteNew(backup, original);
            WriteNew(file, output);
            using (var copy = OpenRead(backup))
                if (Convert.ToHexString(SHA256.HashData(copy)) != InputHash) throw new IOException(L.FileExport.Verification);
            var verified = FormationFileData.Read(file);
            byte[] saved = File.ReadAllBytes(file);
            byte[] unpacked = Decode(saved);
            int checksum = RuleFileContainer.ChecksumOffset;
            if (!saved.AsSpan(0, checksum).SequenceEqual(original.AsSpan(0, checksum))
                || !saved.AsSpan(checksum + 4, RuleFileContainer.Block - checksum - 4)
                    .SequenceEqual(original.AsSpan(checksum + 4, RuleFileContainer.Block - checksum - 4)))
                throw new InvalidDataException(L.FileExport.Verification);
            VerifyBody(before, unpacked, changes);
            var reread = RuleFileSnapshot.Read(source.Build, verified, unpacked);
            if (Edits.Where(e => e.Status is FileEditStatus.Already or FileEditStatus.Apply)
                .Any(e => !reread.Cells.TryGetValue(e.Key, out var c) || c.Value != e.Desired))
                throw new InvalidDataException(L.FileExport.Verification);
            Directory.Move(staging, final);
            return new(Path.Combine(final, FileName), Path.Combine(final, "original", FileName), verified.Sha256);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                File.Delete(file); File.Delete(backup);
                if (Directory.Exists(backupFolder)) Directory.Delete(backupFolder);
                Directory.Delete(staging);
            }
        }
    }

    private static string PhysicalDirectory(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) throw new DirectoryNotFoundException(path);
        if (dir.Parent == null) return dir.FullName;
        string resolved = PhysicalDirectory(dir.Parent.FullName);
        var child = new DirectoryInfo(Path.Combine(resolved, dir.Name));
        return child.ResolveLinkTarget(true)?.FullName ?? child.FullName;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static void VerifyBody(byte[] original, byte[] output, FileRuleEdit[] changes)
    {
        if (original.Length != output.Length) throw new InvalidDataException(L.FileExport.Verification);
        int end = 0;
        foreach (var e in changes)
        {
            var c = e.Cell!;
            if (c.Offset < end || !original.AsSpan(end, c.Offset - end).SequenceEqual(output.AsSpan(end, c.Offset - end)))
                throw new InvalidDataException(L.FileExport.Verification);
            int value = c.Width == 1 ? output[c.Offset] : BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(c.Offset));
            if (value != e.Desired) throw new InvalidDataException(L.FileExport.Verification);
            end = c.Offset + c.Width;
        }
        if (!original.AsSpan(end).SequenceEqual(output.AsSpan(end))) throw new InvalidDataException(L.FileExport.Verification);
    }
}

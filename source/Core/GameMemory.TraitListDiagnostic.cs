#if LOCAL_DEVELOPER
using System.Security.Cryptography;

namespace San14Tenkazu;

internal sealed record TraitColumnDifference(int ColumnIndex, string ColumnIdHex, string RecordOffset,
    string Rva, string ExpectedHex, string ActualHex);
internal sealed record TraitListEvidence(string ModuleBase, string TableRva, int RecordSize, int ColumnCount,
    bool Consistent, string? Error, string ExpectedSha256, string ActualSha256,
    string ExpectedHex, string ActualHex, string SecondReadHex, TraitColumnDifference[] Differences);

internal static partial class GameMemory
{
    // Developer-only, explicitly invoked. No write handle, suspension, allocation or native execution.
    internal static TraitListEvidence ReadTraitListDiagnostic(GameIdentity identity)
    {
        if (!identity.Verified || !identity.ExeHash.Equals(VerifiedExeHash, StringComparison.OrdinalIgnoreCase) || !IsSameGame(identity))
            throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        GameLayout layout = ReadCurrentLayout(identity);
        if (ReadPace(identity, layout) != GamePace.Paused)
            throw new InvalidOperationException(L.DeveloperErrors.PauseForTraits);
        IntPtr handle = OpenRead(identity.ProcessId);
        try
        {
            var evidence = ReadTraitListDiagnostic(identity.ModuleBase, (address, length) => ReadBytes(handle, address, length));
            try
            {
                if (!IsSameGame(identity) || ReadPace(identity, layout) != GamePace.Paused)
                    return evidence with { Consistent = false, Error = L.DeveloperErrors.TraitReadChanged };
            }
            catch (Exception error)
            { return evidence with { Consistent = false, Error = L.DeveloperErrors.TraitFinalCheckFailed + error.Message }; }
            return evidence;
        }
        finally { CloseHandle(handle); }
    }

    // The fixture dependency exposes only reads. Compare raw fields without guessing their meaning.
    internal static TraitListEvidence ReadTraitListDiagnostic(ulong module, Func<ulong, int, byte[]> read)
    {
        var block = RuntimeTraitList.Definition.Columns;
        byte[] expected = RuntimeTraitList.Rebase(block, module);
        byte[] first = read(module + block.Rva, expected.Length).ToArray();
        byte[] second = read(module + block.Rva, expected.Length).ToArray();
        if (first.Length != expected.Length || second.Length != expected.Length)
            throw new IOException(L.DeveloperErrors.TraitReadIncomplete);
        var differences = new List<TraitColumnDifference>();
        for (int at = 0; at < expected.Length; at += 8)
        {
            if (expected.AsSpan(at, 8).SequenceEqual(first.AsSpan(at, 8))) continue;
            int column = at / RuntimeTraitList.RecordSize;
            differences.Add(new(column, $"0x{BitConverter.ToUInt64(first, column * RuntimeTraitList.RecordSize):X}",
                $"0x{at % RuntimeTraitList.RecordSize:X2}", $"0x{block.Rva + at:X}",
                Convert.ToHexString(expected.AsSpan(at, 8)), Convert.ToHexString(first.AsSpan(at, 8))));
        }
        bool consistent = first.AsSpan().SequenceEqual(second);
        return new($"0x{module:X}", $"0x{block.Rva:X}", RuntimeTraitList.RecordSize, expected.Length / RuntimeTraitList.RecordSize,
            consistent, consistent ? null : L.DeveloperErrors.TraitReadsDiffer,
            Convert.ToHexString(SHA256.HashData(expected)), Convert.ToHexString(SHA256.HashData(first)),
            Convert.ToHexString(expected), Convert.ToHexString(first), Convert.ToHexString(second), differences.ToArray());
    }
}
#endif

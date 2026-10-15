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
            throw new InvalidOperationException("게임을 일시정지한 뒤 진단하세요. 게임 상태는 변경하지 않았습니다.");
        IntPtr handle = OpenRead(identity.ProcessId);
        try
        {
            var evidence = ReadTraitListDiagnostic(identity.ModuleBase, (address, length) => ReadBytes(handle, address, length));
            try
            {
                if (!IsSameGame(identity) || ReadPace(identity, layout) != GamePace.Paused)
                    return evidence with { Consistent = false, Error = "읽는 동안 게임 연결 또는 일시정지 상태가 바뀌었습니다. 원시 기록이며 확정 결과가 아닙니다." };
            }
            catch (Exception error)
            { return evidence with { Consistent = false, Error = "읽기 후 상태 확인 실패: " + error.Message }; }
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
            throw new IOException("개성 열 정의를 전부 읽지 못했습니다.");
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
            consistent, consistent ? null : "두 번 읽은 열 정의가 다릅니다. 원시 기록이며 확정 결과가 아닙니다.",
            Convert.ToHexString(SHA256.HashData(expected)), Convert.ToHexString(SHA256.HashData(first)),
            Convert.ToHexString(expected), Convert.ToHexString(first), Convert.ToHexString(second), differences.ToArray());
    }
}
#endif

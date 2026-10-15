#if LOCAL_DEVELOPER
using San14Tenkazu;

internal static partial class RenderChecks
{
    private static void CheckTraitListDiagnostic()
    {
        ulong module = 0x7FF600000000;
        var block = RuntimeTraitList.Definition.Columns;
        byte[] bytes = RuntimeTraitList.Rebase(block, module);
        int reads = 0;
        byte[] Read(ulong at, int count)
        { Require(at == module + block.Rva && count == bytes.Length, "Diagnostic read escaped column table"); reads++; return bytes.ToArray(); }
        var original = GameMemory.ReadTraitListDiagnostic(module, Read);
        Require(original.Consistent && original.Differences.Length == 0 && reads == 2 && original.ColumnCount == 213, "Original/rebased table diagnostic failed");
        int changed = 65 * RuntimeTraitList.RecordSize + 0x18;
        bytes[changed] ^= 0x01;
        var difference = GameMemory.ReadTraitListDiagnostic(module, Read);
        Require(difference.Consistent && difference.Differences is [{ ColumnIndex: 65, RecordOffset: "0x18" }]
            && difference.ActualSha256 != difference.ExpectedSha256 && difference.ActualHex == Convert.ToHexString(bytes), "Difference field or raw evidence lost");
        reads = 0;
        var raced = GameMemory.ReadTraitListDiagnostic(module, (at, size) => { var result = Read(at, size); if (reads == 2) result[0] ^= 1; return result; });
        Require(!raced.Consistent && raced.Error is not null && raced.ActualHex != raced.SecondReadHex, "Racing read reported as confirmed");
        bool refused = false;
        try { GameMemory.ReadTraitListDiagnostic(module, (_, count) => new byte[count - 1]); }
        catch (System.IO.IOException) { refused = true; }
        Require(refused, "Partial reads were accepted");
        Console.WriteLine("PASS trait-list diagnostic: rebasing, two bounded reads, exact diff, raw evidence, race and partial read; gameAccess=false");
    }
}
#endif

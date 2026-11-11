namespace San14Tenkazu.Tests;

public sealed class ImageDataPageTests
{
    private const ulong Module = 0x7FF768040000, Address = 0x7FF769A8B3B8;
    private static byte[] Observed()
    {
        // PROCESS_QUERY_INFORMATION / VirtualQueryEx of PID26200, 2026-11-11 23:33:52 KST.
        byte[] info = new byte[48];
        BitConverter.GetBytes(0x7FF769A8B000UL).CopyTo(info, 0);
        BitConverter.GetBytes(Module).CopyTo(info, 8);
        BitConverter.GetBytes(0x80u).CopyTo(info, 16);
        BitConverter.GetBytes(98304UL).CopyTo(info, 24);
        BitConverter.GetBytes(0x1000u).CopyTo(info, 32);
        BitConverter.GetBytes(0x08u).CopyTo(info, 36);
        BitConverter.GetBytes(0x1000000u).CopyTo(info, 40);
        return info;
    }

    [Theory] [InlineData(2u)] [InlineData(4u)] [InlineData(8u)]
    public void ReadOnlyCapsAcceptCommittedNonExecutableDataOfTheSameImage(uint protection)
    {
        var info = Observed(); BitConverter.GetBytes(protection).CopyTo(info, 36);
        Assert.True(GameMemory.IsImageDataPage(info, Address, MilitaryPointCaps.Size, Module));
    }

    [Theory] [InlineData("allocation")] [InlineData("state")] [InlineData("type")]
    [InlineData("execute")] [InlineData("guard")] [InlineData("bounds")] [InlineData("truncated")]
    public void ForeignUncommittedExecutableGuardedAndOutOfBoundsPagesAreRejected(string kind)
    {
        var info = Observed();
        if (kind == "allocation") BitConverter.GetBytes(Module + 0x1000).CopyTo(info, 8);
        if (kind == "state") BitConverter.GetBytes(0x2000u).CopyTo(info, 32);
        if (kind == "type") BitConverter.GetBytes(0x20000u).CopyTo(info, 40);
        if (kind == "execute") BitConverter.GetBytes(0x80u).CopyTo(info, 36);
        if (kind == "guard") BitConverter.GetBytes(0x108u).CopyTo(info, 36);
        if (kind == "bounds") BitConverter.GetBytes(1UL).CopyTo(info, 24);
        if (kind == "truncated") info = info[..40];
        Assert.False(GameMemory.IsImageDataPage(info, Address, MilitaryPointCaps.Size, Module));
    }
}

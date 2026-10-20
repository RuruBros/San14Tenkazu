using System.Runtime.InteropServices;

namespace San14Tenkazu.Tests;

public sealed class FrameAllocationTests
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr VirtualQueryEx(IntPtr process, IntPtr address, out Region region, UIntPtr length);
    [StructLayout(LayoutKind.Sequential)]
    private struct Region { internal IntPtr Base, AllocationBase; internal uint Protect; internal UIntPtr Size; internal uint State, Access, Type; }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnpublishedPagesAreReleasedOnReturnAndException(bool fail)
    {
        IntPtr process = GetCurrentProcess(), page = Allocate(process);
        try
        {
            void Prepare()
            {
                using var allocation = new GameMemory.FrameAllocation(process, page);
                if (fail) throw new InvalidOperationException("Preparation failed before publication");
            }
            if (fail) Assert.Throws<InvalidOperationException>(Prepare); else Prepare();
            Assert.Equal(0x10000u, State(process, page)); // MEM_FREE
        }
        finally { if (State(process, page) != 0x10000) VirtualFreeEx(process, page, UIntPtr.Zero, 0x8000); }
    }

    [Fact]
    public void PublishedPagesRemainAvailableForLateGameEntries()
    {
        IntPtr process = GetCurrentProcess(), page = Allocate(process);
        try
        {
            var allocation = new GameMemory.FrameAllocation(process, page);
            allocation.MarkPublished(); allocation.Dispose(); allocation.Dispose();
            Assert.Equal(0x1000u, State(process, page)); // MEM_COMMIT
            Marshal.WriteInt32(page, 12345);
            Assert.Equal(12345, Marshal.ReadInt32(page));
        }
        finally { Assert.True(VirtualFreeEx(process, page, UIntPtr.Zero, 0x8000)); }
    }

    private static IntPtr Allocate(IntPtr process)
    {
        IntPtr page = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)8192, 0x1000 | 0x2000, 4);
        Assert.NotEqual(IntPtr.Zero, page); return page;
    }

    private static uint State(IntPtr process, IntPtr page)
    {
        Assert.NotEqual(UIntPtr.Zero, VirtualQueryEx(process, page, out var region, (UIntPtr)Marshal.SizeOf<Region>()));
        return region.State;
    }
}

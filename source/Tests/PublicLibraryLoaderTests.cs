using San14Tenkazu.Seasons;

namespace San14Tenkazu.Tests;

public sealed class PublicLibraryLoaderTests
{
    private sealed class Fixture
    {
        internal const ulong Kernel = 0x10000000, Other = 0x20000000, Relay = 0x30000000;
        internal readonly Dictionary<ulong, byte[]> Data = [];
        internal readonly List<PublicLibraryLoader.Module> Modules = [];
        internal uint Protection = 0x20;
        internal Fixture() { Add(Kernel, Path.Combine(Environment.SystemDirectory, "kernel32.dll")); Add(Other, "C:\\fixture\\overlay.dll"); Data[Relay] = new byte[0x4000]; }
        private void Add(ulong address, string path)
        {
            var b = new byte[0x4000]; Data[address] = b; Modules.Add(new(path, address, b.Length));
            void U(int at, uint value) => BitConverter.GetBytes(value).CopyTo(b, at);
            void S(int at, ushort value) => BitConverter.GetBytes(value).CopyTo(b, at);
            S(0, 0x5a4d); U(60, 0x80); U(0x80, 0x4550); S(0x84, 0x8664); S(0x86, 1); S(0x94, 240); S(0x98, 0x20b);
            U(0xd0, 0x4000); U(0x104, 16); U(0x108, 0x1000); U(0x10c, 0x100);
            U(0x190, 0x2000); U(0x194, 0x2000); U(0x1ac, 0x60000020);
            U(0x1010, 1); U(0x1014, 1); U(0x1018, 1); U(0x101c, 0x1100); U(0x1020, 0x1110); U(0x1024, 0x1120);
            U(0x1100, 0x2000); U(0x1110, 0x1130); System.Text.Encoding.ASCII.GetBytes("LoadLibraryW\0").CopyTo(b, 0x1130); b[0x2000] = 0x53;
        }
        internal byte[] Read(ulong at, int size) { var block = Data.Single(p => at >= p.Key && at - p.Key < (ulong)p.Value.Length); return block.Value.AsSpan((int)(at - block.Key), size).ToArray(); }
        internal PublicLibraryLoader.Region Query(ulong at)
        {
            ulong start = at & ~0x3fffUL;
            return new(start, start, 0x4000, 0x1000, Protection, start == Relay ? 0x20000U : 0x1000000U);
        }
        internal PublicLibraryLoader Plan() => new(Read, Query, () => Modules.ToArray());
        internal void Jump(ulong from, ulong to)
        { var b = Data.Single(p => from >= p.Key && from - p.Key < 0x4000); int at = (int)(from - b.Key); b.Value[at] = 0xe9; BitConverter.GetBytes(checked((int)((long)to - (long)from - 5))).CopyTo(b.Value, at + 1); }
        internal void Forward(string target) { BitConverter.GetBytes(0x1040U).CopyTo(Data[Kernel], 0x1100); System.Text.Encoding.ASCII.GetBytes(target + '\0').CopyTo(Data[Kernel], 0x1040); }
    }
    [Fact] public void UnhookedPublicExportWorksWithoutOverlay() { var f = new Fixture(); f.Modules.RemoveAt(1); Assert.Equal(Fixture.Kernel + 0x2000, f.Plan().Entry); }
    [Theory] [InlineData("overlay.LoadLibraryW")] [InlineData("overlay.#1")]
    public void ForwardedPublicExportsResolve(string target) { var f = new Fixture(); f.Forward(target); Assert.Equal(Fixture.Other + 0x2000, f.Plan().Entry); }
    [Fact] public void OverlayRelayPreservesPublicEntryAndTracksIndirectSlot()
    {
        var f = new Fixture(); f.Jump(Fixture.Kernel + 0x2000, Fixture.Relay);
        byte[] b = f.Data[Fixture.Relay]; b[0] = 0xff; b[1] = 0x25; BitConverter.GetBytes(10).CopyTo(b, 2); BitConverter.GetBytes(Fixture.Other + 0x2000).CopyTo(b, 16);
        var plan = f.Plan(); Assert.Equal(Fixture.Kernel + 0x2000, plan.Entry); Assert.Equal(3, plan.Hops);
        b[16] ^= 1; Assert.Throws<InvalidDataException>(plan.Verify);
    }
    [Fact] public void PrivateNonDispatchCodeIsRejected() { var f = new Fixture(); f.Jump(Fixture.Kernel + 0x2000, Fixture.Relay); Assert.Throws<InvalidDataException>(() => f.Plan()); }
    [Fact] public void JumpCyclesAreRejected() { var f = new Fixture(); f.Jump(Fixture.Kernel + 0x2000, Fixture.Relay); f.Jump(Fixture.Relay, Fixture.Kernel + 0x2000); Assert.Throws<InvalidDataException>(() => f.Plan()); }
    [Fact] public void ForwardCyclesAreRejected() { var f = new Fixture(); f.Forward("kernel32.LoadLibraryW"); Assert.Throws<InvalidDataException>(() => f.Plan()); }
    [Theory] [InlineData(4U)] [InlineData(0x120U)] [InlineData(1U)]
    public void InaccessibleOrNonExecutableEntryIsRejected(uint protection) { var f = new Fixture { Protection = protection }; Assert.Throws<InvalidDataException>(() => f.Plan()); }
    [Fact] public void CodeProtectionAndModuleChangesInvalidatePlan()
    {
        var f = new Fixture(); var plan = f.Plan(); f.Data[Fixture.Kernel][0x2000] ^= 1; Assert.Throws<InvalidDataException>(plan.Verify);
        f = new Fixture(); plan = f.Plan(); f.Protection = 0x40; Assert.Throws<InvalidDataException>(plan.Verify);
        f = new Fixture(); plan = f.Plan(); f.Modules.Clear(); Assert.Throws<InvalidDataException>(plan.Verify);
    }
    [Fact] public void WrongArchitectureAndOutOfImageExportAreRejected()
    {
        var f = new Fixture(); f.Data[Fixture.Kernel][0x84] = 0; Assert.Throws<InvalidDataException>(() => f.Plan());
        f = new Fixture(); BitConverter.GetBytes(0x5000U).CopyTo(f.Data[Fixture.Kernel], 0x1100); Assert.Throws<InvalidDataException>(() => f.Plan());
    }
}

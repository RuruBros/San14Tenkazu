using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace San14Tenkazu.Tests;

public class SnapshotReadBufferTests
{
    private sealed class TrackingPool : ArrayPool<byte>
    {
        internal List<byte[]> Rented { get; } = [];
        internal List<byte[]> Returned { get; } = [];

        public override byte[] Rent(int minimumLength)
        {
            var data = new byte[minimumLength + 64];
            Array.Fill(data, (byte)0xCC);
            Rented.Add(data);
            return data;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.DoesNotContain(array, Returned);
            Returned.Add(array);
            Array.Fill(array, (byte)0xEE);
        }
    }

    [Fact]
    public void ReadsOnlyTheRequestedRangeAndReturnsTheBufferOnce()
    {
        var pool = new TrackingPool();
        IntPtr source = Marshal.AllocHGlobal(256);
        using Process process = Process.GetCurrentProcess();
        try
        {
            Marshal.Copy(Enumerable.Repeat((byte)0x23, 256).ToArray(), 0, source, 256);
            using var buffers = new GameMemory.SnapshotReadBuffers(pool);
            byte[] data = buffers.Read(process.Handle, (ulong)source.ToInt64(), 48);
            Assert.All(data.Take(48), value => Assert.Equal((byte)0x23, value));
            Assert.All(data.Skip(48), value => Assert.Equal((byte)0xCC, value));
            buffers.Dispose();
            Assert.Single(pool.Returned);
            Assert.Same(data, pool.Returned[0]);
        }
        finally { Marshal.FreeHGlobal(source); }
    }

    [Fact]
    public void AFailedReadReturnsBothTheCompletedAndFailedReadBuffers()
    {
        var pool = new TrackingPool();
        IntPtr source = Marshal.AllocHGlobal(256);
        using Process process = Process.GetCurrentProcess();
        try
        {
            Assert.Throws<Win32Exception>(() =>
            {
                using var buffers = new GameMemory.SnapshotReadBuffers(pool);
                buffers.Read(process.Handle, (ulong)source.ToInt64(), 48);
                buffers.Read(process.Handle, 0, 48);
            });
            Assert.Equal(2, pool.Rented.Count);
            Assert.Equal(pool.Rented, pool.Returned);
        }
        finally { Marshal.FreeHGlobal(source); }
    }

    [Fact]
    public void TileFieldsOwnTheirDataAfterTheReadBufferIsReturned()
    {
        var pool = new TrackingPool();
        byte[] tiles = pool.Rent(GameMemory.TileCount * GameMemory.TileStride);
        Array.Clear(tiles);
        SetTile(tiles, 0, 4, 300);
        SetTile(tiles, GameMemory.TileCount - 1, 9, 501);
        var map = GameMemory.ReadTileMap(tiles.AsSpan(0, GameMemory.TileCount * GameMemory.TileStride), null, null);
        pool.Return(tiles);
        Assert.Equal((byte)4, map.Owners[0]);
        Assert.Equal((ushort)300, map.Counties[0]);
        Assert.Equal((byte)9, map.Owners[^1]);
        Assert.Equal((ushort)501, map.Counties[^1]);
    }

    [Fact]
    public void UnchangedTileFieldsDoNotAllocateNewSnapshotArrays()
    {
        var tiles = new byte[GameMemory.TileCount * GameMemory.TileStride];
        SetTile(tiles, 0, 4, 300);
        SetTile(tiles, GameMemory.TileCount - 1, 9, 501);
        var before = GameMemory.ReadTileMap(tiles, null, null);
        var after = GameMemory.ReadTileMap(tiles, before.Owners, before.Counties);
        Assert.Same(before.Owners, after.Owners);
        Assert.Same(before.Counties, after.Counties);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ChangedFieldsCopyBeforeWritingAndKeepTheEarlierSnapshot(bool changeOwner, bool changeCounty)
    {
        var tiles = new byte[GameMemory.TileCount * GameMemory.TileStride];
        SetTile(tiles, 0, 4, 300);
        SetTile(tiles, GameMemory.TileCount - 1, 9, 501);
        var before = GameMemory.ReadTileMap(tiles, null, null);
        SetTile(tiles, 0, changeOwner ? (byte)5 : (byte)4, changeCounty ? (ushort)301 : (ushort)300);
        SetTile(tiles, GameMemory.TileCount - 1, changeOwner ? (byte)10 : (byte)9, changeCounty ? (ushort)502 : (ushort)501);
        var after = GameMemory.ReadTileMap(tiles, before.Owners, before.Counties);
        Assert.Equal((byte)4, before.Owners[0]);
        Assert.Equal((ushort)300, before.Counties[0]);
        Assert.Equal((byte)9, before.Owners[^1]);
        Assert.Equal((ushort)501, before.Counties[^1]);
        Assert.Equal(changeOwner ? (byte)5 : (byte)4, after.Owners[0]);
        Assert.Equal(changeCounty ? (ushort)301 : (ushort)300, after.Counties[0]);
        Assert.Equal(changeOwner ? (byte)10 : (byte)9, after.Owners[^1]);
        Assert.Equal(changeCounty ? (ushort)502 : (ushort)501, after.Counties[^1]);
        Assert.Equal(!changeOwner, ReferenceEquals(before.Owners, after.Owners));
        Assert.Equal(!changeCounty, ReferenceEquals(before.Counties, after.Counties));
    }

    private static void SetTile(byte[] tiles, int tile, byte owner, ushort county)
    {
        tiles[tile * GameMemory.TileStride + 0x14] = owner;
        BinaryPrimitives.WriteUInt16LittleEndian(tiles.AsSpan(tile * GameMemory.TileStride + 0x12), county);
    }
}

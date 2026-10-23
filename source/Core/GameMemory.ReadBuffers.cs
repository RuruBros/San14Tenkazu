using System.Buffers;
using System.Buffers.Binary;

namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal static byte[] ReadTerrainMap(ReadOnlySpan<byte> tiles, byte[]? previous)
    {
        if (tiles.Length != TileCount * TileStride) throw new ArgumentException("Incomplete tile map", nameof(tiles));
        byte[] result = previous is { Length: TileCount } ? previous : new byte[TileCount];
        for (int tile = 0; tile < TileCount; tile++)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(tiles.Slice(tile * TileStride + 0x10, 2));
            if (value > 24) throw new InvalidDataException("Loaded terrain type is outside the supported hex layout");
            if (result[tile] == value) continue;
            if (ReferenceEquals(result, previous)) result = (byte[])result.Clone();
            result[tile] = (byte)value;
        }
        return result;
    }

    // Bulk table reads belong to this operation. Only independently owned record copies and decoded tile
    // fields enter the snapshot; returning these buffers cannot rewrite snapshots or pending edits.
    internal sealed class SnapshotReadBuffers(ArrayPool<byte>? pool = null) : IDisposable
    {
        private readonly ArrayPool<byte> pool = pool ?? ArrayPool<byte>.Shared;
        private readonly List<byte[]> rented = new(8);
        private bool disposed;

        internal byte[] Read(IntPtr handle, ulong address, int length)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            byte[] data = pool.Rent(length);
            rented.Add(data);
            if (!TryRead(handle, address, data, length)) throw AccessError(L.Errors.ReadFailed);
            return data;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (byte[] data in rented) pool.Return(data);
            rented.Clear();
        }
    }

    // Unchanged tile fields share the previous snapshot's read-only arrays. At the first change, copy
    // that field's array before writing, so retained snapshots and map workers keep their original data.
    internal static (byte[] Owners, ushort[] Counties) ReadTileMap(ReadOnlySpan<byte> tiles, byte[]? previousOwners, ushort[]? previousCounties)
    {
        if (tiles.Length != TileCount * TileStride) throw new ArgumentException("Incomplete tile map", nameof(tiles));
        byte[] owners = previousOwners is { Length: TileCount } ? previousOwners : new byte[TileCount];
        ushort[] counties = previousCounties is { Length: TileCount } ? previousCounties : new ushort[TileCount];
        for (int i = 0; i < TileCount; i++)
        {
            byte owner = tiles[i * TileStride + 0x14];
            ushort county = BinaryPrimitives.ReadUInt16LittleEndian(tiles.Slice(i * TileStride + TileCountyOffset, 2));
            if (owners[i] != owner)
            {
                if (ReferenceEquals(owners, previousOwners)) owners = (byte[])owners.Clone();
                owners[i] = owner;
            }
            if (counties[i] != county)
            {
                if (ReferenceEquals(counties, previousCounties)) counties = (ushort[])counties.Clone();
                counties[i] = county;
            }
        }
        return (owners, counties);
    }
}

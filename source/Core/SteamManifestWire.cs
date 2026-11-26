using System.Buffers.Binary;

namespace San14Tenkazu;

internal static class SteamManifestWire
{
    internal static InvalidDataException Invalid() => new(L.InstallationAudit.InvalidManifest);
    internal static ReadOnlySpan<byte> Section(ref ReadOnlySpan<byte> rest, uint magic)
    {
        if (rest.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(rest) != magic) throw Invalid();
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(rest[4..]);
        if (size > rest.Length - 8) throw Invalid();
        var section = rest.Slice(8, (int)size);
        rest = rest[(8 + (int)size)..];
        return section;
    }

    internal ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> rest = data;
        private ulong Varint()
        {
            ulong result = 0;
            for (int shift = 0; shift < 64 && !rest.IsEmpty; shift += 7)
            {
                byte b = rest[0];
                rest = rest[1..];
                if (shift == 63 && b > 1) throw Invalid();
                result |= (ulong)(b & 127) << shift;
                if (b < 128) return result;
            }
            throw Invalid();
        }

        internal bool Next(out int tag, out ulong number, out ReadOnlySpan<byte> bytes)
        {
            tag = 0; number = 0; bytes = default;
            if (rest.IsEmpty) return false;
            ulong key = Varint();
            if (key < 8 || key > int.MaxValue) throw Invalid();
            tag = (int)key;
            if ((tag & 7) == 0) number = Varint();
            else
            {
                ulong length = (tag & 7) switch { 1 => 8, 2 => Varint(), 5 => 4, _ => throw Invalid() };
                if (length > (ulong)rest.Length) throw Invalid();
                bytes = rest[..(int)length];
                rest = rest[(int)length..];
            }
            return true;
        }
    }
}

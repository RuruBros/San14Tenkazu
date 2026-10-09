using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace San14Tenkazu;

// Read-only access to the game's KTGL resource archives (0002\NAME.rdb + NAME.rdb.bin*).
// NAME.rdb lists entries ("IDRK0000") with a type hash and a location "offset@size#bin&sub";
// the payload in the .bin file is another IDRK entry whose data is stored in zlib chunks.
internal static class KtglArchive
{
    internal const uint TextureType = 0xAFBEC60C;     // G1T texture
    internal const uint SpriteSheetType = 0xF20DE437; // UI sprite names + UV rectangles
    internal const uint HeightMapType = 0xD7F47FB1;   // "_MHK0100" terrain height map
    private const uint FlagZlib = 0x00100000;

    internal readonly record struct Entry(uint Type, uint Ktid, string Bin, long Offset, int Size);

    // What reading the game's files may throw when one is missing, locked or not laid out as expected (another
    // version of the game): the readers follow offsets and sizes from the files, and a wrong one ends in any of
    // these. Every caller that can do without the game's data (a plain map, no portrait) catches them by this.
    internal static bool IsUnreadable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException
            or IndexOutOfRangeException or OverflowException or OutOfMemoryException;

    internal static List<Entry> ReadIndex(string dataDirectory, string archive)
    {
        byte[] rdb = File.ReadAllBytes(Path.Combine(dataDirectory, archive + ".rdb"));
        if (Encoding.ASCII.GetString(rdb, 0, 8) != "_DRK0000") throw new InvalidDataException(L.Errors.RdbFormat(archive));
        var entries = new List<Entry>();
        long pos = BinaryPrimitives.ReadUInt32LittleEndian(rdb.AsSpan(8));
        while (pos + 0x38 <= rdb.Length && Encoding.ASCII.GetString(rdb, (int)pos, 8) == "IDRK0000")
        {
            var span = rdb.AsSpan((int)pos);
            long entrySize = BinaryPrimitives.ReadInt64LittleEndian(span[8..]);
            int dataSize = (int)BinaryPrimitives.ReadInt64LittleEndian(span[0x10..]);
            int extraSize = BinaryPrimitives.ReadInt32LittleEndian(span[0x20..]);
            uint ktid = BinaryPrimitives.ReadUInt32LittleEndian(span[0x24..]);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(span[0x28..]);
            int links = BinaryPrimitives.ReadInt32LittleEndian(span[0x34..]);
            int at = 0x38 + links * 12 + extraSize;
            string location = Encoding.ASCII.GetString(span.Slice(at, dataSize)).TrimEnd('\0');
            if (TryParseLocation(archive, location, out string bin, out long offset, out int size))
                entries.Add(new Entry(type, ktid, bin, offset, size));
            pos = (pos + entrySize + 3) & ~3L;
        }
        return entries;
    }

    // "offset@size#binIndex&sub" (hex) -> NAME.rdb.bin{index}{_sub}
    private static bool TryParseLocation(string archive, string location, out string bin, out long offset, out int size)
    {
        bin = ""; offset = 0; size = 0;
        int at = location.IndexOf('@');
        if (at <= 0) return false;
        string rest = location[(at + 1)..];
        string suffix = "";
        int hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            string[] parts = rest[(hash + 1)..].Split('&');
            if (!int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int index)) return false;
            suffix = index.ToString(CultureInfo.InvariantCulture) + (parts.Length > 1 ? "_" + parts[1] : "");
            rest = rest[..hash];
        }
        if (!long.TryParse(location[..at], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset) ||
            !int.TryParse(rest, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size)) return false;
        bin = archive + ".rdb.bin" + suffix;
        return true;
    }

    // The IDRK entry that fills the space between the end of one indexed entry (`start`, maybe
    // followed by alignment padding) and the next indexed entry, `next`: usually an old copy that an
    // update replaced with a new one elsewhere. Returns its type and KTID, or null.
    internal static (uint Type, uint Ktid)? UnindexedBefore(FileStream stream, long start, long next)
    {
        var header = new byte[0x38];
        foreach (long at in new[] { start, (start + 0xF) & ~0xFL, (start + 0xFFF) & ~0xFFFL }.Distinct())
        {
            if (at + header.Length > next) continue;
            stream.Seek(at, SeekOrigin.Begin);
            stream.ReadExactly(header);
            if (Encoding.ASCII.GetString(header, 0, 8) != "IDRK0000") continue;
            long end = at + BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
            if (next - end is >= 0 and < 16)
                return (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x28)), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x24)));
        }
        return null;
    }

    internal static byte[] Read(string dataDirectory, Entry entry)
    {
        using FileStream stream = File.OpenRead(Path.Combine(dataDirectory, entry.Bin));
        return Read(stream, entry.Offset, entry.Size);
    }

    internal static byte[] Read(FileStream stream, long offset, int size)
    {
        byte[]? output = null;
        ReadTo(stream, offset, size, length =>
        {
            output = new byte[length];
            return new MemoryStream(output, writable: true);
        });
        return output!;
    }

    internal static void ReadTo(string dataDirectory, Entry entry, Func<int, Stream> createOutput)
    {
        using FileStream stream = File.OpenRead(Path.Combine(dataDirectory, entry.Bin));
        ReadTo(stream, entry.Offset, entry.Size, createOutput);
    }

    // The consumer can own native upload storage; decompression never needs a whole managed G1T.
    internal static void ReadTo(FileStream stream, long offset, int size, Func<int, Stream> createOutput)
    {
        if (offset < 0 || size < 0x38) throw new InvalidDataException(L.Errors.ResourceFormat);
        long entryEnd = checked(offset + size);
        if (entryEnd > stream.Length) throw new InvalidDataException(L.Errors.ResourceFormat);
        stream.Seek(offset, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[0x38];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual("IDRK0000"u8)) throw new InvalidDataException(L.Errors.ResourceFormat);
        int dataSize = checked((int)BinaryPrimitives.ReadInt64LittleEndian(header[0x10..]));
        int fileSize = checked((int)BinaryPrimitives.ReadInt64LittleEndian(header[0x18..]));
        int extraSize = BinaryPrimitives.ReadInt32LittleEndian(header[0x20..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header[0x2C..]);
        int links = BinaryPrimitives.ReadInt32LittleEndian(header[0x34..]);
        int start = checked(0x38 + links * 12 + extraSize);
        if (dataSize < 0 || fileSize < 0 || links < 0 || extraSize < 0 || start > size)
            throw new InvalidDataException(L.Errors.ResourceFormat);
        long end = Math.Min(checked(offset + start + dataSize), entryEnd);
        stream.Seek(offset + start, SeekOrigin.Begin);
        bool compressed = (flags & FlagZlib) != 0;
        int outputLength = compressed ? fileSize : (int)(end - stream.Position);
        using Stream output = createOutput(outputLength);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int written = 0;
            if (!compressed)
            {
                while (written < outputLength)
                {
                    int count = Math.Min(buffer.Length, outputLength - written);
                    stream.ReadExactly(buffer.AsSpan(0, count));
                    output.Write(buffer.AsSpan(0, count));
                    written += count;
                }
            }
            else
            {
                Span<byte> chunkHeader = stackalloc byte[4];
                while (written < outputLength && stream.Position + 4 <= end)
                {
                    stream.ReadExactly(chunkHeader);
                    int chunk = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader);
                    if (chunk <= 0 || chunk > end - stream.Position) throw new InvalidDataException(L.Errors.ResourceFormat);
                    long chunkEnd = stream.Position + chunk;
                    using (var zlib = new ZLibStream(new ChunkStream(stream, chunk), CompressionMode.Decompress))
                    {
                        int read;
                        while (written < outputLength && (read = zlib.Read(buffer.AsSpan(0, Math.Min(buffer.Length, outputLength - written)))) > 0)
                        {
                            output.Write(buffer.AsSpan(0, read));
                            written += read;
                        }
                        if (written == outputLength && zlib.ReadByte() != -1) throw new InvalidDataException(L.Errors.ResourceFormat);
                    }
                    stream.Seek(chunkEnd, SeekOrigin.Begin);
                }
            }
            if (written != outputLength) throw new InvalidDataException(L.Errors.ResourceFormat);
            stream.Seek(entryEnd, SeekOrigin.Begin);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    // Zlib may read ahead; keep it within this chunk and leave the archive open for the next one.
    private sealed class ChunkStream(Stream source, int length) : Stream
    {
        private int remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => length - remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int read = source.Read(buffer[..Math.Min(buffer.Length, remaining)]);
            remaining -= read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // DataEnd is where the next texture's header starts (or the file ends). An array texture holds
    // (DataEnd - DataOffset) / (one layer's mip chain) layers, stored mip by mip: every layer's mip 0,
    // then every layer's mip 1, and so on.
    internal readonly record struct G1tTexture(int Format, int Width, int Height, int DataOffset, int Mips = 1, int DataEnd = 0);

    // G1T container: header, offset table, then per texture an 8-byte header, an optional
    // extended header and the pixel data (mip 0 first).
    internal static G1tTexture[] G1tTextures(ReadOnlySpan<byte> g1t)
    {
        if (g1t.Length < 0x20 || !g1t[..4].SequenceEqual("GT1G"u8)) return [];
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(g1t[0x0C..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(g1t[0x10..]);
        var headers = new int[count];
        for (int i = 0; i < count; i++) headers[i] = headerSize + BinaryPrimitives.ReadInt32LittleEndian(g1t[(headerSize + i * 4)..]);
        var textures = new G1tTexture[count];
        for (int i = 0; i < count; i++)
        {
            int header = headers[i];
            int extended = (g1t[header + 7] & 0xF0) != 0 ? BinaryPrimitives.ReadInt32LittleEndian(g1t[(header + 8)..]) : 0;
            if (extended is < 0 or > 0x100) extended = 0;
            int width = 1 << (g1t[header + 2] & 0xF), height = 1 << (g1t[header + 2] >> 4);
            // Sizes that are not powers of two (the 2049x2049 field maps) sit in the extended header.
            if (extended >= 0x14 && g1t[header + 2] == 0)
            {
                width = BinaryPrimitives.ReadInt32LittleEndian(g1t[(header + 8 + 0x0C)..]);
                height = BinaryPrimitives.ReadInt32LittleEndian(g1t[(header + 8 + 0x10)..]);
            }
            textures[i] = new G1tTexture(g1t[header + 1], width, height, header + 8 + extended,
                Math.Max(1, g1t[header] >> 4), i + 1 < count ? headers[i + 1] : g1t.Length);
        }
        return textures;
    }
}

// Block compression as the game's G1T textures store their pixels (BC1, BC3, BC7): one 4x4 block to ARGB.
internal static class Bc
{
    private static (int, int, int) C565(ushort c) => ((c >> 11 & 31) * 255 / 31, (c >> 5 & 63) * 255 / 63, (c & 31) * 255 / 31);
    private static uint Pack(int a, int r, int g, int b) => (uint)(a << 24 | r << 16 | g << 8 | b);

    internal static void Bc1(ReadOnlySpan<byte> b, Span<uint> o, bool allowAlpha)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(b), c1 = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);
        var (r0, g0, b0) = C565(c0);
        var (r1, g1, b1) = C565(c1);
        Span<uint> colours = stackalloc uint[4];
        colours[0] = Pack(255, r0, g0, b0);
        colours[1] = Pack(255, r1, g1, b1);
        if (c0 > c1 || !allowAlpha)
        {
            colours[2] = Pack(255, (2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3);
            colours[3] = Pack(255, (r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3);
        }
        else
        {
            colours[2] = Pack(255, (r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2);
            colours[3] = 0;
        }
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        for (int i = 0; i < 16; i++) o[i] = colours[(int)(bits >> (2 * i) & 3)];
    }

    internal static void Bc3(ReadOnlySpan<byte> b, Span<uint> o)
    {
        Bc1(b[8..], o, false);
        int a0 = b[0], a1 = b[1];
        Span<int> a = stackalloc int[8];
        a[0] = a0;
        a[1] = a1;
        if (a0 > a1) for (int i = 1; i < 7; i++) a[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else
        {
            for (int i = 1; i < 5; i++) a[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            a[6] = 0;
            a[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) o[i] = o[i] & 0x00FFFFFF | (uint)a[(int)(bits >> (3 * i) & 7)] << 24;
    }

    // A BC3 block's colours alone, opaque (the alpha block is skipped): the field's maps and the ground's layers.
    internal static void Bc3Colour(ReadOnlySpan<byte> b, Span<uint> o) => Bc1(b[8..], o, false);

    private static readonly byte[] P2 = Partitions(
        "0011001100110011 0001000100010001 0111011101110111 0001001100110111 0000000100010011 0011011101111111 0001001101111111 0000000100110111 " +
        "0000000000010011 0011011111111111 0000000101111111 0000000000010111 0001011111111111 0000000011111111 0000111111111111 0000000000001111 " +
        "0000100011101111 0111000100000000 0000000010001110 0111001100010000 0011000100000000 0000100011001110 0000000010001100 0111001100110001 " +
        "0011000100010000 0000100010001100 0110011001100110 0011011001101100 0001011111101000 0000111111110000 0111000110001110 0011100110011100 " +
        "0101010101010101 0000111100001111 0101101001011010 0011001111001100 0011110000111100 0101010110101010 0110100101101001 0101101010100101 " +
        "0111001111001110 0001001111001000 0011001001001100 0011101111011100 0110100110010110 0011110011000011 0110011010011001 0000011001100000 " +
        "0100111001000000 0010011100100000 0000001001110010 0000010011100100 0110110010010011 0011011011001001 0110001110011100 0011100111000110 " +
        "0110110011001001 0110001100111001 0111111010000001 0001100011100111 0000111100110011 0011001111110000 0010001011101110 0100010001110111");
    private static readonly byte[] P3 = Partitions(
        "0011001102212222 0001001122112221 0000200122112211 0222002200110111 0000000011221122 0011001100220022 0022002211111111 0011001122112211 " +
        "0000000011112222 0000111111112222 0000111122222222 0012001200120012 0112011201120112 0122012201220122 0011011211221222 0011200122002220 " +
        "0001001101121122 0111001120012200 0000112211221122 0022002200221111 0111011102220222 0001000122212221 0000001101220122 0000110022102210 " +
        "0122012200110000 0012001211222222 0110122112210110 0000011012211221 0022110211020022 0110011020022222 0011012201220011 0000200022112221 " +
        "0000000211221222 0222002200120011 0011001200220222 0120012001200120 0000111122220000 0120120120120120 0120201212010120 0011220011220011 " +
        "0011112222000011 0101010122222222 0000000021212121 0022112200221122 0022001100220011 0220122102201221 0101222222220101 0000212121212121 " +
        "0101010101012222 0222011102220111 0002111200021112 0000211221122112 0222011101110222 0002111211120002 0110011001102222 0000000021122112 " +
        "0110011022222222 0022001100110022 0022112211220022 0000000000002112 0002000100020001 0222122202221222 0101222222222222 0111201122012220");
    private static readonly byte[] A2 = [15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15];
    private static readonly byte[] A3a = [3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3];
    private static readonly byte[] A3b = [15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8];
    private static readonly int[] W2 = [0, 21, 43, 64], W3 = [0, 9, 18, 27, 37, 46, 55, 64], W4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    private static byte[] Partitions(string s) => s.Where(char.IsDigit).Select(c => (byte)(c - '0')).ToArray();

    private ref struct Bits(ReadOnlySpan<byte> b)
    {
        private readonly ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(b), hi = BinaryPrimitives.ReadUInt64LittleEndian(b[8..]);
        private int position;

        internal int Get(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++, position++)
                v |= (int)((position < 64 ? lo >> position : hi >> (position - 64)) & 1) << i;
            return v;
        }
    }

    private static int Unquantize(int v, int bits)
    {
        v <<= 8 - bits;
        return v | v >> bits;
    }

    private static int Interpolate(int e0, int e1, int w) => ((64 - w) * e0 + w * e1 + 32) >> 6;
    private static int[] Weights(int bits) => bits == 2 ? W2 : bits == 3 ? W3 : W4;

    internal static void Bc7(ReadOnlySpan<byte> b, Span<uint> o)
    {
        int mode = 0;
        while (mode < 8 && (b[0] >> mode & 1) == 0) mode++;
        if (mode == 8)
        {
            o.Fill(0);
            return;
        }
        var bits = new Bits(b);
        bits.Get(mode + 1);
        int subsets = mode switch { 0 or 2 => 3, 1 or 3 or 7 => 2, _ => 1 };
        int partition = bits.Get(mode switch { 0 => 4, 1 or 2 or 3 or 7 => 6, _ => 0 });
        int rotation = mode is 4 or 5 ? bits.Get(2) : 0;
        int indexSelection = mode == 4 ? bits.Get(1) : 0;
        int colourBits = mode switch { 0 => 4, 1 => 6, 2 => 5, 3 => 7, 4 => 5, 5 => 7, 6 => 7, 7 => 5, _ => 0 };
        int alphaBits = mode switch { 4 => 6, 5 => 8, 6 => 7, 7 => 5, _ => 0 };
        int endpoints = subsets * 2;
        Span<int> ep = stackalloc int[6 * 4];
        for (int c = 0; c < 3; c++)
            for (int e = 0; e < endpoints; e++) ep[e * 4 + c] = bits.Get(colourBits);
        if (alphaBits > 0)
            for (int e = 0; e < endpoints; e++) ep[e * 4 + 3] = bits.Get(alphaBits);
        bool perEndpoint = mode is 0 or 3 or 6 or 7, shared = mode == 1;
        Span<int> pbits = stackalloc int[6];
        if (perEndpoint)
            for (int e = 0; e < endpoints; e++) pbits[e] = bits.Get(1);
        if (shared)
            for (int s = 0; s < subsets; s++) pbits[s * 2] = pbits[s * 2 + 1] = bits.Get(1);
        bool hasP = perEndpoint || shared;
        for (int e = 0; e < endpoints; e++)
        {
            for (int c = 0; c < 3; c++)
                ep[e * 4 + c] = hasP ? Unquantize(ep[e * 4 + c] << 1 | pbits[e], colourBits + 1) : Unquantize(ep[e * 4 + c], colourBits);
            ep[e * 4 + 3] = alphaBits == 0 ? 255 : hasP ? Unquantize(ep[e * 4 + 3] << 1 | pbits[e], alphaBits + 1) : Unquantize(ep[e * 4 + 3], alphaBits);
        }
        Span<int> subset = stackalloc int[16];
        for (int i = 0; i < 16; i++) subset[i] = subsets == 2 ? P2[partition * 16 + i] : subsets == 3 ? P3[partition * 16 + i] : 0;
        bool IsAnchor(int i) => i == 0 || subsets == 2 && i == A2[partition] || subsets == 3 && (i == A3a[partition] || i == A3b[partition]);
        int indexBits = mode switch { 0 or 1 => 3, 6 => 4, _ => 2 };
        int indexBits2 = mode switch { 4 => 3, 5 => 2, _ => 0 };
        Span<int> index = stackalloc int[16], index2 = stackalloc int[16];
        for (int i = 0; i < 16; i++) index[i] = bits.Get(IsAnchor(i) ? indexBits - 1 : indexBits);
        if (indexBits2 > 0)
            for (int i = 0; i < 16; i++) index2[i] = bits.Get(i == 0 ? indexBits2 - 1 : indexBits2);
        for (int i = 0; i < 16; i++)
        {
            int e0 = subset[i] * 2 * 4, e1 = (subset[i] * 2 + 1) * 4;
            int colourWeight, alphaWeight;
            if (indexBits2 == 0) colourWeight = alphaWeight = Weights(indexBits)[index[i]];
            else if (indexSelection == 0)
            {
                colourWeight = Weights(indexBits)[index[i]];
                alphaWeight = Weights(indexBits2)[index2[i]];
            }
            else
            {
                colourWeight = Weights(indexBits2)[index2[i]];
                alphaWeight = Weights(indexBits)[index[i]];
            }
            int r = Interpolate(ep[e0], ep[e1], colourWeight), g = Interpolate(ep[e0 + 1], ep[e1 + 1], colourWeight);
            int bl = Interpolate(ep[e0 + 2], ep[e1 + 2], colourWeight), a = Interpolate(ep[e0 + 3], ep[e1 + 3], alphaWeight);
            switch (rotation)
            {
                case 1: (a, r) = (r, a); break;
                case 2: (a, g) = (g, a); break;
                case 3: (a, bl) = (bl, a); break;
            }
            o[i] = Pack(a, r, g, bl);
        }
    }
}

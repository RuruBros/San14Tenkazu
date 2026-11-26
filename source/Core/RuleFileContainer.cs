using System.Buffers.Binary;
using System.Numerics;

namespace San14Tenkazu;

// The first 754 bytes of a CE fixed-data container belong to the input file.
// Only its body checksum changes when the LWC block is replaced.
internal static class RuleFileContainer
{
    internal const int Block = 754, ChecksumOffset = 463;
    // Terminated UTF-16 title at +0x1a6, checked on both Korean originals and against JP/SC/TC headers.
    internal static ReadOnlySpan<byte> KoreanTitle => [0x69, 0xd6, 0x74, 0xac, 0x58, 0xc7, 0x20, 0, 0x9c, 0xb0, 0, 0];
    private const int Window = 8189, MaxMatch = 512;

    internal static byte[] Pack(ReadOnlySpan<byte> header, byte[] body)
    {
        if (header.Length != Block) throw new InvalidDataException(L.FileComparison.FormatError);
        var frequencies = new int[256];
        uint sum = 0;
        foreach (byte b in body) { frequencies[b]++; sum += b; }
        byte[] symbols = Enumerable.Range(0, 256).OrderByDescending(i => frequencies[i]).Select(i => (byte)i).ToArray();
        var ranks = new byte[256];
        for (int i = 0; i < symbols.Length; i++) ranks[symbols[i]] = (byte)i;

        using var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(LwcCodec.Magic);
        stream.Write(new byte[8]); // lengths are filled after encoding
        stream.Write(symbols);
        var writer = new CodeWriter(stream);
        // Hash collisions only discard a candidate. Always compare the actual bytes before emitting a match.
        var previous = new int[65536];
        Array.Fill(previous, -1);
        int Hash(int i) => ((body[i] * 251 + body[i + 1]) * 251 + body[i + 2]) & 0xffff;
        for (int at = 0; at < body.Length;)
        {
            int length = 0, distance = 0;
            if (body.Length - at >= 3)
            {
                int p = previous[Hash(at)];
                distance = at - p;
                if (p >= 0 && distance <= Window)
                {
                    int limit = Math.Min(MaxMatch, body.Length - at);
                    while (length < limit && body[p + length] == body[at + length]) length++;
                }
            }
            if (length >= 3) { writer.Put(256 + distance); writer.Put(length - 3); }
            else { writer.Put(ranks[body[at]]); length = 1; }
            int end = at + length;
            while (at < end)
            {
                if (at <= body.Length - 3) previous[Hash(at)] = at;
                at++;
            }
        }
        writer.Finish();
        byte[] file = stream.ToArray();
        int payload = file.Length - Block - LwcCodec.HeaderSize;
        // In this format equal sizes designate an uncompressed payload. Do not emit ambiguous output.
        if (payload == body.Length) throw new InvalidDataException(L.FileComparison.FormatError);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(Block + 4), body.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(Block + 8), payload);
        uint checksum = (uint)BigInteger.ModPow(sum & 0xffff, 5, 0x3fde03a3);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(ChecksumOffset), checksum);
        return file;
    }

    private sealed class CodeWriter(Stream stream)
    {
        private int buffer, used;
        // n prefix bits (n-1 ones and a zero) and n payload bits represent value + 2 - 2^n.
        internal void Put(int value)
        {
            int n = BitOperations.Log2((uint)value + 2);
            int code = (((1 << n) - 2) << n) | (value + 2 - (1 << n));
            for (int i = 2 * n - 1; i >= 0; i--)
            {
                buffer = (buffer << 1) | ((code >> i) & 1);
                if (++used == 8) { stream.WriteByte((byte)buffer); buffer = used = 0; }
            }
        }
        internal void Finish() { if (used > 0) stream.WriteByte((byte)(buffer << (8 - used))); }
    }
}

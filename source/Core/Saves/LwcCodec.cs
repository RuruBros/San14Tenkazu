namespace San14Tenkazu;

// The game's save body is one "LWC" block: magic, decoded size, payload size, a 256-byte literal table,
// then a bit stream (most significant bit first) of Elias-gamma style codes. A code below 256 is an index
// into the table; 256 + d copies from d bytes back, for a second code + 3 bytes. Transcribed from the
// verified build's decoder (0x9330A0 and 0x933AF0) and checked against saved games (notes/saves.md).
internal static class LwcCodec
{
    internal const int HeaderSize = 0x10C;
    internal static ReadOnlySpan<byte> Magic => "LWC\x1a"u8;

    internal static bool IsBlock(ReadOnlySpan<byte> data, int offset)
        => offset >= 0 && data.Length - offset >= HeaderSize && data.Slice(offset, 4).SequenceEqual(Magic);

    internal static byte[] Decode(ReadOnlySpan<byte> file, int offset)
    {
        if (!IsBlock(file, offset)) throw new InvalidDataException(L.RuntimeErrors.SaveFormat("magic"));
        int decoded = BitConverter.ToInt32(file.Slice(offset + 4, 4)), payload = BitConverter.ToInt32(file.Slice(offset + 8, 4));
        if (decoded < 0 || payload < 0 || (long)payload + HeaderSize != file.Length - offset)
            throw new InvalidDataException(L.RuntimeErrors.SaveFormat("size"));
        ReadOnlySpan<byte> table = file.Slice(offset + 12, 256);
        var bits = new BitReader(file[(offset + HeaderSize)..]);
        byte[] output = new byte[decoded];
        int written = 0;
        while (written < decoded)
        {
            int code = bits.Gamma();
            if (code < 256) { output[written++] = table[code]; continue; }
            int distance = code - 256, length = bits.Gamma() + 3;
            if (distance <= 0 || distance > written || length > decoded - written)
                throw new InvalidDataException(L.RuntimeErrors.SaveFormat($"copy {written}"));
            for (int i = 0; i < length; i++) output[written + i] = output[written - distance + i];
            written += length;
        }
        return output;
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int position, current, left;
        private int Bit()
        {
            if (left == 0)
            {
                if (position >= data.Length) throw new InvalidDataException(L.RuntimeErrors.SaveFormat("end"));
                current = data[position++]; left = 8;
            }
            left--;
            int bit = current >> 7;
            current = (current << 1) & 0xFF;
            return bit;
        }
        // Ones up to the first zero form the prefix value (binary); as many bits as that took follow and are added.
        internal int Gamma()
        {
            long prefix = 0; int count = 0;
            while (true)
            {
                int bit = Bit(); count++; prefix = prefix * 2 + bit;
                if (bit == 0) break;
                if (count > 30) throw new InvalidDataException(L.RuntimeErrors.SaveFormat("code"));
            }
            long value = 0;
            for (int i = 0; i < count; i++) value = value * 2 + Bit();
            return checked((int)(value + prefix));
        }
    }
}

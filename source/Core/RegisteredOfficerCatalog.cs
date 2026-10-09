namespace San14Tenkazu;

// Eligibility in CEditPerson -> CPerson (0x20A800). The encoded vector uses the
// original CData byte/word decoders (0xDB00/0xDB30), rotate-left one then XOR ID.
internal static class RegisteredOfficerCatalog
{
    internal static bool Personality(int id, byte[] record, Func<ulong, int, byte[]?> read)
    {
        // 0x32F500 -> 0x234270: entry zero is invalid; custom entries need effect 1.
        if (id <= 0 || id > 355 || id >= 256 && record[0xB6] == 0 || record[0xBE] == 0) return false;
        int flags = record[0xBF];
        if (id < 256 && VectorLength(record, 0xC8) == 4)
        {
            byte[]? encoded = read(BitConverter.ToUInt64(record, 0xC8), 2);
            if (encoded is null) return false;
            int word = BitConverter.ToUInt16(encoded);
            flags = ((word << 1 | word >> 15) ^ id) & 0xFF;
        }
        return (flags & 16) != 0; // 0x16F7F0(record, 4)
    }

    internal static bool Tactic(int id, byte[] record, Func<ulong, int, byte[]?> read)
    {
        if (id <= 0 || id > 200 || record[0x40] == 0) return false;
        int flags = record[0x44];
        if (VectorLength(record, 0x70) == 40)
        {
            byte[]? encoded = read(BitConverter.ToUInt64(record, 0x70), 1);
            if (encoded is null) return false;
            int value = encoded[0];
            flags = ((value << 1 | value >> 7) ^ id) & 0xFF;
        }
        return (flags & 2) != 0;
    }

    private static ulong VectorLength(byte[] record, int offset) =>
        BitConverter.ToUInt64(record, offset + 8) - BitConverter.ToUInt64(record, offset);
}

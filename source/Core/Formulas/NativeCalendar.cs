using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// Five-byte game dates use 360-day years, 30-day months and signed byte arithmetic.
internal static class NativeCalendar
{
    // 0x31ABE0: ordered field copies preserve bytes after the five date fields.
    internal static void CopyFive(ReadOnlySpan<byte> source, Span<byte> target)
    {
        BinaryPrimitives.WriteInt16LittleEndian(target, BinaryPrimitives.ReadInt16LittleEndian(source));
        target[2] = source[2];
        target[3] = source[3];
        target[4] = source[4];
    }

    // 0x31CBA0: truncating divisions, narrow intermediate stores and 32-bit wrap follow the instructions.
    internal static void AddHours(Span<byte> date, int hours)
    {
        int total = unchecked(hours + (sbyte)date[4] +
            ((sbyte)date[3] - 31 + ((sbyte)date[2] + BinaryPrimitives.ReadInt16LittleEndian(date) * 12) * 30) * 24);
        short year = unchecked((short)(total / 8640));
        int remaining = Math.Max(0, unchecked(total - year * 8640));
        sbyte month = unchecked((sbyte)(remaining / 720));
        remaining = Math.Max(0, unchecked(remaining - month * 720));
        sbyte day = unchecked((sbyte)(remaining / 24));
        remaining = Math.Max(0, unchecked(remaining - day * 24));
        BinaryPrimitives.WriteInt16LittleEndian(date, year);
        date[2] = unchecked((byte)(month + 1));
        date[3] = unchecked((byte)(day + 1));
        date[4] = unchecked((byte)remaining);
    }
}

using System.Buffers.Binary;

namespace San14Tenkazu;

// Verified storage rules of unnamed scalar fields. No guessed game labels.
internal static class AiRawInfo
{
    internal readonly record struct Field(int Offset, int Bytes, bool Signed = false, int Mask = 0);
    internal static Field GetField(string family, int kind) => (family, kind) switch
    {
        ("Person", 31) => new(0x17A, 2, true), ("Person", 37) => new(0x1E8, 4, true),
        ("Foothold", 13) => new(0xD0, 4, true), ("Foothold", 14) => new(0xD4, 4, true),
        ("Foothold", 15) => new(0xD8, 4, true), ("Foothold", 16) => new(0x50, 4, true),
        ("Foothold", 17) => new(0x54, 4, true), ("Foothold", 18) => new(0xC8, 1),
        ("Foothold", 19) => new(0xC9, 1, Mask: 1), ("Foothold", 25) => new(0x67, 1, Mask: 1),
        ("District", 1) => new(0x16, 1, true), ("District", 5) => new(0x14, 1),
        ("Area", 6) => new(0x44, 2), ("Area", 7) => new(0x46, 2), ("Area", 8) => new(0x48, 2),
        ("Hex", 3) => new(0x16, 2), ("Scenery", 2) => new(0x64, 1), ("Scenery", 7) => new(0x66, 2),
        ("CautionTroops", 0) => new(0x18, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static int Read(string family, int kind, ReadOnlySpan<byte> record)
    {
        Field field = GetField(family, kind);
        ReadOnlySpan<byte> value = record.Slice(field.Offset, field.Bytes);
        int result = field.Bytes switch
        {
            1 => field.Signed ? unchecked((sbyte)value[0]) : value[0],
            2 => field.Signed ? BinaryPrimitives.ReadInt16LittleEndian(value) : BinaryPrimitives.ReadUInt16LittleEndian(value),
            4 => BinaryPrimitives.ReadInt32LittleEndian(value),
            _ => throw new InvalidOperationException(),
        };
        return field.Mask == 0 ? result : result & field.Mask;
    }
}

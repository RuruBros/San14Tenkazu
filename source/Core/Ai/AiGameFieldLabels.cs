using System.Buffers.Binary;

namespace San14Tenkazu;

// Game column records have a 0x60-byte stride. The formatter and message ID belong to the same record.
internal readonly record struct AiGameColumn(ulong Formatter, uint MessageId)
{
    internal static AiGameColumn Read(ReadOnlySpan<byte> record) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(record[0x18..]),
        BinaryPrimitives.ReadUInt32LittleEndian(record[0x48..]));
}

internal static class AiGameFieldLabels
{
    // 35B400 formats the signed Han inclination at Person+17A as the game's five display levels.
    internal static int HanDisplayLevel(short value) => value switch
    {
        -20 => 1, -10 => 2, 0 => 3, 10 => 4, 20 => 5, _ => 0,
    };

    // The option dialog copies Options+8 to rbp-10. Its stack displacements are not Options offsets.
    internal static int OptionCopyOffset(int rbpDisplacement) => checked(rbpDisplacement + 0x10);
}

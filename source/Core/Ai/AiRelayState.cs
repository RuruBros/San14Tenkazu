using System.Buffers.Binary;

namespace San14Tenkazu;

// Common prefix of the factory states for 65E30 and 66C20. Building pointers and area pointers
// have different types despite sharing these offsets; no simulated game-turn advancement is implied.
internal readonly record struct AiRelayState(ulong Current, ulong Next, int Initialized)
{
    internal static AiRelayState Read(ReadOnlySpan<byte> state) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(state[8..]),
        BinaryPrimitives.ReadUInt64LittleEndian(state[0x10..]),
        BinaryPrimitives.ReadInt32LittleEndian(state[0x18..]));
}

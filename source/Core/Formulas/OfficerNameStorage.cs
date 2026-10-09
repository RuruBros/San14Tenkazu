using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// Logical ownership of copied UTF-16 name buffers. Released native allocation addresses are observable
// outputs; this store never frees or writes a running game's memory.
internal sealed class OfficerNameStorage(IDictionary<ulong, byte[]> buffers, IReadOnlyDictionary<ulong, ulong> alignedBases)
{
    internal readonly record struct Name(ulong Pointer, ulong Allocation);
    internal List<ulong> Released { get; } = [];

    // 0x21E190: small names free their pointer directly; allocations of at least 4096 bytes use
    // the preceding base pointer and require a 32-byte aligned payload with an 8..39-byte offset.
    internal Name? Prepare(Rec person)
    {
        ulong capacity = BitConverter.ToUInt64(person.Bytes, 0x1B0);
        if (capacity <= 7) return null;
        ulong pointer = BitConverter.ToUInt64(person.Bytes, 0x198);
        if (!buffers.TryGetValue(pointer, out byte[]? bytes) || bytes.Length < 2)
            throw new InvalidDataException("Missing copied heap name buffer.");
        ulong count = unchecked(capacity + 1), allocation = pointer;
        if (count > long.MaxValue) throw new InvalidDataException("Native string release size exceeds its signed bound.");
        if (count * 2 > 4095)
        {
            if ((pointer & 31) != 0 || !alignedBases.TryGetValue(pointer, out allocation) ||
                pointer <= allocation || pointer - allocation is < 8 or > 39)
                throw new InvalidDataException("Invalid native aligned string allocation header.");
        }
        return new Name(pointer, allocation);
    }

    // 0x226300 -> 0x242690 -> 0x2421B0: write only the empty terminator into the old buffer and
    // inline union, retain the unused union tail, set capacity 7, then release the old allocation.
    internal void ClearAndShrink(Rec person, Name name)
    {
        buffers[name.Pointer].AsSpan(0, 2).Clear();
        person.Bytes.AsSpan(0x198, 2).Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(person.Bytes.AsSpan(0x1B0), 7);
        buffers.Remove(name.Pointer);
        Released.Add(name.Allocation);
    }
}

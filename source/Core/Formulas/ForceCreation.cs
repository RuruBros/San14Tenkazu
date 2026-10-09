using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

internal static class ForceCreation
{
    // 0x226EC0: local record initialization, without bilateral agreement changes or storage release.
    // The five pool lists are cleared in native order; their wrapper pointers and padding survive.
    internal static void InitializeRecord(Rec force, bool resetColor, bool nodePoolEnabled, Action<int> clearList)
    {
        byte[] b = force.Bytes;
        void Word(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), value);
        foreach (var (at, length) in new (int, int)[] {
            (0x10,3), (0x13,52), (0x47,2), (0x4A,2), (0x52,2), (0x54,0x96), (0xEA,52),
            (0x11E,2), (0x120,0x3E), (0x160,9), (0x16A,4), (0x170,8), (0x178,2),
            (0x17A,10), (0x184,10), (0x18E,5), (0x193,2), (0x196,6), (0x19C,3),
            (0x1A0,16), (0x1B8,4), (0x1BC,8), (0x1C4,1), (0x1C5,6), (0x1CB,30) })
            b.AsSpan(at, length).Clear();
        if (resetColor) b[0x49] = 60;
        Word(0x15E, 11);
        foreach (int at in new[] { 0x4C, 0x1B0 })
        {
            Word(at, 0); b[at + 2] = b[at + 3] = 1; b[at + 4] = 0;
        }
        void Clear(int at)
        {
            if (nodePoolEnabled && BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at)) != 0) clearList(at);
        }
        Clear(0x250); Clear(0x260);
        b[0x20D] = 0;
        Clear(0x218); Clear(0x228); Clear(0x238);
    }

    private static bool Available(Rec force) => !Objects.ForceValid(force) && force.Index != 51 &&
        (uint)(force.Index - 46) > 4;

    // 0x2A9520: slot selection only; the subsequent 0x2D7C80 creation body is separate.
    internal static Rec? SelectSlot(World w, Rec person, int requested)
    {
        if (requested > 0)
        {
            Rec explicitSlot = w.Entry(Tables.Forces, requested, 51);
            if (Available(explicitSlot)) return explicitSlot;
        }
        foreach (Rec force in w.Table(Tables.Forces))
            if (force.Index != 0 && Available(force) && force.I16(0x11E) == person.I16(0x10)) return force;

        Rec? fallback = null;
        foreach (Rec force in w.Table(Tables.Forces))
        {
            if (force.Index == 0 || !Available(force)) continue;
            if (force.I16(0x11E) == person.I16(0xB2)) return force;
            if (force.Index is >= 38 and <= 45 && (fallback is null || force.Index > fallback.Index)) fallback = force;
        }
        return fallback;
    }
}

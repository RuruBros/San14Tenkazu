using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

internal static class ForceReset
{
    // 0x226030: reset all bilateral fields and the record, then clear both external lists in order.
    // This compatibility entry excludes a configured player and owned +0x1A0 storage.
    internal static void ApplyWithoutPlayerOrVector(World w, Rec force, bool nodePoolEnabled, Action<int> clearList)
    {
        if (w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 51).Index != 0)
            throw new NotSupportedException("A configured player requires the agreement-refresh body.");
        ApplyWithoutVector(w, force, nodePoolEnabled, clearList);
    }

    // 0x226030 with empty +0x1A0 storage, including player flag policy and ordered city refresh requests.
    internal static void ApplyWithoutVector(World w, Rec force, bool nodePoolEnabled, Action<int> clearList,
        Action<Rec>? refreshCity = null)
    {
        if (BinaryPrimitives.ReadUInt64LittleEndian(force.Bytes.AsSpan(0x1A0)) != 0)
            throw new NotSupportedException("Owned force storage requires its release host.");
        Apply(w, force, nodePoolEnabled, clearList, refreshCity);
    }

    // +0x1A0 is one owned allocation, released after record fields and before dates and external lists.
    internal static void Apply(World w, Rec force, bool nodePoolEnabled, Action<int> clearList,
        Action<Rec>? refreshCity = null, Action<ulong>? releaseStorage = null)
    {
        byte[] b = force.Bytes;
        ulong Pointer(int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at));
        ulong storage = Pointer(0x1A0);
        if (storage != 0 && releaseStorage is null)
            throw new NotSupportedException("Owned force storage requires its release host.");
        void Zero(int at, int length) => b.AsSpan(at, length).Clear();
        void Word(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), value);
        void EmptyDate(int at)
        {
            Word(at, 0);
            b[at + 2] = b[at + 3] = 1;
            b[at + 4] = 0;
        }
        foreach (Rec other in w.Table(Tables.Forces))
        {
            ForceRelations.SetAgreement(w, force, other, 0, refreshCity);
            if ((uint)other.Index < 52) b[0x13 + other.Index] = 0;
            if ((uint)force.Index < 52) other.Bytes[0x13 + force.Index] = 0;
            EmptyDate(0x4C);
        }
        Zero(0x10, 3);
        Zero(0x47, 2);
        Zero(0x4A, 2);
        Zero(0x52, 2);
        Zero(0x54, 0x96);
        Zero(0x120, 0x3E);
        Word(0x15E, 11);
        Zero(0x160, 9);
        Zero(0x16A, 4);
        Zero(0x170, 8);
        Zero(0x178, 2);
        Zero(0x17A, 10);
        Zero(0x184, 10);
        Zero(0x18E, 5);
        Zero(0x193, 2);
        Zero(0x196, 6);
        Zero(0x19C, 3);
        if (storage != 0)
        {
            releaseStorage!(storage);
            Zero(0x1A0, 8);
        }
        Zero(0x1A8, 8);
        EmptyDate(0x1B0);
        Zero(0x1B8, 4);
        Zero(0x1BC, 8);
        Zero(0x1C5, 6);
        Zero(0x1CB, 30);
        foreach (int at in new[] { 0x250, 0x260 })
            if (Pointer(at) != 0 && nodePoolEnabled) clearList(at);
        b[0x20D] = 0;
    }
}

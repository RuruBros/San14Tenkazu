using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// 0x934400: history records contain fixed UTF-16 fields. Localization and the game-state refresh are host requests.
internal sealed class HistoryLog(World w, IReadOnlyList<Rec?> records, bool suppressed, bool gameStateAvailable,
    Func<Rec, string> forceInitial, Action<Rec> refresh)
{
    internal bool Suppressed => suppressed;

    internal readonly record struct Entry(int Kind, int Corps, int Hex, int Player, int Tick, string Primary,
        string Secondary, int Officer, int Immediate = 0, int Flag4 = 0, int Flag5 = 0, int Flag6 = 0);

    // 0x30DE00 copies at most the field limit, including a terminator only if it fits; unused tail bytes survive.
    private static void CopyField(Rec record, int offset, string text, int limit)
    {
        int end = text.IndexOf('\0');
        if (end < 0) end = text.Length;
        int count = Math.Min(end + 1, limit);
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(record.Bytes.AsSpan(offset + i * 2), i == end ? (ushort)0 : text[i]);
    }

    internal Rec? Add(Entry entry)
    {
        if (suppressed) return null;
        int index = (w.WorldState.U16(0x165A) + 1) % 2001;
        Rec? record = records[index];
        if (record is null) return null;
        bool primary = entry.Primary.Length != 0 && entry.Primary[0] != '\0';
        if (primary && entry.Corps < 0)
            throw new InvalidDataException("A negative native corps lookup requires memory outside the copied table.");
        record.Bytes[0x86] = (byte)Math.Clamp(entry.Kind, 0, 6);
        record.Bytes[0x12] = (byte)Math.Clamp(entry.Corps, 0, 51);
        BinaryPrimitives.WriteUInt16LittleEndian(record.Bytes.AsSpan(0x84), (ushort)Math.Clamp(entry.Hex, 0, 48399));
        byte flags = (byte)((record.U8(0x82) & 0xFE) | (entry.Player != 0 ? 3 : 2));
        flags = (byte)((flags & 0xBB) | (entry.Flag6 != 0 ? 0x40 : 0));
        record.Bytes[0x82] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(record.Bytes.AsSpan(0x10), (ushort)Math.Clamp(entry.Tick, 0, 65535));
        CopyField(record, 0x14, entry.Primary, 55);
        CopyField(record, 0x8A, entry.Secondary, 85);
        BinaryPrimitives.WriteUInt16LittleEndian(record.Bytes.AsSpan(0x88), (ushort)Math.Clamp(entry.Officer, 1, 6000));
        if (primary)
        {
            Rec corps = w.Entry(Tables.Corps, entry.Corps, 51);
            Rec force = w.Entry(Tables.Forces, Objects.CorpsValid(corps) ? corps.U8(0x10) : 0, 51);
            record.Bytes[0x134] = Objects.CorpsValid(corps) ? force.U8(0x49) : (byte)60;
            string initial = forceInitial(force);
            CopyField(record, 0x136, initial.Length == 0 ? "" : initial[..1], 2);
        }
        record.Bytes[0x82] = (byte)((record.U8(0x82) & 0xC7) |
            (entry.Immediate != 0 ? 8 : 0) | (entry.Flag4 != 0 ? 16 : 0) | (entry.Flag5 != 0 ? 32 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(w.WorldState.Bytes.AsSpan(0x165A), (ushort)index);
        if (!primary) return record;
        if (!gameStateAvailable) return null;
        refresh(record);
        return record;
    }
}

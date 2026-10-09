using System.Buffers.Binary;

namespace San14Tenkazu;

// CDistrictData: the military intelligence balance, not the income formula.
// Native 6273E0 writes signed int32 +24 and clamps to the rank-dependent cap from 2311B0.
internal static class CorpsFields
{
    internal static readonly FieldDef MilitaryPoints = new(L.Force.MilitaryPoints, RecordKind.Corps, 0x24, FieldKind.Int32, 0, int.MaxValue);
    internal const int Size = 0x30;
}

internal sealed record CorpsRecord(int Id, ulong Address, byte[] Data)
{
    internal int Force => Data[0x10];
    internal int Number => Data[0x11];
    internal int Leader => BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(0x12));
    internal int MilitaryPoints => (int)CorpsFields.MilitaryPoints.Read(Data);
}

// Read the four Native parameters too, rather than assuming that their original values remain current.
internal sealed record MilitaryPointCaps(ulong Address, byte[] Data)
{
    internal const ulong Rva = 0x1A4836C;
    internal const int Size = 0x24;
    internal bool Valid => Data.Length == Size && new[] { 0, 8, 20, 32 }.All(at => BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(at)) >= 0);
    internal int ForRank(int rank) => BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(rank switch
    {
        1 or 2 or 3 or 10 or 11 or 12 => 32,
        4 or 5 => 20,
        6 or 7 => 8,
        _ => 0
    }));
}

internal sealed record MilitaryPointRequest(int Corps, int Number, int Leader, int Value);
internal sealed record MilitaryPointGuard(ulong ForceAddress, int Force, int Ruler, MilitaryPointCaps Caps);

internal static class MilitaryIntelligence
{
    internal static void Check(IRecordMemory memory, GameClasses classes, RecordEdit edit, byte[] corps)
    {
        if (edit.MilitaryPoints is not { } guard || guard.Force != corps[0x10]
            || edit.Changes.Any(c => c.Field != CorpsFields.MilitaryPoints)) throw new InvalidOperationException(L.Force.MilitaryUnavailable);
        byte[] force = memory.Read(guard.ForceAddress, 0x48);
        if (BinaryPrimitives.ReadUInt64LittleEndian(force) != classes.Force
            || BinaryPrimitives.ReadUInt16LittleEndian(force.AsSpan(ForceFields.RulerOffset)) != guard.Ruler)
            throw new InvalidOperationException(L.Force.MilitaryUnavailable);
        var caps = new MilitaryPointCaps(guard.Caps.Address, memory.Read(guard.Caps.Address, MilitaryPointCaps.Size));
        if (!caps.Valid) throw new InvalidOperationException(L.Force.MilitaryUnavailable);
        int maximum = caps.ForRank(force[ForceFields.Rank.Offset]);
        if (edit.Changes.Any(c => c.Value < 0 || c.Value > maximum)) throw new InvalidOperationException(L.Force.MilitaryRange(maximum));
    }
}

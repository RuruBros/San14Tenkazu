using System.Buffers.Binary;

namespace San14Tenkazu;

internal sealed record SaveOfficer(int Index, ushort Id, byte[] Abilities, int[] Experience, ushort[] Traits, byte[] Stream)
{
    internal int TraitCount => Traits.Count(t => t != 0);
}

internal sealed record GameSaveOfficers(string Path, DateTime WrittenUtc, long Length, int Year, int Month, int Day, SaveOfficer[] Officers)
{
    internal string Name => System.IO.Path.GetFileName(Path);
}

// A Korean CE save (svdexceKO##.s14, autosdexceKO##.s14): a plain header, then the LWC block with the
// world stream. The stream lists each table as its record count, the records written by their own
// serializer, then the table's index. Officer records (table 11) of file version 0x66 are 402 bytes;
// where each byte lands in the live 0x1F0 record was read off the game's own deserializer run over a
// real save in Unicorn (source/Reverse/save_officers_emulate.py), not inferred.
internal static class GameSaveFile
{
    internal const int FileVersion = 0x66;
    internal const int RecordSize = 402;
    internal const int OfficerTableIndex = 11;
    private const int HeaderScanLimit = 0x200;
    private static ReadOnlySpan<byte> Signature => "SN14SVEXVER"u8;

    // Stream offset, live record offset, byte count. Only runs confirmed on every record of the sample.
    internal static readonly (int Stream, int Record, int Length)[] Segments =
    [
        (0, 0x12, 10), (14, 0x20, 8), (28, 0x2E, 4), (34, 0x34, 14), (148, 0xA6, 2), (150, 0x10, 2), (152, 0xBA, 4),
        (156, 0xE0, 1), (157, 0xE2, 2), (159, 0xE4, 5), (166, 0xEC, 5), (171, 0xF2, 5), (176, 0x118, 18), (194, 0x12C, 4),
        (198, 0x130, 10), (208, 0x14D, 4), (213, 0x166, 1), (216, 0xA9, 4), (221, 0xAE, 3), (224, 0xB2, 7), (231, 0xBE, 1),
        (232, 0xC0, 32), (264, 0xF8, 10), (274, 0x14C, 1), (276, 0x16A, 7), (284, 0x172, 1), (286, 0x174, 1), (288, 0x176, 13),
        (305, 0x15E, 2), (307, 0x1BC, 1), (308, 0x1BE, 4), (315, 0x1C5, 1), (317, 0x104, 20), (371, 0x165, 1), (373, 0x184, 2),
    ];
    internal const int IdStream = 150, AbilityStream = 166, TraitStream = 176, ExperienceStream = 317;

    internal static bool LooksLikeSave(ReadOnlySpan<byte> file)
        => file.Length > HeaderScanLimit && BinaryPrimitives.ReadInt32LittleEndian(file) == FileVersion && file.Slice(4, Signature.Length).SequenceEqual(Signature);

    internal static GameSaveOfficers? Read(string path, int officerCount)
    {
        var info = new FileInfo(path);
        return Read(File.ReadAllBytes(path), path, info.LastWriteTimeUtc, officerCount);
    }

    // Null when the file holds no officer table of this size; throws when it is not a save of this version.
    internal static GameSaveOfficers? Read(byte[] file, string path, DateTime writtenUtc, int officerCount)
    {
        if (!LooksLikeSave(file)) throw new InvalidDataException(L.RuntimeErrors.SaveFormat("header"));
        int block = file.AsSpan(0, HeaderScanLimit).IndexOf(LwcCodec.Magic);
        if (block < 0) throw new InvalidDataException(L.RuntimeErrors.SaveFormat("block"));
        int year = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0xD7)), month = file[0xD9], day = file[0xDA];
        byte[] stream = LwcCodec.Decode(file, block);
        int table = FindOfficerTable(stream, officerCount);
        if (table < 0) return null;
        var officers = new SaveOfficer[officerCount];
        for (int i = 0; i < officerCount; i++)
        {
            byte[] record = stream.AsSpan(table + 8 + i * RecordSize, RecordSize).ToArray();
            officers[i] = new(i, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(IdStream)), record.AsSpan(AbilityStream, 5).ToArray(),
                Enumerable.Range(0, 5).Select(k => BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(ExperienceStream + 4 * k))).ToArray(),
                Enumerable.Range(0, 9).Select(k => BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(TraitStream + 2 * k))).ToArray(), record);
        }
        return new(path, writtenUtc, file.LongLength, year, month, day, officers);
    }

    // The previous table's index, this table's count, the records, then this table's index.
    internal static int FindOfficerTable(ReadOnlySpan<byte> stream, int officerCount)
    {
        long body = (long)officerCount * RecordSize;
        for (int at = 0; at + 12 + body <= stream.Length; at++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(stream[at..]) != OfficerTableIndex - 1
                || BinaryPrimitives.ReadInt32LittleEndian(stream[(at + 4)..]) != officerCount) continue;
            if (BinaryPrimitives.ReadInt32LittleEndian(stream[(int)(at + 8 + body)..]) == OfficerTableIndex) return at;
        }
        return -1;
    }

    // The same officer in the same state, apart from what the original loader strips (abilities, traits).
    internal static bool SameOfficer(ReadOnlySpan<byte> record, SaveOfficer saved)
    {
        foreach (var (stream, offset, length) in Segments)
        {
            if (stream == AbilityStream || stream == TraitStream) continue;
            if (!record.Slice(offset, length).SequenceEqual(saved.Stream.AsSpan(stream, length))) return false;
        }
        return true;
    }

    // Writes stream bytes into a live-layout record: for tests and diagnostics only.
    internal static void ToRecord(ReadOnlySpan<byte> stream, Span<byte> record)
    {
        foreach (var (at, offset, length) in Segments) stream.Slice(at, length).CopyTo(record.Slice(offset, length));
    }
}

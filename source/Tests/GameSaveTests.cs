using System.Buffers.Binary;
using System.Text.Json;
using San14Tenkazu;
using Xunit;

namespace San14Tenkazu.Tests;

public sealed class GameSaveTests
{
    private const int Count = 7;
    private static readonly RuntimeRuleSettings Full = new(true, AbilityRuleMode.Growth200, true);

    // A greedy LWC encoder, the inverse of the game's decoder: literals through the table, matches as 256 + distance then length - 3.
    internal static byte[] Encode(byte[] data, byte[]? table = null)
    {
        table ??= Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        int[] index = new int[256]; for (int i = 0; i < 256; i++) index[table[i]] = i;
        var bits = new BitWriter();
        for (int at = 0; at < data.Length;)
        {
            int bestLength = 0, bestDistance = 0;
            for (int distance = 1; distance <= Math.Min(at, 4096); distance++)
            {
                int length = 0;
                while (length < 40 && at + length < data.Length && data[at + length] == data[at + length - distance]) length++;
                if (length > bestLength) { bestLength = length; bestDistance = distance; }
            }
            if (bestLength >= 3) { bits.Gamma(256 + bestDistance); bits.Gamma(bestLength - 3); at += bestLength; }
            else { bits.Gamma(index[data[at]]); at++; }
        }
        byte[] payload = bits.ToArray();
        byte[] block = new byte[LwcCodec.HeaderSize + payload.Length];
        LwcCodec.Magic.CopyTo(block);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(4), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(8), payload.Length);
        table.CopyTo(block, 12); payload.CopyTo(block, LwcCodec.HeaderSize);
        return block;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> bytes = []; private int current, used;
        private void Bit(int bit) { current = (current << 1) | bit; if (++used == 8) { bytes.Add((byte)current); current = 0; used = 0; } }
        public void Gamma(int value)
        {
            int n = 0; while (value >= (2L << (n + 1)) - 2) n++;
            long prefix = (2L << n) - 2;
            for (int i = 0; i < n; i++) Bit(1);
            Bit(0);
            long rest = value - prefix;
            for (int i = n; i >= 0; i--) Bit((int)((rest >> i) & 1));
        }
        public byte[] ToArray() { var copy = bytes.ToList(); if (used > 0) copy.Add((byte)(current << (8 - used))); return copy.ToArray(); }
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(7)] [InlineData(4096)]
    public void LwcRoundTripsLiteralsMatchesAndEveryTableSymbol(int seed)
    {
        var random = new Random(seed);
        byte[] data = new byte[seed == 0 ? 1 : 20000];
        for (int i = 0; i < data.Length; i++) data[i] = i % 3 == 0 ? (byte)random.Next(256) : data[Math.Max(0, i - 1 - random.Next(5))];
        byte[] table = Enumerable.Range(0, 256).Select(i => (byte)i).OrderBy(_ => random.Next()).ToArray();
        byte[] block = Encode(data, table);
        Assert.Equal(data, LwcCodec.Decode(block, 0));
        byte[] prefixed = new byte[10 + block.Length]; block.CopyTo(prefixed, 10);
        Assert.Equal(data, LwcCodec.Decode(prefixed, 10));
    }

    [Fact]
    public void LwcRejectsBadMagicSizesAndReferencesBeforeTheStart()
    {
        byte[] block = Encode([1, 2, 3, 4, 5, 6]);
        byte[] magic = (byte[])block.Clone(); magic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => LwcCodec.Decode(magic, 0));
        Assert.Throws<InvalidDataException>(() => LwcCodec.Decode(block.AsSpan(0, block.Length - 1).ToArray(), 0));
        var bits = new BitWriter(); bits.Gamma(256 + 1); bits.Gamma(0);
        byte[] bad = Encode([]); byte[] payload = bits.ToArray();
        byte[] broken = new byte[LwcCodec.HeaderSize + payload.Length]; bad.AsSpan(0, LwcCodec.HeaderSize).CopyTo(broken);
        BinaryPrimitives.WriteInt32LittleEndian(broken.AsSpan(4), 3); BinaryPrimitives.WriteInt32LittleEndian(broken.AsSpan(8), payload.Length); payload.CopyTo(broken, LwcCodec.HeaderSize);
        Assert.Throws<InvalidDataException>(() => LwcCodec.Decode(broken, 0));
    }

    internal static byte[] StreamRecord(int id, byte[] abilities, int[] experience, ushort[] traits, int salt)
    {
        byte[] record = new byte[GameSaveFile.RecordSize];
        var random = new Random(salt);
        foreach (var (at, _, length) in GameSaveFile.Segments) for (int i = 0; i < length; i++) record[at + i] = (byte)random.Next(1, 200);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(GameSaveFile.IdStream), (ushort)id);
        abilities.CopyTo(record, GameSaveFile.AbilityStream);
        for (int k = 0; k < 5; k++) BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(GameSaveFile.ExperienceStream + 4 * k), experience[k]);
        for (int k = 0; k < 9; k++) BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(GameSaveFile.TraitStream + 2 * k), traits[k]);
        return record;
    }

    internal static (byte[] File, byte[][] Streams) SyntheticSave(int count, Func<int, byte[]> stream, int year = 184, int month = 2, int day = 21)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(0x66));
        body.AddRange(Enumerable.Repeat((byte)0, 40));
        body.AddRange(BitConverter.GetBytes(10)); body.AddRange(BitConverter.GetBytes(count + 1)); // decoy: a table of another size
        body.AddRange(Enumerable.Repeat((byte)7, 33));
        body.AddRange(BitConverter.GetBytes(10)); body.AddRange(BitConverter.GetBytes(count));
        var streams = Enumerable.Range(0, count).Select(stream).ToArray();
        foreach (var s in streams) body.AddRange(s);
        body.AddRange(BitConverter.GetBytes(11));
        body.AddRange(Enumerable.Repeat((byte)3, 50));
        byte[] block = Encode(body.ToArray());
        byte[] file = new byte[0x11F + block.Length];
        BinaryPrimitives.WriteInt32LittleEndian(file, GameSaveFile.FileVersion);
        "SN14SVEXVER0000\0"u8.CopyTo(file.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0xD7), (ushort)year); file[0xD9] = (byte)month; file[0xDA] = (byte)day;
        block.CopyTo(file, 0x11F);
        return (file, streams);
    }

    private static byte[] Memory(byte[] stream)
    {
        byte[] record = new byte[GameMemory.OfficerStride];
        GameSaveFile.ToRecord(stream, record);
        return record;
    }

    [Fact]
    public void ReadsTheOfficerTableOfTheRequestedSizeBehindADecoy()
    {
        var (file, streams) = SyntheticSave(Count, i => StreamRecord(1000 + i, [100, 150, 100, 100, 200], [1, 2, 3, 4, 5], [1, 2, 3, 4, 5, 6, 7, 8, 9], i));
        var save = GameSaveFile.Read(file, @"X:\remote\svdexceKO04.s14", new DateTime(2026, 10, 16, 10, 9, 11, DateTimeKind.Utc), Count)!;
        Assert.Equal((184, 2, 21, "svdexceKO04.s14", Count), (save.Year, save.Month, save.Day, save.Name, save.Officers.Length));
        for (int i = 0; i < Count; i++)
        {
            var officer = save.Officers[i];
            Assert.Equal((i, 1000 + i, 9), (officer.Index, (int)officer.Id, officer.TraitCount));
            Assert.Equal(new byte[] { 100, 150, 100, 100, 200 }, officer.Abilities);
            Assert.Equal([1, 2, 3, 4, 5], officer.Experience);
            Assert.Equal(streams[i], officer.Stream);
            Assert.True(GameSaveFile.SameOfficer(Memory(streams[i]), officer));
            Assert.False(GameSaveFile.SameOfficer(Memory(streams[(i + 1) % Count]), officer));
        }
        Assert.Null(GameSaveFile.Read(file, "x", DateTime.UtcNow, Count + 2));
        byte[] notASave = (byte[])file.Clone(); notASave[5] = (byte)'x';
        Assert.False(GameSaveFile.LooksLikeSave(notASave));
        Assert.Throws<InvalidDataException>(() => GameSaveFile.Read(notASave, "x", DateTime.UtcNow, Count));
    }

    [Fact]
    public void RestoresOnlyWhatTheLoaderStrippedFromAMatchingRecord()
    {
        var saved = GameSaveFile.Read(SyntheticSave(1, _ => StreamRecord(77, [150, 100, 99, 151, 100], [10, 20, 30, 40, 50], [1, 2, 3, 4, 5, 6, 0, 0, 0], 3)).File, "s", DateTime.UtcNow, 1)!.Officers[0];
        byte[] stripped = Memory(saved.Stream);
        stripped[0xEC] = 100; stripped[0xEF] = 100; Array.Clear(stripped, 0x118, 18);
        var writes = SaveRestore.Plan(stripped, 0x1000, saved, Full);
        Assert.Equal(3, writes.Length);
        var traits = Assert.Single(writes, SaveRestore.IsTraitRestore);
        Assert.Equal((0x1000UL + 0x118, 18), (traits.Address, traits.After.Length));
        Assert.Equal(new byte[] { 1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6, 0, 0, 0, 0, 0, 0, 0 }, traits.After);
        Assert.Equal([(0x1000UL + 0xEC, (byte)150), (0x1000UL + 0xEF, (byte)151)], writes.Where(w => !SaveRestore.IsTraitRestore(w)).Select(w => (w.Address, w.After[0])));
        Assert.All(writes, w => Assert.False(w.Executable));
        // Nothing without the rules, nothing for the untouched record, nothing when any other field differs.
        Assert.Empty(SaveRestore.Plan(stripped, 0x1000, saved, RuntimeRuleSettings.Original));
        Assert.Equal(2, SaveRestore.Plan(stripped, 0x1000, saved, new(false, AbilityRuleMode.Cap200)).Length);
        Assert.Empty(SaveRestore.Plan(Memory(saved.Stream), 0x1000, saved, Full));
        byte[] changed = (byte[])stripped.Clone(); changed[0x104]++;
        Assert.Empty(SaveRestore.Plan(changed, 0x1000, saved, Full));
        // Five or fewer traits are never a deletion signature; a lone zeroed slot set is left alone.
        var few = GameSaveFile.Read(SyntheticSave(1, _ => StreamRecord(77, [100, 100, 100, 100, 100], [1, 1, 1, 1, 1], [1, 2, 3, 4, 5, 0, 0, 0, 0], 3)).File, "s", DateTime.UtcNow, 1)!.Officers[0];
        byte[] fewMemory = Memory(few.Stream); Array.Clear(fewMemory, 0x118, 18);
        Assert.Empty(SaveRestore.Plan(fewMemory, 0x1000, few, Full));
    }

    [Fact]
    public void LocatorPicksTheSaveWhoseRecordsMatchTheLiveTable()
    {
        string directory = Path.Combine(Path.GetTempPath(), "san14-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] Stream(int i, int salt) => StreamRecord(500 + i, [100, 100, 100, 100, 100], [i, i, i, i, i], [1, 0, 0, 0, 0, 0, 0, 0, 0], salt * 100 + i);
            var loaded = SyntheticSave(Count, i => Stream(i, 1));
            var other = SyntheticSave(Count, i => Stream(i, 2));
            File.WriteAllBytes(Path.Combine(directory, "svdexceKO01.s14"), other.File);
            File.WriteAllBytes(Path.Combine(directory, "svdexceKO02.s14"), loaded.File);
            File.WriteAllBytes(Path.Combine(directory, "junk.s14"), [1, 2, 3]);
            var records = new byte[]?[Count]; for (int i = 1; i < Count; i++) records[i] = Memory(loaded.Streams[i]);
            var candidates = new DirectoryInfo(directory).EnumerateFiles("*.s14").ToArray();
            var match = GameSaveLocator.FindLoaded(records, candidates)!;
            Assert.Equal(("svdexceKO02.s14", Count - 1, Count - 1), (match.Save.Name, match.Matched, match.Compared));
            records[1] = Memory(other.Streams[1]);
            Assert.Null(GameSaveLocator.FindLoaded(records, candidates)); // 5 of 6: below the threshold
            Assert.Null(GameSaveLocator.FindLoaded(new byte[]?[Count + 1], candidates));
        }
        finally { Directory.Delete(directory, true); }
    }

    // Opt-in: SAN14_SAVE_VECTOR names a JSON with {"save": path, "officers": N, "records": [{"index","id","abilities","experience","traits"}]}
    // produced by running the game's own deserializer in Unicorn (source/Reverse/save_officers_emulate.py) over a real save.
    [Fact]
    public void RealSaveMatchesTheGamesOwnDeserializerWhenAVectorIsProvided()
    {
        string? vector = Environment.GetEnvironmentVariable("SAN14_SAVE_VECTOR");
        if (string.IsNullOrEmpty(vector) || !File.Exists(vector)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(vector));
        var root = document.RootElement;
        var save = GameSaveFile.Read(root.GetProperty("save").GetString()!, root.GetProperty("officers").GetInt32());
        Assert.NotNull(save);
        int checkedRecords = 0;
        foreach (var expected in root.GetProperty("records").EnumerateArray())
        {
            var officer = save!.Officers[expected.GetProperty("index").GetInt32()];
            Assert.Equal(expected.GetProperty("id").GetInt32(), officer.Id);
            Assert.Equal(expected.GetProperty("abilities").EnumerateArray().Select(e => (byte)e.GetInt32()), officer.Abilities);
            Assert.Equal(expected.GetProperty("experience").EnumerateArray().Select(e => e.GetInt32()), officer.Experience);
            Assert.Equal(expected.GetProperty("traits").EnumerateArray().Select(e => (ushort)e.GetInt32()), officer.Traits);
            if (expected.TryGetProperty("record", out var record))
            {
                byte[] memory = Convert.FromHexString(record.GetString()!);
                Assert.True(GameSaveFile.SameOfficer(memory, officer));
            }
            checkedRecords++;
        }
        Assert.Equal(root.GetProperty("officers").GetInt32(), checkedRecords);
    }
}

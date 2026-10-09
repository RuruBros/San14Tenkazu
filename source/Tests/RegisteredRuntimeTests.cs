using System.Buffers.Binary;
using System.Text.Json;
using San14Tenkazu.Formulas;
using San14Tenkazu.Tests.Formulas;

namespace San14Tenkazu.Tests;

public sealed class RegisteredRuntimeTests
{
    private static JsonDocument Evidence() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RegisteredRuntimeVectors.json")));

    [Fact]
    public void NativeNineSlotWritesYieldOnlyFiveReadablePersonalitiesForAllCustomIdRanges()
    {
        using var document = Evidence();
        Assert.Empty(document.RootElement.GetProperty("patches").EnumerateArray());
        Assert.Empty(document.RootElement.GetProperty("helperStubs").EnumerateArray());
        World w = Vectors.LoadWorld("world.json.gz");
        Rec person = w.Table(Tables.Officers)[1];
        var rows = document.RootElement.GetProperty("slotCases").EnumerateArray().ToArray();
        Assert.Equal(new[] { 3001, 4101, 5101 }, rows.Select(r => r.GetProperty("officerId").GetInt32()));
        foreach (var row in rows)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(person.Bytes.AsSpan(0x10), row.GetProperty("officerId").GetUInt16());
            int[] stored = row.GetProperty("stored").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            int[] read = row.GetProperty("read").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(9, stored.Length);
            Assert.Equal(RegisteredOfficerFields.PersonalityActiveMax, read.Count(x => x != 0));
            for (int k = 0; k < stored.Length; k++)
                BinaryPrimitives.WriteUInt16LittleEndian(person.Bytes.AsSpan(0x118 + 2*k), (ushort)stored[k]);
            Assert.Equal(read, Enumerable.Range(0, 9).Select(k => Personality.Slot(w, person, k)));
        }
    }

    [Fact]
    public void NewClanNumbersStayWithinTheNativeInitializationRange()
    {
        using var document = Evidence();
        var rows = document.RootElement.GetProperty("clanCases").EnumerateArray().ToArray();
        Assert.Equal(6, rows.Length);
        Assert.All(rows.Where(r => r.GetProperty("input").GetInt32() is > 0 and <= 6000),
            r => Assert.Equal(r.GetProperty("input").GetInt32(), r.GetProperty("output").GetInt32()));
        Assert.All(rows.Where(r => r.GetProperty("input").GetInt32() is 0 or > 6000),
            r => Assert.Equal(3001, r.GetProperty("output").GetInt32()));
        Assert.Equal(rows.Where(r => r.GetProperty("input").GetInt32() == r.GetProperty("output").GetInt32()).Max(r => r.GetProperty("input").GetInt32()), RegisteredOfficerFields.Clan.Max);
    }
}

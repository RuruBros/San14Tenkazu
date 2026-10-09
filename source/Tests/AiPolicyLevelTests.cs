using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPolicyLevelTests
{
    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPolicyLevelVectors.json.gz"));
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
    private static JsonElement[] Items(JsonElement value, string name) => value.GetProperty(name).EnumerateArray().ToArray();
    private static bool Flag(JsonElement value, string name) => value.GetProperty(name).GetBoolean();
    private static bool Variant(JsonElement row, string name) => row.GetProperty("variant").GetString() == name;

    [Fact]
    public void NativeFullPolicyLevelsMatchSelectedOfficerContributionsAndFinalClamping()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(Flag(root, "completeMatrix"));
        Assert.True(Flag(root, "clockStable"));
        Assert.Empty(Items(root, "failures"));
        Assert.Equal(1507, root.GetProperty("plannedCases").GetInt32());
        JsonElement[] rows = Items(root, "cases");
        Assert.Equal(1507, rows.Length);
        foreach (JsonElement row in rows)
        {
            AiPolicyLevelContribution[] officers = Items(row, "contributions").Select(c => new AiPolicyLevelContribution(
                Flag(c, "validPolicy"), c.GetProperty("baseLevel").GetByte(),
                c.GetProperty("creditBonus").ValueKind == JsonValueKind.Null ? 0 : c.GetProperty("creditBonus").GetInt32(),
                Items(c, "effects").Select(e => e.GetByte()).ToArray())).ToArray();
            int raw = AiPolicyLevels.Accumulate(row.GetProperty("effect").GetInt32(), officers);
            int expected = AiPolicyLevels.ReadLevel(raw);
            Assert.Equal(expected, row.GetProperty("native").GetInt32());
            Assert.InRange(expected, 0, 10);
            if (Flag(row, "validForce")) Assert.Equal(raw, Assert.Single(Items(row, "finalFrames")).GetProperty("rawLevel").GetInt32());
            else
            {
                Assert.Empty(officers);
                Assert.Empty(Items(row, "events"));
                Assert.Empty(Items(row, "finalFrames"));
            }
            Assert.All(Items(row, "events"), e => Assert.True(e.TryGetProperty("result", out _)));
        }
    }

    [Fact]
    public void NativeActualMatrixCoversEveryActiveForceAndAllEffectIndices()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        int[] forces = Items(root, "actualForceIds").Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 3, 9, 10, 28, 29, 30, 35, 45, 46, 47, 48, 49, 50 }, forces);
        JsonElement[] rows = Items(root, "cases").Where(r => Variant(r, "actual")).ToArray();
        Assert.Equal(1339, rows.Length);
        foreach (int force in forces)
        {
            JsonElement[] cases = rows.Where(r => r.GetProperty("forceId").GetInt32() == force).ToArray();
            Assert.Equal(Enumerable.Range(0, 102).Append(255), cases.Select(r => r.GetProperty("effect").GetInt32()));
            Assert.All(cases.Where(r => r.GetProperty("effect").GetInt32() is 0 or 101 or 255), r => Assert.Equal(0, r.GetProperty("native").GetInt32()));
        }
        Assert.Equal(7, rows.Single(r => r.GetProperty("forceId").GetInt32() == 29 && r.GetProperty("effect").GetInt32() == 1).GetProperty("native").GetInt32());
    }

    [Fact]
    public void NativeVisitorSelectsRulerThenEnactedStrategistAndAllThirtyFiveSlots()
    {
        using JsonDocument document = Vectors();
        JsonElement[] rows = Items(document.RootElement, "cases");
        foreach (JsonElement row in rows)
        {
            ulong[] selected = Items(row, "members").Where(m => AiPolicyLevels.SelectsMember(Flag(m, "valid"),
                m.GetProperty("role").GetString() == "ruler", m.GetProperty("remaining").GetUInt16()))
                .Select(m => m.GetProperty("pointer").GetUInt64()).ToArray();
            Assert.Equal(selected, Items(row, "contributions").Select(c => c.GetProperty("officer").GetUInt64()));
            JsonElement[] visits = Items(row, "events").Where(e => e.GetProperty("rva").GetUInt32() == 0x26AA20).ToArray();
            Assert.Equal(Flag(row, "validForce") ? 1 : 0, visits.Length);
            if (visits.Length != 0) Assert.Equal(1, visits[0].GetProperty("args")[2].GetInt32());
        }
        JsonElement[] slots = rows.Where(r => Variant(r, "enactedSlot")).ToArray();
        Assert.Equal(105, slots.Length);
        Assert.Equal(35, slots.Select(r => r.GetProperty("slotOffset").GetInt32()).Distinct().Count());
        foreach (JsonElement row in slots.Concat(rows.Where(r => Variant(r, "strategist"))))
        {
            bool enacted = row.GetProperty("remaining").GetInt32() == 0;
            Assert.Equal(enacted ? 2 : 1, Items(row, "contributions").Length);
            Assert.Equal(enacted ? 6 : 3, row.GetProperty("native").GetInt32());
        }
    }

    [Fact]
    public void NativeDuplicateEffectBytesAndRepeatedOfficerSlotsAreCounted()
    {
        using JsonDocument document = Vectors();
        JsonElement[] rows = Items(document.RootElement, "cases");
        JsonElement[] levels = rows.Where(r => Variant(r, "levelAndEffectSlots")).ToArray();
        Assert.Equal(30, levels.Length);
        foreach (JsonElement row in levels)
        {
            int level = Math.Min(row.GetProperty("baseLevel").GetInt32(), 10);
            string pattern = row.GetProperty("pattern").GetString()!;
            int multiplier = pattern switch { "one" => 1, "duplicate" => 2, "eight" => 8, _ => 0 };
            Assert.Equal(Math.Min(level * multiplier, 10), row.GetProperty("native").GetInt32());
            Assert.Equal(0, Assert.Single(Items(row, "contributions")).GetProperty("creditBonus").GetInt32());
        }
        JsonElement[] repeated = rows.Where(r => Variant(r, "repeatedOfficer")).ToArray();
        Assert.Equal(6, repeated.Length);
        foreach (JsonElement row in repeated)
        {
            int copies = row.GetProperty("slotCopies").GetInt32();
            Assert.Equal(copies + 2, Items(row, "contributions").Length);
            Assert.Equal(Math.Min((copies + 2) * 3, 10), row.GetProperty("native").GetInt32());
            if (copies >= 3) Assert.Equal(13, Assert.Single(Items(row, "finalFrames")).GetProperty("rawLevel").GetInt32());
        }
    }

    [Fact]
    public void NativeInvalidPolicyAndForceInputsSkipTheRelevantHelpers()
    {
        using JsonDocument document = Vectors();
        foreach (JsonElement row in Items(document.RootElement, "cases"))
        {
            JsonElement[] officers = Items(row, "contributions");
            JsonElement[] bonuses = Items(row, "events").Where(e => e.GetProperty("rva").GetUInt32() == 0x2A9D30).ToArray();
            Assert.Equal(officers.Count(c => Flag(c, "validPolicy")), bonuses.Length);
            Assert.All(bonuses, e => Assert.Equal(3, e.GetProperty("args")[1].GetInt32()));
            if (Variant(row, "policyIndex"))
            {
                JsonElement officer = Assert.Single(officers);
                Assert.Equal(row.GetProperty("policyIndex").GetInt32() == 120, Flag(officer, "validPolicy"));
            }
            if (Variant(row, "invalidForce"))
            {
                Assert.False(Flag(row, "validForce"));
                Assert.Equal(0, row.GetProperty("native").GetInt32());
            }
            if (Variant(row, "officerIndex") && row.GetProperty("role").GetString() == "ruler" && row.GetProperty("officerIndex").GetInt32() == 0)
            {
                Assert.False(Flag(row, "validForce"));
                Assert.Empty(officers);
            }
        }
    }
}

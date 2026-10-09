using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiInterceptOptionsTests
{
    [Fact]
    public void OriginalConsumerCallsMatchTheTwoSignedOptionsAndMobilityOrder()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AiInterceptOptionsVectors.json")));
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Empty(root.GetProperty("failures").EnumerateArray());
        Assert.Equal(13, root.GetProperty("cases").GetArrayLength());
        foreach (JsonElement row in root.GetProperty("cases").EnumerateArray())
        {
            if (row.GetProperty("kind").GetString() == "sort")
            {
                JsonElement[] factors = row.GetProperty("mobility").EnumerateArray().ToArray();
                ulong[] expected = factors.OrderByDescending(f => f.GetProperty("value").GetSingle()).Select(f => f.GetProperty("unit").GetUInt64()).ToArray();
                Assert.Equal(expected, row.GetProperty("native").EnumerateArray().Select(u => u.GetUInt64()).ToArray());
                foreach (JsonElement e in row.GetProperty("events").EnumerateArray())
                {
                    float a = factors.Single(f => f.GetProperty("unit").GetUInt64() == e.GetProperty("args")[1].GetUInt64()).GetProperty("value").GetSingle();
                    float b = factors.Single(f => f.GetProperty("unit").GetUInt64() == e.GetProperty("args")[2].GetUInt64()).GetProperty("value").GetSingle();
                    Assert.Equal(e.GetProperty("result").GetUInt32() != 0, AiInterceptRules.MobilityPrecedes(a, b));
                }
                continue;
            }
            int p0 = row.GetProperty("p0").GetInt32(), p1 = row.GetProperty("p1").GetInt32();
            JsonElement[] events = row.GetProperty("events").EnumerateArray().ToArray();
            Assert.Equal(AiInterceptRules.SortMembers(row.GetProperty("members").GetArrayLength(), p1), events.Any(e => e.GetProperty("rva").GetUInt32() == 0x5D3B0));
            Assert.Equal(AiInterceptRules.CollectHostileCenters(p0, 48400), events.Any(e => e.GetProperty("rva").GetUInt32() == 0xCF920));
            foreach (JsonElement gate in row.GetProperty("gates").EnumerateArray())
                Assert.Equal(gate.GetProperty("rva").GetUInt32() == 0x6AAAA ? p1 : p0, gate.GetProperty("value").GetInt32());
        }
    }
}

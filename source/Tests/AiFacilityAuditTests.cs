using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiFacilityAuditTests
{
    [Fact]
    public void ActualHomeAndGateAreasMatchNativeFacilityGateAndMode()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiFacilityAuditVectors.json")));
        JsonElement rows = document.RootElement.GetProperty("cases");
        Assert.Equal(50, rows.GetArrayLength());
        foreach (JsonElement row in rows.EnumerateArray())
        {
            JsonElement[] events = row.GetProperty("events").EnumerateArray().ToArray();
            bool blocked = (events.Single(e => e.GetProperty("rva").GetUInt32() == 0x233340).GetProperty("result").GetUInt32() & 0xFF) != 0;
            Assert.Equal(AiExecutorRules.FacilityAreaStatus(blocked), row.GetProperty("status").GetInt32());
            if (blocked)
            {
                Assert.Empty(row.GetProperty("native").EnumerateArray());
                Assert.Equal(JsonValueKind.Null, row.GetProperty("special").ValueKind);
            }
            else
            {
                int helper = events.Single(e => e.GetProperty("rva").GetUInt32() == 0x3F5D0).GetProperty("result").GetInt32();
                Assert.Equal(AiExecutorRules.FacilityMode(helper, row.GetProperty("inputMode").GetInt32()), row.GetProperty("outputMode").GetInt32());
                Assert.Equal(0, row.GetProperty("special").GetInt32()); // A special result was not observed in this snapshot.
            }
        }
    }
}

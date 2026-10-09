using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiParameterAuditTests
{
    [Fact]
    public void ParameterConsumersMatchRecordedNativeCalls()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiParameterAuditVectors.json")));
        JsonElement rows = document.RootElement.GetProperty("cases");
        Assert.True(rows.GetArrayLength() >= 60);
        foreach (JsonElement row in rows.EnumerateArray())
        {
            switch (row.GetProperty("kind").GetString())
            {
                case "districtType":
                    Assert.Equal(1, row.GetProperty("status").GetInt32());
                    Assert.Equal(AiParameterRules.DistrictPurpose(row.GetProperty("input").GetInt32()), row.GetProperty("native").GetSByte());
                    break;
                case "departurePolicies":
                    bool[] flags = row.GetProperty("flags").EnumerateArray().Select(x => x.GetBoolean()).ToArray();
                    Assert.Equal(AiParameterRules.DeparturePolicies(Ints(row.GetProperty("before")), flags[0], flags[1], flags[2]), Ints(row.GetProperty("native")));
                    Assert.All(Ints(row.GetProperty("routes")), x => Assert.Equal(48400, x));
                    break;
                case "facilityHexOption":
                    ulong area = row.GetProperty("area").GetUInt64();
                    bool mode = AiParameterRules.FacilityBorderMode(area == row.GetProperty("homeArea").GetUInt64(),
                        area == row.GetProperty("gateArea").GetUInt64(), row.GetProperty("adjacent").GetBoolean(), row.GetProperty("option").GetInt32());
                    Assert.Equal(mode ? 1 : 0, row.GetProperty("nativeMode").GetInt32());
                    break;
                case "interceptCache":
                    Assert.Equal(Ints(row.GetProperty("input")).Reverse(), Ints(row.GetProperty("native")));
                    break;
                case "occupyCache":
                    Assert.Equal(new[] { 123, row.GetProperty("input").GetInt32() }, Ints(row.GetProperty("native")));
                    Assert.Equal(row.GetProperty("expectedPointer").GetUInt64(), row.GetProperty("pointer").GetUInt64());
                    break;
                case "baseAttackCache":
                    Assert.Equal(row.GetProperty("input").GetInt32(), row.GetProperty("native").GetInt32());
                    break;
                case "relayFlags":
                    Assert.Equal(Enumerable.Repeat(row.GetProperty("input").GetInt32() != 0 ? 1 : 0, 3), Ints(row.GetProperty("native")));
                    break;
                case "tradeTroops":
                    bool allowed = AiParameterRules.TradeTroopsAllowed(row.GetProperty("input").GetInt32(), row.GetProperty("troops").GetInt32());
                    Assert.Equal(allowed, row.GetProperty("commandCalled").GetBoolean());
                    Assert.Equal(allowed ? 1 : 2, row.GetProperty("status").GetInt32());
                    break;
                default: Assert.Fail("Unknown parameter evidence kind."); break;
            }
        }
    }
    private static int[] Ints(JsonElement values) => values.EnumerateArray().Select(x => x.GetInt32()).ToArray();
}

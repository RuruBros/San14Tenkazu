using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiExecutorAuditTests
{
    [Fact]
    public void RemainingExecutorRulesMatchNativeBodiesAndStates()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiExecutorAuditVectors.json")));
        JsonElement rows = document.RootElement.GetProperty("cases");
        Assert.Equal(8, rows.EnumerateArray().Select(r => r.GetProperty("class").GetString()).Distinct().Count());
        foreach (JsonElement row in rows.EnumerateArray())
        {
            int status = row.GetProperty("status").GetInt32();
            switch (row.GetProperty("class").GetString())
            {
                case "CExclusionUserArmyAttachedFootholdNode":
                    bool helper = row.GetProperty("helper").GetBoolean();
                    Assert.Equal(helper ? 1 : 2, status);
                    Assert.Equal(AiExecutorRules.ExclusionFlags(row.GetProperty("before").GetUInt32(), helper), row.GetProperty("after").GetUInt32());
                    break;
                case "CDefendFootholdRemoveNode":
                    ulong value = row.GetProperty("value").GetUInt64();
                    var expected = row.GetProperty("before").EnumerateArray().Where(r => r[0].GetUInt64() != value).Select(Pair).ToArray();
                    var actual = row.GetProperty("native").EnumerateArray().Select(Pair).ToArray();
                    Assert.Equal(1, status);
                    Assert.Equal(expected.Length, actual.Length);
                    for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i]);
                    break;
                case "CTroopsSetTargetTroopsNode":
                    bool blocked = row.TryGetProperty("blocked", out JsonElement b) && b.GetBoolean();
                    Assert.Equal(blocked ? 2 : 1, status);
                    Assert.Equal(row.GetProperty("expectedTarget").GetUInt16(), row.GetProperty("nativeTarget").GetUInt16());
                    if (!blocked)
                    {
                        var kind = AiExecutorRules.TargetKind(row.GetProperty("purpose").GetUInt16());
                        if (kind != AiTargetKind.None) Assert.Equal(kind == AiTargetKind.CautionTroops ? "caution" : kind.ToString(), row.GetProperty("kind").GetString(), ignoreCase: true);
                        if (row.TryGetProperty("inputIndex", out JsonElement input)) Assert.Equal(AiExecutorRules.TargetIndex(input.GetInt32()), row.GetProperty("nativeTarget").GetUInt16());
                    }
                    break;
                case "CExecuteAreaOccupyNode":
                case "CExecuteAttackAreaBuildingNode":
                    Assert.Equal(1, status); // Fresh state and no relay hexes: early success.
                    Assert.Equal(row.GetProperty("stateBefore").GetString(), row.GetProperty("stateAfter").GetString());
                    break;
                case "CExecuteAttractNode":
                    byte[] state = Convert.FromHexString(row.GetProperty("stateAfter").GetString()!);
                    Assert.Equal(AiExecutorRules.AttractStatus(row.GetProperty("targetAllowed").GetBoolean(), state[0x10]), status);
                    break;
                case "CDecideAreaOccupyRouteNode":
                    ushort purpose = row.GetProperty("purpose").GetUInt16();
                    bool path = row.GetProperty("events").EnumerateArray().Any(e => e.GetProperty("result").GetUInt32() != 0);
                    Assert.Equal(AiExecutorRules.OccupyRouteStatus(purpose, path), status);
                    if (AiLeafRules.RejectBuildingPurpose(purpose))
                    {
                        Assert.Equal(2, status);
                        Assert.Empty(row.GetProperty("native").EnumerateArray());
                        Assert.Empty(row.GetProperty("events").EnumerateArray());
                    }
                    break;
                case "CGetCandidateFacilityListNode":
                    int choice = row.GetProperty("events")[0].GetProperty("result").GetInt32();
                    Assert.Equal(1, status);
                    Assert.Equal(AiExecutorRules.FacilityMode(choice, row.GetProperty("mode").GetInt32()), row.GetProperty("outputMode").GetInt32());
                    Assert.InRange(row.GetProperty("special").GetInt32(), 0, 1);
                    break;
                default: Assert.Fail("Unrecognized executor fixture class."); break;
            }
        }
    }
    private static ulong[] Pair(JsonElement pair) => pair.EnumerateArray().Select(p => p.GetUInt64()).ToArray();
}

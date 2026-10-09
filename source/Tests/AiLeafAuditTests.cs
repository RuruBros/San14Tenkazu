using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiLeafAuditTests
{
    [Fact]
    public void CorrectedLeafRulesMatchOriginalNativeBodies()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiLeafAuditVectors.json")));
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.Equal(14, cases.EnumerateArray().Select(r => r.GetProperty("class").GetString()).Distinct().Count());
        foreach (JsonElement row in cases.EnumerateArray())
        {
            string name = row.GetProperty("class").GetString()!;
            int status = row.GetProperty("status").GetInt32();
            switch (name)
            {
                case "CGetSpecificForceDataNode":
                    Assert.Equal(AiLeafRules.SpecificForce(row.GetProperty("choice").GetInt32(), UArray(row.GetProperty("forces")),
                        row.GetProperty("lords").EnumerateArray().Select(v => v.GetInt32()).ToArray()), row.GetProperty("native").GetUInt64());
                    break;
                case "CCautionFootholdRemoveNode":
                    Assert.Equal(AiLeafRules.RemoveAll(UArray(row.GetProperty("before")), row.GetProperty("value").GetUInt64()), UArray(row.GetProperty("native")));
                    break;
                case "CDistrictListAddTacticsNode":
                    ulong district = row.GetProperty("value").GetUInt64();
                    Assert.Equal(AiLeafRules.AppendDistrict(UArray(row.GetProperty("before")), district, district != 0), UArray(row.GetProperty("native")));
                    break;
                case "CCutSupplyNode":
                    Assert.Equal(2, status);
                    break;
                case "CResolutionFakeNewsNode":
                    JsonElement resolution = row.GetProperty("events")[0];
                    Assert.Equal(AiLeafRules.ResolutionStatus(resolution.GetProperty("result").GetUInt32() != 0), status);
                    Assert.Equal(row.GetProperty("inputs")[1].GetUInt64(), resolution.GetProperty("args")[0].GetUInt64());
                    Assert.Equal(row.GetProperty("inputs")[0].GetUInt64(), resolution.GetProperty("args")[1].GetUInt64());
                    break;
                case "CGetNearCapitalNode":
                    Assert.Equal(1, status);
                    Assert.Equal(2UL, row.GetProperty("events")[0].GetProperty("args")[1].GetUInt64());
                    break;
                case "CCommandExecutionInspectNode":
                    Assert.Equal(1, status);
                    Assert.Equal(AiLeafRules.InspectNeedsCandidates(row.GetProperty("storedReserve").GetInt32()),
                        row.GetProperty("events").EnumerateArray().Any(e => e.GetProperty("rva").GetInt32() == 0x6DDCB0));
                    break;
                case "CTradePriorityCountrySelectNode":
                    Assert.Equal(2, status);
                    Assert.Empty(row.GetProperty("events")[0].GetProperty("list").EnumerateArray());
                    break;
                case "CCommandExecutionGovernmentNode":
                    var assignment = AiLeafRules.PolicyAssignment(row.GetProperty("slot").GetInt32(), row.GetProperty("personId").GetInt32(),
                        row.GetProperty("oldAdvisor").GetInt32(), row.GetProperty("oldGroups").EnumerateArray().Select(IArray).ToArray());
                    JsonElement issued = row.GetProperty("events")[0];
                    Assert.Equal(1, status);
                    Assert.Equal(assignment.Advisor, issued.GetProperty("advisor").GetInt32());
                    for (int i = 0; i < 5; i++) Assert.Equal(assignment.Groups[i], IArray(issued.GetProperty("groups")[i]));
                    break;
                case "CCommandExecutionUnattendedHomeNode":
                    bool allowed = row.GetProperty("points").GetInt32() >= row.GetProperty("requirement").GetInt32();
                    Assert.Equal(allowed ? 1 : 2, status);
                    Assert.Equal(allowed ? 1 : 0, row.GetProperty("events").GetArrayLength());
                    if (allowed)
                    {
                        JsonElement command = row.GetProperty("events")[0];
                        Assert.Equal(11, command.GetProperty("kind").GetInt32());
                        Assert.Equal(row.GetProperty("person").GetUInt64(), command.GetProperty("person").GetUInt64());
                        Assert.Equal(row.GetProperty("city").GetUInt64(), command.GetProperty("city").GetUInt64());
                    }
                    break;
                case "CExecuteAttackBuildingNode":
                    Assert.Equal(AiLeafRules.RejectBuildingPurpose((ushort)row.GetProperty("purpose").GetInt32()) ? 2 : 1, status);
                    break;
                default:
                    Assert.Equal(1, status);
                    Assert.Equal(row.GetProperty("expected").GetRawText(), row.GetProperty("native").GetRawText());
                    break;
            }
        }
    }
    private static ulong[] UArray(JsonElement array) => array.EnumerateArray().Select(v => v.GetUInt64()).ToArray();
    private static int[] IArray(JsonElement array) => array.EnumerateArray().Select(v => v.GetInt32()).ToArray();
}

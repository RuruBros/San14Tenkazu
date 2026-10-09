using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiCommandPredicateTests
{
    [Fact]
    public void GateRoutingAndMembershipMatchObservedNativeCalls()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiCommandPredicateVectors.json")));
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.Equal(50, cases.EnumerateArray().Select(row => row.GetProperty("predicate").GetInt32()).Distinct().Count());
        Assert.Contains(cases.EnumerateArray(), row => row.GetProperty("native").GetInt32() == 1);
        foreach (JsonElement row in cases.EnumerateArray())
        {
            int predicate = row.GetProperty("predicate").GetInt32();
            var plan = AiCommandPredicates.GetPlan(predicate);
            Assert.Equal(row.GetProperty("sourceKind").GetString(), plan.Input.ToString());
            Assert.Equal(row.GetProperty("gate").GetInt32(), plan.Gate);
            Assert.Equal(row.GetProperty("candidates").GetInt32(), plan.Candidates);
            Assert.Equal(row.GetProperty("listArgument").GetInt32(), plan.ListArgument);
            Assert.Equal(row.GetProperty("resolved").GetInt64(), AiCommandPredicates.ResolveSource(plan,
                row.GetProperty("first").GetInt64(), row.GetProperty("mainDistrict").GetInt64(),
                row.GetProperty("districtForce").GetInt64(), row.GetProperty("agentDistrict").GetInt64(),
                row.GetProperty("validAgent").GetBoolean()));
            bool actual = AiCommandPredicates.Evaluate(plan, row.GetProperty("validFirst").GetBoolean(),
                row.GetProperty("validSecond").GetBoolean(), row.GetProperty("permission").GetBoolean(),
                row.GetProperty("gatePassed").GetBoolean(), row.GetProperty("candidatePresent").GetBoolean());
            Assert.Equal(row.GetProperty("native").GetInt32() != 0, actual);
        }
    }
}

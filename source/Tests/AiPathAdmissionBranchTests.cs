using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathAdmissionBranchTests
{
    [Fact]
    public void NativeCityGateBoatAndReturnBranchesMatchTheirActualHelperInputs()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Empty(root.GetProperty("failures").EnumerateArray());
        Assert.Equal(2566, root.GetProperty("cases").GetArrayLength());
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (JsonElement row in root.GetProperty("cases").EnumerateArray())
        {
            AiPathAdmission facts = row.GetProperty("facts").Deserialize<AiPathAdmission>(options)!;
            Assert.Equal(row.GetProperty("native").GetInt32() != 0, facts.Allowed());
        }
        var routes = root.GetProperty("cases").EnumerateArray().SelectMany(r => r.GetProperty("events").EnumerateArray())
            .Where(e => e.GetProperty("rva").GetUInt32() == 0x2C5070).ToArray();
        Assert.Contains(routes, r => r.GetProperty("result").GetUInt32() == 1);
        Assert.Contains(routes, r => r.GetProperty("result").GetUInt32() == 0);
    }

    [Fact]
    public void ObservedConditionalEdgesPreserveTheRedundantGateCenterComparison()
    {
        using JsonDocument document = Vectors();
        JsonElement[] branches = document.RootElement.GetProperty("branchCoverage").EnumerateArray().ToArray();
        Assert.Equal(39, branches.Length);
        Assert.Equal(38, branches.Count(r => r.GetProperty("taken").GetBoolean() && r.GetProperty("fallthrough").GetBoolean()));
        JsonElement duplicate = Assert.Single(branches, r => !r.GetProperty("taken").GetBoolean());
        Assert.Equal(0x18A23Eu, duplicate.GetProperty("rva").GetUInt32());
        Assert.True(duplicate.GetProperty("fallthrough").GetBoolean());
        // 18A1D8 already requires tile index == gate center. A goal matching the center returned at 18A22D;
        // the subsequent 18A23E compares the same value, so its taken edge cannot follow that earlier mismatch.
        foreach (JsonElement row in document.RootElement.GetProperty("cases").EnumerateArray()
            .Where(r => r.GetProperty("role").GetString() == "gateGoalLoop"))
        {
            Assert.True(row.GetProperty("facts").GetProperty("atCenter").GetBoolean());
            Assert.Equal(1, row.GetProperty("native").GetInt32());
        }
    }

    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathAdmissionBranchVectors.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}

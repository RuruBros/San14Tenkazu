using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiCooperationTests
{
    [Fact]
    public void CooperationModesAndBoundariesMatchTheNativeFunction()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiCooperationVectors.json")));
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.Equal(418, cases.GetArrayLength());
        foreach (JsonElement row in cases.EnumerateArray())
        {
            int[] a = row.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(a[14] != 0, AiCooperation.WithOtherForce(a[0], (byte)a[1], (byte)a[2], (byte)a[3], (byte)a[4],
                (short)a[5], (short)a[6], a[7] != 0, a[8] != 0,
                (short)a[9], (short)a[10], (short)a[11], (short)a[12], (short)a[13]));
        }
    }

    [AiFileFact]
    public void TheExchangeBranchesAskForModeThreeInEveryCorpsType()
    {
        AiNode[] nodes = AiTreeFile.Read(AiFileFactAttribute.Folder)[1].Nodes;
        foreach (int index in new[] { 1161, 4734, 8026 })
        {
            AiTerm term = Assert.Single(AiCondition.Of(nodes[index].Params).Terms, t => t.Function == 160);
            Assert.False(term.Negated);
            Assert.Equal(3, term.Args[0]);
            Assert.Equal(AiCatalog.NameHash("District"), unchecked((uint)term.Args[1]));
            Assert.Equal(AiCatalog.NameHash("EachFirstDistrict"), unchecked((uint)term.Args[2]));
        }
    }
}

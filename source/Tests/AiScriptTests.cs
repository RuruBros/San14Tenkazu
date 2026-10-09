using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiScriptTests
{
    [Fact]
    public void TheSevenTermSumAndConstructorMatchNativeExecution()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiScript4Vectors.json")));
        Assert.Equal(200, document.RootElement.GetProperty("cases").GetArrayLength());
        foreach (JsonElement item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            int[] a = item.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(a[7], AiScriptScore.SumSeven(a.AsSpan(0, 7)));
        }
        Assert.All(document.RootElement.GetProperty("constructorArgumentHashes").EnumerateArray(), x => Assert.Equal(0, x.GetInt32()));
        Assert.Equal(6, document.RootElement.GetProperty("liveSeventhArgumentHashes").GetArrayLength());
        Assert.All(document.RootElement.GetProperty("liveSeventhArgumentHashes").EnumerateArray(), x => Assert.Equal(0, x[2].GetInt32()));
    }

    [AiFileFact]
    public void TheStockSevenTermSumCallersProvideSixHashes()
    {
        AiNode[] nodes = AiTreeFile.Read(AiFileFactAttribute.Folder)[1].Nodes;
        AiNode[] callers = nodes.Where(n => n.Class == 0x72E99C29 && n.Params[0] == 4).ToArray();
        Assert.Equal(new[] { 915, 1617, 4488, 5190, 7780, 8482 }, callers.Select(n => n.Index));
        Assert.All(callers, n => Assert.Equal(8, n.Params.Length));
        foreach (int index in new[] { 915, 4488, 7780 })
        {
            Assert.Equal(new[] { nodes[index - 6].Params[2], nodes[index - 3].Params[2],
                nodes[index - 2].Params[2], nodes[index - 1].Params[2], 0, 0 }, nodes[index].Params.Skip(2));
        }
        foreach (int index in new[] { 1617, 5190, 8482 })
        {
            Assert.Equal(new[] { nodes[index - 9].Params[2], nodes[index - 1].Params[2], 0, 0, 0, 0 }, nodes[index].Params.Skip(2));
        }
    }

    [Fact]
    public void EmployFocusMatchesTheNativeScript()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiScript10Vectors.json")));
        Assert.Equal(200, document.RootElement.GetProperty("cases").GetArrayLength());
        foreach (JsonElement item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            int[] a = item.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(a[2], AiScriptScore.EmployFocus(a[0], a[1]));
        }
    }

    [AiFileFact]
    public void EmployFocusCombinesTheTwoPersonalityFieldsInEveryCorpsType()
    {
        AiNode[] nodes = AiTreeFile.Read(AiFileFactAttribute.Folder)[1].Nodes;
        foreach (int index in new[] { 779, 4352, 7644 })
        {
            Assert.Equal(10, nodes[index].Params[0]);
            Assert.Equal(19, nodes[index - 2].Params[1]);
            Assert.Equal(15, nodes[index - 1].Params[1]);
            Assert.Equal(nodes[index - 2].Params[2], nodes[index].Params[2]);
            Assert.Equal(nodes[index - 1].Params[2], nodes[index].Params[3]);
            AiTerm[] guards = AiCondition.Of(nodes[index + 8].Params).Terms;
            Assert.Equal(new[] { nodes[index].Params[1], nodes[index + 6].Params[2] }, guards.Select(t => t.Args[0]));
            Assert.Equal(new[] { 20, 80 }, guards.Select(t => t.Args[1]));
            Assert.False(guards[0].Negated);
            Assert.True(guards[1].Negated);
        }
    }

    [Fact]
    public void WarlikePowerMatchesTheNativeScript()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiScript13Vectors.json")));
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.Equal(200, cases.GetArrayLength());
        foreach (JsonElement item in cases.EnumerateArray())
        {
            int[] a = item.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(a[2], AiScriptScore.PowerByWarlike(a[0], a[1]));
        }
    }

    [AiFileFact]
    public void WarlikePowerCallersReadWarlikeAndReplaceTheirPowerVariable()
    {
        AiNode[] nodes = AiTreeFile.Read(AiFileFactAttribute.Folder)[1].Nodes;
        AiNode[] callers = nodes.Where(n => n.Class == 0x72E99C29 && n.Params[0] == 13).ToArray();
        Assert.Equal(new[] { 519, 1957, 2092 }, callers.Select(n => n.Index));
        foreach (AiNode caller in callers)
        {
            Assert.Equal(caller.Params[1], caller.Params[2]);
            Assert.Equal(13, Assert.Single(nodes.Where(n => n.Index < caller.Index &&
                n.Class == 0x0BB9F92D && n.Params[2] == caller.Params[3]).TakeLast(1)).Params[1]);
        }
    }
}

using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiFriendTests
{
    private static JsonDocument NativeAnswers() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AiFriendVectors.json")));

    [Fact]
    public void TheScoreMatchesTheGamesScriptsOnCopiedMemory()
    {
        using JsonDocument document = NativeAnswers();
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.Equal(200, cases.GetArrayLength());
        Assert.Equal(1, document.RootElement.GetProperty("zeroDivisor").GetInt32());
        foreach (JsonElement item in cases.EnumerateArray())
        {
            int[] a = item.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(a[6], AiFriendScore.Evaluate(a[0], a[1], a[2], a[3], a[4], a[5]));
        }
    }

    [Fact]
    public void CompatibilityMatchesTheGamesOfficerGetter()
    {
        using JsonDocument document = NativeAnswers();
        JsonElement cases = document.RootElement.GetProperty("compatibility");
        Assert.Equal(36, cases.GetArrayLength());
        foreach (JsonElement item in cases.EnumerateArray())
        {
            int[] a = item.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(a[2], AiFriendScore.Compatibility((byte)a[0], (byte)a[1]));
        }
    }

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(1, 9, 0)]
    [InlineData(1, 10, -9)]
    [InlineData(9, 10, -1)]
    [InlineData(1, 20, -9)]
    public void TheTitleAdjustmentHasBothTreeGuards(int rank, int trait, int expected)
    {
        Assert.Equal(expected, AiFriendScore.Evaluate(100, 100, 100, 0, 0, 0, rank, trait));
    }

    [AiFileFact]
    public void TheTreeKeepsTheVerifiedScriptInputsAndPersistentBiases()
    {
        AiNode[] nodes = AiTreeFile.Read(AiFileFactAttribute.Folder)[0].Nodes;
        Assert.Equal(new[] { 8, 11, 12 }, new[] { 100, 102, 110 }.Select(i => nodes[i].Params[0]));
        Assert.Equal(nodes[98].Params[2], nodes[100].Params[2]);
        Assert.Equal(nodes[100].Params[1], nodes[102].Params[2]);
        Assert.Equal(nodes[102].Params[1], nodes[110].Params[6]);
        Assert.Equal(nodes[87].Params[1], nodes[110].Params[2]);
        Assert.Equal(nodes[108].Params[2], nodes[110].Params[3]);
        Assert.Equal(nodes[109].Params[2], nodes[110].Params[4]);
        Assert.Equal(5, nodes[109].Params[1]);
        // The only writes to the two bias hashes in the entire friend block: outside the loop=0, inside=30.
        foreach ((int initial, int matched, int scriptArg) in new[] { (79, 105, 5), (80, 107, 7) })
        {
            int hash = nodes[initial].Params[0];
            Assert.Equal(hash, nodes[matched].Params[0]);
            Assert.Equal(hash, nodes[110].Params[scriptArg]);
            Assert.Equal(new[] { 0, 30 }, new[] { nodes[initial].Params[1], nodes[matched].Params[1] });
            Assert.Equal(78, nodes[initial].Parent);
            Assert.Equal(new[] { initial, matched }, nodes[77..143]
                .Where(n => n.Class == nodes[initial].Class && n.Params[0] == hash).Select(n => n.Index));
        }
        AiTerm[] guards = AiCondition.Of(nodes[114].Params).Terms;
        Assert.Equal(new[] { 20, 21 }, guards.Select(t => t.Function)); // Greater, GreaterEqual
        Assert.Equal(new[] { nodes[112].Params[2], nodes[113].Params[2] }, guards.Select(t => t.Args[0]));
        Assert.Equal(new[] { 0, 10 }, guards.Select(t => t.Args[1]));
        Assert.Equal(10, nodes[116].Params[1]);
        Assert.Equal(nodes[112].Params[2], nodes[117].Params[1]);
        Assert.Equal(nodes[116].Params[0], nodes[118].Params[1]);
    }
}

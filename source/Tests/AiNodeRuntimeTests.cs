using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiNodeRuntimeTests
{
    [AiFileFact]
    public void LoopParametersFollowTheNativeLoaderAndExcludeTheAgentCache()
    {
        AiTree tree = AiTreeFile.Read(AiFileFactAttribute.Folder)[1];
        AiNode node = tree.Nodes[4143];
        AiClass cls = AiCatalog.Class(node.Class)!;
        Assert.Equal("CLoopListElementNode", cls.Name);
        Assert.Equal(new[] { 0x28, 0x2C, 0x30, 0x40 }, cls.Offsets);
        Assert.DoesNotContain(0x48, cls.Offsets!);
        Assert.All(Enumerable.Range(0, 4), p => Assert.False(AiCatalog.IsNumber(node, p)));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiNodeRuntimeVectors.json")));
        JsonElement verification = document.RootElement.GetProperty("verification");
        foreach (JsonElement row in verification.GetProperty("loop_loader").EnumerateArray())
        {
            var memory = new NativeNode(Convert.FromHexString(row.GetProperty("raw").GetString()!));
            var addresses = new ulong[tree.Nodes.Length];
            addresses[node.Index] = NativeNode.Address;
            var live = new AiLiveTree(tree, 0, addresses);
            Assert.Equal(row.GetProperty("params").EnumerateArray().Select(v => v.GetInt32()),
                Enumerable.Range(0, 4).Select(p => AiMemory.Value(memory, live, node.Index, p)!.Value));
        }
        Assert.All(verification.GetProperty("loop_agent_type").EnumerateArray(), row =>
            Assert.Equal(row[0].GetInt32(), row[1].GetInt32()));
        AiTree[] trees = AiTreeFile.Read(AiFileFactAttribute.Folder);
        JsonElement liveParams = verification.GetProperty("live_loop_params");
        Assert.Equal(trees.Sum(t => t.Nodes.Count(n => n.Class == node.Class)), liveParams.GetArrayLength());
        foreach (JsonElement row in liveParams.EnumerateArray())
            Assert.Equal(trees[row[0].GetInt32()].Nodes[row[1].GetInt32()].Params,
                row[2].EnumerateArray().Select(v => v.GetInt32()));
    }

    private sealed class NativeNode(byte[] data) : IRecordMemory
    {
        internal const ulong Address = 0x1000;
        public byte[] Read(ulong address, int length) => data.AsSpan(checked((int)(address - Address)), length).ToArray();
        public void Write(ulong address, byte[] bytes) => throw new NotSupportedException();
    }
}

using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathCompositionTests
{
    [Fact]
    public void OriginalWrappersMatchObservedChainsAndRetainFailedSegments()
    {
        using JsonDocument document = Load();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Equal(80, root.GetProperty("cases").GetArrayLength());
        foreach (JsonElement row in root.GetProperty("cases").EnumerateArray())
        {
            AiComposedPath result = AiPathComposition.Compose(Chains(row));
            Assert.False(result.NullHeadFault);
            Assert.Equal(row.GetProperty("native").GetInt32() != 0, result.Success);
            Assert.Equal(row.GetProperty("cost").GetInt32(), result.Cost);
            Assert.Equal(row.GetProperty("path").EnumerateArray().Select(e => e.GetUInt32()), result.Hexes);
            Assert.Equal(AiPathComposition.OutputOffset(row.GetProperty("slot").GetInt32()), row.GetProperty("outputOffset").GetInt32());
            uint[] targets = row.GetProperty("targets").EnumerateArray().Select(e => e.GetUInt32()).ToArray();
            int index = 0;
            foreach (JsonElement segment in row.GetProperty("events").EnumerateArray())
            {
                Assert.Equal(AiPathComposition.Source(12780, targets, index++), segment.GetProperty("source").GetUInt32());
                Assert.Equal(AiPathComposition.SegmentClearBytes, segment.GetProperty("scratchClearBytes").GetInt32());
            }
        }
        foreach (JsonElement fault in root.GetProperty("failures").EnumerateArray())
        {
            Assert.False(fault.TryGetProperty("native", out _));
            Assert.True(AiPathComposition.Compose(Chains(fault)).NullHeadFault);
            Assert.Contains("0x20", fault.GetProperty("error").GetString());
        }
        Assert.Equal(2, root.GetProperty("failures").GetArrayLength());
    }

    [Fact]
    public void ScratchClearDoesNotCoverPreviouslyVisitedHighHexes()
    {
        using JsonDocument document = Load();
        foreach (JsonElement row in document.RootElement.GetProperty("cases").EnumerateArray()
                     .Where(e => e.GetProperty("targets").GetArrayLength() > 1))
        {
            JsonElement source = row.GetProperty("events")[1].GetProperty("nodesBefore")[0];
            Assert.NotEqual(0, source.GetProperty("flags").GetInt32());
            Assert.True(source.GetProperty("id").GetInt32() * AiPathComposition.SearchNodeBytes >= AiPathComposition.SegmentClearBytes);
        }
        Assert.Equal(1210, AiPathComposition.SegmentClearBytes / AiPathComposition.SearchNodeBytes);
    }

    [Fact]
    public void LaterChainSkipsItsHeadAndUsesTheRemainingHeadCost()
    {
        IReadOnlyList<AiPathNode>[] segments = [
            new AiPathNode[] { new(10, 300), new(11, 0) },
            Array.Empty<AiPathNode>(),
            new AiPathNode[] { new(20, 900), new(21, 600), new(22, 0) }];
        AiComposedPath path = AiPathComposition.Compose(segments);
        Assert.Equal(new uint[] { 10, 11, 21, 22 }, path.Hexes);
        Assert.Equal(900, path.Cost);
        Assert.True(path.Success);
        Assert.Equal(20u, AiPathComposition.Source(10, new uint[] { 11, 20, 22 }, 2));
        Assert.Equal(1320, AiPathComposition.OutputCapacity);
    }

    private static JsonDocument Load() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiPathCompositionVectors.json")));
    private static IReadOnlyList<AiPathNode>[] Chains(JsonElement row) => row.GetProperty("events").EnumerateArray()
        .Select(e => (IReadOnlyList<AiPathNode>)e.GetProperty("chain").EnumerateArray()
            .Select(n => new AiPathNode(n.GetProperty("id").GetUInt32(), n.GetProperty("cost").GetInt32())).ToArray()).ToArray();
}

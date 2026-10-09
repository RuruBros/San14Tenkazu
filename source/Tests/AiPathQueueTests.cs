using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathQueueTests
{
    [Fact]
    public void OriginalCompleteRemovalMatchesEveryLinkAndKeepsPayload()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("payloadUnchanged").GetBoolean());
        Assert.Equal(5565, root.GetProperty("cases").GetArrayLength());
        foreach (JsonElement row in root.GetProperty("cases").EnumerateArray())
        {
            int[] priorities = Numbers(row, "priorities");
            int[] left = Numbers(row, "leftBefore"), right = Numbers(row, "rightBefore");
            int head = AiPathQueue.Remove(row.GetProperty("rootBefore").GetInt32(), row.GetProperty("target").GetInt32(), priorities, left, right);
            Assert.Equal(row.GetProperty("rootAfter").GetInt32(), head);
            Assert.Equal(Numbers(row, "leftAfter"), left);
            Assert.Equal(Numbers(row, "rightAfter"), right);
        }
    }

    [Fact]
    public void OriginalHelperHasBothEdgesOfEveryConditionalBranchObserved()
    {
        using JsonDocument document = Vectors();
        JsonElement branches = document.RootElement.GetProperty("branchCoverage");
        Assert.Equal(16, branches.GetArrayLength());
        Assert.All(branches.EnumerateArray(), b =>
        {
            Assert.True(b.GetProperty("taken").GetBoolean(), b.GetProperty("text").GetString());
            Assert.True(b.GetProperty("fallthrough").GetBoolean(), b.GetProperty("text").GetString());
        });
    }

    private static int[] Numbers(JsonElement row, string key) => row.GetProperty(key).EnumerateArray().Select(n => n.GetInt32()).ToArray();
    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathQueueVectors.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}

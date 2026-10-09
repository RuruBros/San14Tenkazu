using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiOccupyStageTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiOccupyStageVectors.json")));
    private static JsonElement[] Items(JsonElement row, string name) => row.GetProperty(name).EnumerateArray().ToArray();

    [Fact]
    public void NativeConstructionPollingAndCompletionClearMatchCoreRules()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Single(Items(root, "failures"));
        Assert.Equal(8, Items(root, "cases").Length);
        foreach (JsonElement row in Items(root, "cases").Concat(Items(root, "failures")))
        {
            JsonElement poll = row.GetProperty("pollInputs");
            byte expected = AiExecutorRules.AreaConstructionPoll(row.GetProperty("inputStage").GetByte(),
                poll.GetProperty("validHex").GetBoolean(), poll.GetProperty("validBuilding").GetBoolean(),
                poll.GetProperty("durability").GetUInt16(), poll.GetProperty("maximum").GetUInt32());
            JsonElement firstPoll = Items(row, "frames").First(f => f.GetProperty("rva").GetUInt32() == 0x666D3);
            Assert.Equal(expected, firstPoll.GetProperty("stage").GetByte());
            if (!row.TryGetProperty("status", out _)) continue; // Prefix evidence, not a completed native call.
            JsonElement beforeClear = Items(row, "frames").Single(f => f.GetProperty("rva").GetUInt32() == 0x668AE);
            JsonElement afterClear = Items(row, "frames").Single(f => f.GetProperty("rva").GetUInt32() == 0x668BA);
            Assert.Equal(AiExecutorRules.ClearAreaConstructionCompletion(beforeClear.GetProperty("stage").GetByte()),
                afterClear.GetProperty("stage").GetByte());
        }
    }

    [Fact]
    public void FullNativeCallsExerciseConstructionCommandsAndRouteCompletion()
    {
        using JsonDocument document = Vectors();
        JsonElement[] rows = Items(document.RootElement, "cases");
        JsonElement Find(string variant) => rows.Single(r => r.GetProperty("variant").GetString() == variant);
        foreach (string variant in new[] { "stage0BuildEnabled", "stage2Incomplete", "stage2EmptyHex", "stage2OutOfRangeHex" })
        {
            JsonElement row = Find(variant);
            Assert.Equal(3, row.GetProperty("status").GetInt32());
            Assert.Equal(2, Convert.FromHexString(row.GetProperty("stateAfter").GetString()!)[0x1C]);
            JsonElement[] events = Items(row, "events");
            Assert.Contains(events, e => e.GetProperty("rva").GetUInt32() == 0x723E0);
            Assert.Contains(events, e => e.GetProperty("rva").GetUInt32() == 0xB9070 && Items(e, "list").Length > 0);
            Assert.Contains(events, e => e.GetProperty("rva").GetUInt32() == 0x2EAAE0 && Items(e, "args")[1].GetUInt32() == 8);
            Assert.Contains(events, e => e.GetProperty("rva").GetUInt32() == 0x2EAB40 && Items(e, "args")[1].GetUInt32() == 9);
        }
        foreach (string variant in new[] { "stage0", "stage1", "stage4", "stage2Complete" })
        {
            JsonElement row = Find(variant);
            Assert.Equal(1, row.GetProperty("status").GetInt32());
            Assert.Equal(0, Convert.FromHexString(row.GetProperty("stateAfter").GetString()!)[0x1C]);
            Assert.DoesNotContain(Items(row, "events"), e => e.GetProperty("rva").GetUInt32() == 0x723E0);
            Assert.Equal(5, Items(row, "events").Count(e => e.GetProperty("rva").GetUInt32() == 0x2EE1E0));
        }
        // An out-of-range ID resolves to tile zero, which is valid in this world.
        Assert.True(Find("stage2OutOfRangeHex").GetProperty("pollInputs").GetProperty("validHex").GetBoolean());
    }

    [Fact]
    public void MissingWorldTileDiagnosticDoesNotBecomeACompletedNativeAnswer()
    {
        using JsonDocument document = Vectors();
        JsonElement fault = Items(document.RootElement, "failures").Single();
        Assert.Equal("stage2MissingTile", fault.GetProperty("variant").GetString());
        Assert.False(fault.TryGetProperty("status", out _));
        Assert.False(fault.GetProperty("pollInputs").GetProperty("validHex").GetBoolean());
        Assert.Contains("0x332f95", fault.GetProperty("error").GetString());
        Assert.Contains(Items(fault, "copiedInputChanges"), c => c.GetProperty("role").GetString()!
            .Contains("missing construction tile pointer") && c.GetProperty("after").GetUInt64() == 0);
        Assert.DoesNotContain(Items(fault, "frames"), f => f.GetProperty("rva").GetUInt32() == 0x668BA);
    }
}

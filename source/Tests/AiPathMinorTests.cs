using System.IO.Compression;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiPathMinorTests
{
    [Fact]
    public void OriginalBackendsUseAnActualFrozenWuhuanArmyWithoutInputFieldChanges()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("completed").GetBoolean());
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Equal(root.GetProperty("clockBefore").GetRawText(), root.GetProperty("clockAfter").GetRawText());
        Assert.False(root.GetProperty("gameAccess").GetBoolean());
        Assert.Equal(0, root.GetProperty("gameWrites").GetInt32());
        Assert.Empty(root.GetProperty("failures").EnumerateArray());
        Assert.Empty(root.GetProperty("helperReplacements").EnumerateArray());
        Assert.Empty(root.GetProperty("sourceFieldEdits").EnumerateArray());
        Assert.Empty(root.GetProperty("codeSource").GetProperty("addedTextPageRvas").EnumerateArray());
        Assert.Equal(0, root.GetProperty("codeSource").GetProperty("mutablePagesAdded").GetInt32());
        JsonElement selected = root.GetProperty("preparation").GetProperty("selectedArmy");
        Assert.Equal(46, selected.GetProperty("owner").GetInt32());
        Assert.Equal(1, selected.GetProperty("special").GetInt32());
        Assert.Equal(4, root.GetProperty("actualArmies").EnumerateArray().Count(row => row.GetProperty("owner").GetInt32() == 46));
        Assert.Equal(2, root.GetProperty("cases").GetArrayLength());
        foreach (JsonElement run in root.GetProperty("cases").EnumerateArray())
        {
            Assert.Empty(run.GetProperty("changes").EnumerateArray());
            Assert.Equal(selected.GetProperty("pointer").GetUInt64(), run.GetProperty("armyPointer").GetUInt64());
            Assert.Equal(selected.GetProperty("source").GetInt32(), run.GetProperty("source").GetInt32());
            Assert.Equal(run.GetProperty("source").GetInt32(), Assert.Single(run.GetProperty("goals").EnumerateArray()).GetInt32());
            Assert.Equal(0, run.GetProperty("options").GetInt32());
            JsonElement node = Assert.Single(run.GetProperty("chain").EnumerateArray());
            Assert.Equal(run.GetProperty("source").GetInt32(), node.GetProperty("hex").GetInt32());
            Assert.Equal(0, node.GetProperty("cost").GetInt32());
        }
    }

    [Fact]
    public void CoreMinorGateMatchesTheObservedOriginalBranchSuccessors()
    {
        using JsonDocument document = Vectors();
        JsonElement preparation = document.RootElement.GetProperty("preparation");
        JsonElement[] gates = preparation.GetProperty("gates").EnumerateArray().ToArray();
        Assert.Equal(3, gates.Length);
        foreach (JsonElement gate in gates.Where(g => g.GetProperty("rva").GetInt32() != 0x183B39))
        {
            int rva = gate.GetProperty("rva").GetInt32();
            Assert.Contains(rva, new[] { 0x183B2D, 0x184FAD });
            bool taken = gate.GetProperty("taken").GetBoolean();
            Assert.Equal(rva == 0x184FAD ? taken : !taken, AiPathSearchRules.IsMinorOrSpecialForce(gate.GetProperty("owner").GetInt32()));
            Assert.Equal(rva == 0x184FAD ? 0x184FF9 : 0x183B2F, gate.GetProperty("nextRva").GetInt32());
        }
        JsonElement special = gates.Single(g => g.GetProperty("rva").GetInt32() == 0x183B39);
        Assert.False(special.GetProperty("taken").GetBoolean());
        Assert.Equal(0x183B3B, special.GetProperty("nextRva").GetInt32());
        Assert.Equal(1, special.GetProperty("specialReturn").GetInt32());
        // 51 is the separate immediate comparison in the original anchored code, not a native input in this fixture.
        Assert.Contains(document.RootElement.GetProperty("originalAnchors").EnumerateArray(),
            row => row.GetProperty("rva").GetInt32() == 0x183B22 && row.GetProperty("text").GetString() == "cmp edi, 0x33");
    }

    [Fact]
    public void OnlyTheUnboundedBackendChecksThisMinorArmiesCommanderHome()
    {
        using JsonDocument document = Vectors();
        JsonElement preparation = document.RootElement.GetProperty("preparation");
        JsonElement selected = preparation.GetProperty("selectedArmy");
        JsonElement[] checks = preparation.GetProperty("commanderHomeChecks").EnumerateArray().ToArray();
        foreach (JsonElement run in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            int backend = run.GetProperty("rva").GetInt32();
            bool observed = checks.Any(row => row.GetProperty("backend").GetInt32() == backend);
            Assert.Equal(observed, AiPathSearchRules.ChecksCommanderHome(backend == 0x184EB0,
                selected.GetProperty("owner").GetInt32(), selected.GetProperty("special").GetInt32() != 0));
        }
        Assert.Equal(0x183B42, Assert.Single(checks).GetProperty("rva").GetInt32());
    }

    private static JsonDocument Vectors()
    {
        using FileStream file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "AiPathMinorVectors.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
}

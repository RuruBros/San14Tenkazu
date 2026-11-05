using System.Text.Json;
namespace San14Tenkazu.Tests;

public sealed class AiExtensionTests
{
    [Fact]
    public void DefaultExcludesDirectPlayerAndIncludesDelegatedWithoutActivating()
    {
        Assert.False(AiExtensionSettings.Default.SupplyProtection);
        Assert.Equal(AiExtensionScope.AiAndDelegated, AiExtensionSettings.Default.Scope);
        for (ulong flags = 0; flags <= 7; flags++) Assert.Equal(flags, AiExtensionSettings.FromFlags(flags).Flags);
        Assert.Throws<InvalidDataException>(() => AiExtensionSettings.FromFlags(8));
        Assert.Throws<InvalidDataException>(() => new AiExtensionSettings(true, (AiSupplyFailure)2).Validate());
    }

    [Fact]
    public void SavedConditionsRequireMatchingTreeAndValidFormat()
    {
        string folder = Path.Combine(Path.GetTempPath(), "San14AiTest-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "conditions.json");
        var file = new AiExtensionSettingsFile(path);
        try
        {
            Assert.Equal(AiExtensionSettings.Default, file.Load("treeA"));
            var requested = new AiExtensionSettings(true, AiSupplyFailure.Hold, AiExtensionScope.AiOnly);
            file.Save("treeA", requested);
            Assert.Equal(requested, file.Load("treeA"));
            Assert.Throws<InvalidDataException>(() => file.Load("treeB"));
            File.WriteAllText(path, "{}"); Assert.Throws<InvalidDataException>(() => file.Load("treeA"));
            File.WriteAllText(path, "{broken"); Assert.Throws<InvalidDataException>(() => file.Load("treeA"));
            file.Save("treeA", AiExtensionSettings.Default);
            Assert.False(file.Load("treeA").SupplyProtection);
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void NativeWrapperMatchesExecutedAbiFixture()
    {
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "ai_extension_stub.json")));
        JsonElement input = file.RootElement;
        Assert.Equal(input.GetProperty("hex").GetString(), Convert.ToHexString(AiExtensionStub.Build(input.GetProperty("exe").GetUInt64(), input.GetProperty("data").GetUInt64())));
        Assert.Equal(input.GetProperty("installer").GetString(), Convert.ToHexString(AiExtensionStub.Install(input.GetProperty("data").GetUInt64())));
    }
    [Fact]
    public void EveryEmbeddedModuleAndAllocationAddressFollowsItsNewBase()
    {
        const ulong original = 0x140000000, relocated = 0x7ff737fe0000, data = 0x20000000000, movedData = 0x30000000000;
        byte[] expected = AiExtensionStub.Build(original, data);
        int replacements = 0;
        for (int i = 0; i + 8 <= expected.Length; i++)
        {
            ulong value = BitConverter.ToUInt64(expected, i);
            if (value >= original && value - original < 0x3000000)
            { BitConverter.GetBytes(relocated + value - original).CopyTo(expected, i); replacements++; i += 7; }
            else if (value == data) { BitConverter.GetBytes(movedData).CopyTo(expected, i); replacements++; i += 7; }
        }
        Assert.True(replacements > 10);
        Assert.Equal(expected, AiExtensionStub.Build(relocated, movedData));
    }

    [Fact]
    public void NovemberWrapperChangesOnlyTheAuthenticatedBuildAddresses()
    {
        const ulong exe = 0x140000000, data = 0x20000000000;
        var expected = AiExtensionStub.Build(exe, data);
        var moved = new Dictionary<ulong, ulong>
        {
            [0xB2BC40] = 0xB2E510, [0xB6070] = 0xB7100, [0x32F500] = 0x331780,
            [0x22BAE0] = 0x22D520, [0x233960] = 0x235300, [0x20060] = 0x20040,
            [0x201E0] = 0x201C0, [0x61120] = 0x61C90, [0x304700] = 0x306AF0,
            [0x277DF18] = 0x2780F60,
        };
        int remapped = 0;
        for (int offset = 0; offset + 8 <= expected.Length; offset++)
        {
            ulong address = BitConverter.ToUInt64(expected, offset);
            if (address < exe || !moved.TryGetValue(address - exe, out var replacement)) continue;
            BitConverter.TryWriteBytes(expected.AsSpan(offset), exe + replacement);
            offset += 7;
            remapped++;
        }
        Assert.Equal(12, remapped);
        Assert.Equal(expected, AiExtensionStub.Build(exe, data, AiExtensionStub.November));
    }
}

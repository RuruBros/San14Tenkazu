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
}

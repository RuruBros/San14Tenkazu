using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using San14Tenkazu.Seasons;
using San14Tenkazu.EnvironmentRules;

namespace San14Tenkazu.Tests;

public sealed class GameBuildProfileTests
{
    private static JsonDocument Baseline() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Vectors", "GameBuildProfileBaseline.json")));
    [Fact]
    public void RegistryRecognizesOnlyTheExactSupportedExecutableAndRequiresVerifiedIdentity()
    {
        var current = GameBuildProfiles.Current;
        Assert.Equal("87ED4E46590E0939C9EFCAEF14FA0F892917604DFDA86E0A4BE319FCCFFA2D6A", current.ExeSha256);
        Assert.Same(current, GameBuildProfiles.Find(current.ExeSha256.ToLowerInvariant()));
        foreach (string? hash in new[] { null, "", "1.0.1.0", new string('0', 64), current.ExeSha256[..63] })
            Assert.Null(GameBuildProfiles.Find(hash));
        var identity = new GameIdentity(123, DateTime.UnixEpoch, 0x140000000, current.ExeSha256, true, TestGame.Classes);
        Assert.Same(current, GameBuildProfiles.Require(identity));
        Assert.Throws<InvalidDataException>(() => GameBuildProfiles.Require(identity with { Verified = false }));
        Assert.Throws<InvalidDataException>(() => GameBuildProfiles.Require(identity with { ExeHash = new string('0', 64) }));
    }
    [Fact]
    public void AllMigratedStorageFactsEqualTheIndependentlyCapturedPreRefactorDefinitions()
    {
        using var baseline = Baseline(); int count = 0;
        foreach (var item in baseline.RootElement.GetProperty("facts").EnumerateObject())
        {
            string[] parts = item.Name.Split('.');
            var group = typeof(GameBuildFacts).GetNestedType(parts[0], BindingFlags.NonPublic)!;
            var field = group.GetField(parts[1], BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.Equal(item.Value.GetUInt64(), Convert.ToUInt64(field.GetRawConstantValue())); count++;
        }
        Assert.Equal(131, count);
    }
    [Fact]
    public void NativeCallingConventionsArgumentsAndReturnTypesRemainTheOriginalContracts()
    {
        using var baseline = Baseline(); int count = 0;
        foreach (var item in baseline.RootElement.GetProperty("abi").EnumerateObject())
        {
            var field = typeof(GameBuildFacts.NativeAbi).GetField(item.Name, BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.Equal(item.Value.GetString(), field.GetRawConstantValue()); count++;
        }
        Assert.Equal(8, count);
    }
    [Fact]
    public void NativeGuardAddressesSizesAndHashesRemainExactlyTheOriginalNineTargets()
    {
        using var baseline = Baseline();
        foreach (var (kind, actual) in new[] { ("Season", GameBuildProfiles.Current.SeasonTargets), ("Environment", GameBuildProfiles.Current.EnvironmentTargets) })
        {
            var expected = baseline.RootElement.GetProperty("guards").GetProperty(kind).EnumerateArray().ToArray();
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < actual.Length; i++)
            {
                Assert.Equal(expected[i].GetProperty("rva").GetUInt64(), actual[i].Rva);
                Assert.Equal(expected[i].GetProperty("size").GetInt32(), actual[i].Size);
                Assert.Equal(expected[i].GetProperty("sha256").GetString(), actual[i].Hash);
            }
        }
        Assert.Equal(GameBuildProfiles.Current.EnvironmentTargets.ToArray(), EnvironmentNativeBundle.Targets);
    }
    [Fact]
    public void PatchAndRelocationResourcesRemainByteExactAndTamperingIsRejected()
    {
        using var baseline = Baseline(); var profile = GameBuildProfiles.Current;
        foreach (var item in baseline.RootElement.GetProperty("resources").EnumerateObject())
        {
            byte[] bytes = profile.ReadResource(item.Name);
            Assert.Equal(item.Value.GetString(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            foreach (int offset in new[] { 0, bytes.Length / 2, bytes.Length - 1 })
            {
                byte[] changed = (byte[])bytes.Clone(); changed[offset] ^= 1;
                Assert.Throws<InvalidDataException>(() => profile.VerifyResource(item.Name, changed));
            }
            Assert.Throws<InvalidDataException>(() => profile.VerifyResource(item.Name, bytes[..^1]));
        }
        Assert.Throws<InvalidDataException>(() => profile.ReadResource("allowed_builds.txt"));
    }
    [Fact]
    public void CompiledProfileRejectsMissingOrChangedDefinitionDigest()
    {
        Assert.Throws<InvalidDataException>(() => new GameBuildProfile("BuildProfiles.missing.json", GameBuildFacts.Sha256));
        Assert.Throws<InvalidDataException>(() => new GameBuildProfile("BuildProfiles.current.json", new string('0', 64)));
    }
    [Fact]
    public void NativeManifestMustBindTheSameProfileDigestAndAbiContract()
    {
        var profile = GameBuildProfiles.Current;
        var fields = new Dictionary<string, string> { ["gameProfileId"] = profile.Id, ["gameProfileSha256"] = profile.Sha256, ["nativeContractId"] = profile.NativeContractId };
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(fields))) profile.VerifyNativeManifest(document.RootElement);
        foreach (string key in fields.Keys.ToArray())
        {
            var changed = new Dictionary<string, string>(fields) { [key] = "other-profile" };
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(changed));
            Assert.Throws<InvalidDataException>(() => profile.VerifyNativeManifest(document.RootElement));
            changed.Remove(key);
            using var missing = JsonDocument.Parse(JsonSerializer.Serialize(changed));
            Assert.Throws<InvalidDataException>(() => profile.VerifyNativeManifest(missing.RootElement));
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PackagedNativeIdentityIsCheckedFromThePeWithoutLoadingTheLibrary(bool environment)
    {
        string root = Path.Combine(Path.GetTempPath(), "san14-build-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            string directory = environment ? EnvironmentNativeBundle.Extract(root) : SeasonNativeBundle.Extract(root);
            byte[] file = File.ReadAllBytes(Path.Combine(directory, environment ? "EnvironmentBridge.dll" : "SeasonBridge.dll"));
            var profile = GameBuildProfiles.Current; profile.VerifyNativeBinary(file);
            uint rva = NativeExports.Rva(file, "San14BuildProfileIdentity");
            using var stream = new MemoryStream(file); using var pe = new PEReader(stream);
            var section = pe.PEHeaders.SectionHeaders.Single(s => rva >= s.VirtualAddress && rva - s.VirtualAddress < s.SizeOfRawData);
            int offset = checked(section.PointerToRawData + (int)rva - section.VirtualAddress);
            file[offset] ^= 1;
            Assert.Throws<InvalidDataException>(() => profile.VerifyNativeBinary(file));
            file[offset] ^= 1;
            int name = file.AsSpan().IndexOf(Encoding.ASCII.GetBytes("San14BuildProfileIdentity\0"));
            Assert.True(name >= 0); file[name] ^= 1;
            Assert.Throws<InvalidDataException>(() => profile.VerifyNativeBinary(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

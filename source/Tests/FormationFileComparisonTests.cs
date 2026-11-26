using System.Buffers.Binary;
using Xunit;

namespace San14Tenkazu.Tests;

public class FormationFileComparisonTests
{
    private static byte[] Table()
    {
        var body = new byte[8 + 21 * 152 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(body, 11);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 21);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(body.Length - 8), 12);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(body.Length - 4), 201);
        return body;
    }

    [Fact]
    public void LocatorRequiresExactlyOneCompleteTableAndFollowsRelocation()
    {
        var table = Table();
        table[8 + 14 * 152 + 0x6f] = 20;
        Assert.Equal(20, FormationFileData.ReadRows([1, 2, 3, .. table])[13].Values["mobility"]);
        Assert.Throws<InvalidDataException>(() => FormationFileData.ReadRows(table[..^1]));
        Assert.Throws<InvalidDataException>(() => FormationFileData.ReadRows([.. table, .. table]));
    }

    [Fact]
    public void ChangesAtTheSameFieldAndReusedRowIdsRemainVisible()
    {
        var body = Table();
        var original = new OfficialFormation(1, "old", FormationFileData.ReadRows(body));
        body[8 + 152 + 0x73] = 15;
        var target = new OfficialFormation(2, "new", FormationFileData.ReadRows(body));
        body[8 + 152 + 0x73] = 18;
        body[8 + 2 * 152 + 0x6e] = 1;
        var file = new FormationFileData("old", "", body.Length, true, FormationFileData.ReadRows(body));
        var result = FormationFileComparison.Compare(original, target, file, [original, target]);
        Assert.Null(result.OfficialFile);
        Assert.Equal(100, result.Fields.Length);
        Assert.Equal(new FormationFileField(1, "attack", 0, 15, 18, false), Assert.Single(result.Fields, f => f.Base != f.Target));
        Assert.Equal(5, result.Fields.Count(f => f.IdentityChanged && f.Id == 2));
        Assert.Equal(1ul, FormationFileComparison.Compare(original, target, file with { Decoded = false }, [original, target]).OfficialFile);
    }

    [Fact]
    public void InvalidContainerCannotReserveAnUnboundedDecodedBuffer()
    {
        string path = Path.GetTempFileName();
        try
        {
            var bytes = new byte[754 + LwcCodec.HeaderSize];
            bytes[0] = 0x66;
            "SN14SCEXVER0001"u8.CopyTo(bytes.AsSpan(4));
            LwcCodec.Magic.CopyTo(bytes.AsSpan(754));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(758), int.MaxValue);
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => FormationFileData.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NewDepotReferenceRequiresMatchingManifestIdentitySizeAndContent()
    {
        static byte[] Section(uint magic, byte[] payload) => [.. BitConverter.GetBytes(magic), .. BitConverter.GetBytes(payload.Length), .. payload];
        var name = "0010_KO\\fixdataexce.s14"u8.ToArray();
        var hash = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        byte[] record = [10, (byte)name.Length, .. name, 16, 12, 42, 20, .. hash];
        byte[] payload = Section(0x71f617d0, [10, (byte)record.Length, .. record]);
        byte[] metadata = Section(0x1f4812be, [8, 0xd0, 0x94, 0x93, 2, 16, 42, 32, 0]);
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [.. payload, .. metadata]);
            var entry = FormationDepotManifest.Read(path, 42);
            var file = new FormationFileData("", Convert.ToHexString(hash), 12, false, []);
            Assert.True(entry.Matches(file));
            Assert.False(entry.Matches(file with { Sha1 = new string('0', 40) }));
            Assert.False(entry.Matches(file with { Length = 13 }));
            Assert.False(entry.Matches(file with { Decoded = true }));
            Assert.Throws<InvalidDataException>(() => FormationDepotManifest.Read(path, 43));
            File.WriteAllBytes(path, [.. payload, .. metadata[..^1]]);
            Assert.Throws<InvalidDataException>(() => FormationDepotManifest.Read(path, 42));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ScalarImportUsesVerifiedIdentityOfEachEditedFormation()
    {
        var fixture = new FormationFixture();
        var source = fixture.Snapshot().Baseline(GameBuildFacts.ExeSha256);
        var key = new RuleKey("formation", 1, "attack");
        var change = source.WithChanges([new(key, 10, 20)]);
        fixture.Set(fixture.Array + 2 * 0xc8 + 0x7e, [9]);
        var target = fixture.Snapshot().Baseline(source.Build);
        Assert.Equal(20, SettingsMigration.Rules(change, target).Desired(key));
        var second = new RuleKey("formation", 2, "defense");
        var partial = SettingsMigration.PlanRules(change.WithChanges([new(second, 10, 19)]), target);
        Assert.Equal(second, Assert.Single(partial.Unavailable));
        Assert.Throws<InvalidDataException>(() => partial.Resolve());
        var accepted = partial.Resolve(new HashSet<RuleKey>());
        Assert.Equal(20, accepted.Desired(key));
        Assert.Equal(10, accepted.Desired(second));
        Assert.Throws<InvalidDataException>(() => SettingsMigration.Rules(change with { FormationIdentities = null }, target));
        Assert.Throws<InvalidDataException>(() => (target with { FormationIdentities = source.FormationIdentities }).Validate());
        fixture.Set(fixture.Array + 0xc8 + 0x10, [0, 0]);
        Assert.Throws<InvalidDataException>(() => SettingsMigration.Rules(change, fixture.Snapshot().Baseline(source.Build)));
        var legacy = source with { FormationIdentities = null };
        Assert.True(source.SameAs(new FormationFixture().Snapshot().WithIdentities(legacy)));
    }
}

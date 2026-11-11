using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class LinkedProfileEditTests
{
    private static GameDataProfile Profile => GameBuildProfiles.FindData(GameSessionTests.UpdateHash)!;

    private static GameSnapshot Game()
    {
        var game = new TestGame().Force(1, 1, 1).Force(2, 2, 2)
            .City(1, 1, 10).City(2, 2, 2).City(3, 1).Gate(52, 1, 13)
            .Officer(1, "군주1", 1, 1, 1).Officer(2, "군주2", 2, 2, 1)
            .Officer(10, "태수", 1, 1, 3, office: 5).Officer(11, "일반", 1, 1, 4)
            .Officer(12, "정책", 1, 1, 4).Officer(13, "관문태수", 1, 52, 3)
            .Officer(20, "재야", 0, 1, 5).Officer(21, "포로", 0, 1, 6)
            .Strategist(1, 10).Policy(1, 0, (11, 10), (10, 7), (12, 3));
        foreach (int id in new[] { 1, 2, 10, 11, 12, 13, 20, 21 })
            game.With(id, OfficerFields.Ideology, 1).With(id, OfficerFields.ServiceValue, 0xABCD)
                .With(id, OfficerFields.Merit, 12345);
        for (int city = 1; city <= GameMemory.CityCount; city++) game.Castle(city, 2000);
        var data = game.Build();
        foreach (int city in data.CityIds) CityFields.Scale.Encode(1).CopyTo(data.Cities[city].Data, CityFields.Scale.Offset);
        return data;
    }

    private static void RoundTrip(GameSnapshot data, EditPlan plan)
    {
        Assert.False(plan.Refused, plan.Refusal);
        Assert.NotEmpty(plan.Edits);
        var fields = plan.Edits.SelectMany(edit => edit.Changes).Select(change => change.Field);
        Assert.Null(ProfiledEdits.Block(Profile, fields));
        Assert.Null(GameChecks.WriteBlock(false, true, GameChecks.Run(data, GameCatalog.Empty), ProfiledEdits.CheckedGroups(fields)));
        Assert.Empty(GameChecks.Broken(data, plan.Edits, GameCatalog.Empty));
        ProfiledEdits.RequireFields(Profile, plan.Edits);
        ProfiledEdits.RequireCurrentRecords(data, plan.Edits);
        var memory = new FakeMemory(data);
        GameMemory.Apply(memory, TestGame.Classes, plan.Edits);
        foreach (var edit in plan.Edits)
        {
            var expected = (byte[])edit.Expected.Clone();
            foreach (var change in edit.Changes) change.Field.Encode(change.Value).CopyTo(expected, change.Field.Offset);
            Assert.Equal(expected, memory[edit.Address]);
            if (edit.Kind == RecordKind.Officer)
                Assert.Equal(OfficerFields.Merit.Read(edit.Expected), OfficerFields.Merit.Read(memory[edit.Address]!));
        }
        var undo = Undo.Of(plan.Edits);
        ProfiledEdits.RequireFields(Profile, undo);
        ProfiledEdits.RequireCurrentRecords(data.With(plan.Edits), undo);
        GameMemory.Apply(memory, TestGame.Classes, undo);
        foreach (var edit in plan.Edits) Assert.Equal(edit.Expected, memory[edit.Address]);
    }

    [Theory]
    [InlineData(10, (int)Transfer.Move, 3)]
    [InlineData(10, (int)Transfer.Defect, 2)]
    [InlineData(10, (int)Transfer.Dismiss, 0)]
    [InlineData(11, (int)Transfer.Defect, 2)]
    [InlineData(13, (int)Transfer.Move, 3)]
    [InlineData(20, (int)Transfer.Recruit, 2)]
    [InlineData(21, (int)Transfer.Recruit, 2)]
    [InlineData(21, (int)Transfer.Release, 0)]
    public void TransfersKeepMeritAndRestoreAllLinkedRecords(int id, int kind, int home)
    {
        var transfer = (Transfer)kind;
        var data = Game();
        var officer = data.OfficerById[id];
        Assert.False(Transfers.RequiresNative(data, officer, transfer));
        RoundTrip(data, Transfers.Plan(data, officer, transfer, home));
    }

    [Theory]
    [InlineData(0)] [InlineData(100)] [InlineData(255)]
    public void FriendshipWritesBothForcesAndUndoesBoth(int value)
    {
        var data = Game();
        data = data.With(ForceEdits.Plan(data, 1, [], [(2, 99)]).Edits);
        var plan = ForceEdits.Plan(data, 1, [], [(2, value)]);
        Assert.Equal(2, plan.Edits.Count);
        Assert.Contains(plan.Edits[0].Changes, c => c.Field == ForceFields.Friendship(2) && c.Value == value);
        Assert.Contains(plan.Edits[1].Changes, c => c.Field == ForceFields.Friendship(1) && c.Value == value);
        RoundTrip(data, plan);
    }

    [Theory]
    [InlineData(0)] [InlineData(7)]
    public void RelationshipSlotsWriteOnlyTheSelectedOfficer(int slot)
    {
        var data = Game();
        var plan = OfficerEdits.Plan(data, 11, new([new(OfficerFields.Dislikes[slot], 2), new(OfficerFields.Likes[slot], 1)], null, null, []));
        Assert.Single(plan.Edits);
        RoundTrip(data, plan);
        var stale = data.With(plan.Edits);
        Assert.Throws<StaleEditException>(() => ProfiledEdits.RequireCurrentRecords(stale, plan.Edits));
        Assert.NotNull(ProfiledEdits.Block(Profile, [OfficerFields.Gender with { Offset = 0x1EF }]));
    }

    [Fact]
    public void RegisteredOffsetsWidthsAndCodePinsMatchNativeSerializerReplay()
    {
        using var proof = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Vectors", "GameLinkedEditSerializers20261111.json")));
        var root = proof.RootElement;
        Assert.False(root.GetProperty("gameAccess").GetBoolean());
        FieldDef[] fields = [CityFields.Governor, CityFields.GateGovernor,
            OfficerFields.CorpsField, OfficerFields.BaseField, OfficerFields.PlaceField, OfficerFields.StatusField,
            OfficerFields.Office, OfficerFields.FormerLord, OfficerFields.FormerLordMonths, OfficerFields.PrisonerOf,
            OfficerFields.Captivity, OfficerFields.JoinedYear, OfficerFields.Service, OfficerFields.ServiceValue,
            .. OfficerFields.Likes, .. OfficerFields.Dislikes, ForceFields.Strategist,
            .. Enumerable.Range(1, 51).Select(ForceFields.Friendship),
            .. Enumerable.Range(0, 5).SelectMany(field => Enumerable.Range(0, 7)
                .SelectMany(slot => new[] { ForceFields.PolicyOfficer(field, slot), ForceFields.PolicyDays(field, slot) }))];
        foreach (var field in fields)
        {
            Assert.True(Profile.Supports(field));
            string kind = field.Record == RecordKind.Officer ? "officer" : field.Record == RecordKind.Force ? "force" : "base";
            foreach (string version in new[] { "legacy", "november" })
                Assert.Equal(field.Size, root.GetProperty("versions").GetProperty(version).GetProperty(kind)
                    .GetProperty("fields").GetProperty($"0x{field.Offset:x}").GetInt32());
            // The complete editor vector separately verifies newly added serializer dependencies.
            foreach (var guard in new[] { Profile.Fields[field] })
            {
                var native = root.GetProperty("guards").EnumerateArray().Single(g => g.GetProperty("rva").GetString() == $"0x{guard.Rva:x}");
                Assert.Equal(guard.Length, native.GetProperty("length").GetInt32());
                Assert.Equal(guard.Sha256, native.GetProperty("sha256").GetString());
            }
        }
        Assert.Equal(2, OfficerFields.ServiceValue.Size);
        Assert.Equal(OfficerFields.Merit.Offset, OfficerFields.ServiceValue.Offset + OfficerFields.ServiceValue.Size);
    }

    [Fact]
    public void ArrayEditsRequireBothParentAndNestedCodeAndRejectChangedBytes()
    {
        FieldDef[] fields = [ForceFields.Friendship(2), ForceFields.PolicyOfficer(0, 0)];
        foreach (var field in fields) Assert.Equal(2, Profile.Fields[field].RequiredCode.Count());
        var code = new Dictionary<ulong, byte[]> { [0x100] = [1, 2], [0x200] = [3, 4] };
        GameDataGuard Guard(ulong rva) => new(rva, code[rva].Length, Convert.ToHexString(SHA256.HashData(code[rva])));
        var guard = Guard(0x100) with { Dependencies = [Guard(0x200)] };
        var profile = Profile with { Fields = fields.ToImmutableDictionary(field => field, _ => guard) };
        ProfiledEdits.RequireCode(profile, fields, g => code[g.Rva]);
        foreach (ulong rva in code.Keys)
        {
            code[rva][0] ^= 1;
            Assert.Throws<InvalidDataException>(() => ProfiledEdits.RequireCode(profile, fields, g => code[g.Rva]));
            code[rva][0] ^= 1;
        }
    }
}

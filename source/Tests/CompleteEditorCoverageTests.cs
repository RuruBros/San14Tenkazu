using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class CompleteEditorCoverageTests
{
    private static GameDataProfile Profile => GameBuildProfiles.FindData(GameSessionTests.UpdateHash)!;
    private static JsonDocument Native() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
        "Vectors", "GameEditorCoverage20261111.json")));

    [Fact]
    public void EveryEditorFieldHasAdmissionAndBothNativeWidthsAndAllCodePins()
    {
        using var proof = Native();
        Assert.False(proof.RootElement.GetProperty("gameAccess").GetBoolean());
        foreach (var field in EditorCoverageFixture.All)
        {
            if (!EditorCoverageFixture.Implicit.Contains(field))
                Assert.True(Profile.Supports(field), $"Unregistered {field.Record} {field.Label} +{field.Offset:X}");
            if (field.Record == RecordKind.PlayRecord)
            {
                Assert.Contains(field, new[] { PlayRecordFields.Unlocks1, PlayRecordFields.Unlocks2, PlayRecordFields.Changed });
                Assert.Equal(field == PlayRecordFields.Changed ? 1 : 4, field.Size);
            }
            else
            {
                string kind = field.Record switch { RecordKind.Officer => "officer", RecordKind.Force => "force", _ => field.Record.ToString() };
                foreach (var version in proof.RootElement.GetProperty("records").GetProperty(kind).EnumerateArray())
                    Assert.Equal(field.Size, version.GetProperty("fields").GetProperty($"0x{field.Offset:x}").GetInt32());
            }
        }
        foreach (var guard in Profile.Fields.Values.SelectMany(g => g.RequiredCode).DistinctBy(g => g.Rva))
        {
            var native = proof.RootElement.GetProperty("guards").EnumerateArray()
                .Single(g => g.GetProperty("rva").GetString() == $"0x{guard.Rva:x}");
            Assert.Equal(guard.Length, native.GetProperty("length").GetInt32());
            Assert.Equal(guard.Sha256, native.GetProperty("sha256").GetString());
        }
        Assert.NotNull(ProfiledEdits.Block(Profile, [OfficerFields.Gender with { Offset = 0x1EF }]));
    }

    public static IEnumerable<object[]> OrdinaryFields() => EditorCoverageFixture.All.Select((field, index) => (field, index))
        .Where(pair => !EditorCoverageFixture.Implicit.Contains(pair.field) &&
            pair.field.Record is RecordKind.City or RecordKind.Gate or RecordKind.Officer or RecordKind.Unit
                or RecordKind.Item or RecordKind.Personality or RecordKind.Force)
        .Where(pair => pair.field.Record != RecordKind.Item || pair.field == ItemFields.Ability || pair.field == ItemFields.Bonus)
        .Where(pair => pair.field.Record != RecordKind.Force ||
            new[] { ForceFields.Country, ForceFields.Color, ForceFields.Rank, ForceFields.Strategist }.Contains(pair.field))
        .Select(pair => new object[] { pair.index, pair.field.Label });

    // Include all ordinary direct fields; dynamic friendship/alliance/policy batches
    // retain their separate bilateral/packing/native regression tests.
    [Theory]
    [MemberData(nameof(OrdinaryFields))]
    public void ActualPlansWriteAndUndoPreservingNeighborBytes(int index, string label)
    {
        var field = EditorCoverageFixture.All[index];
        var data = EditorCoverageFixture.Snapshot();
        EditPlan plan;
        long Next(byte[] bytes) => field == UnitFields.Ailments[UnitFields.Provoked] ? field.Read(bytes) - 1
            : field == OfficerFields.Office ? 1 : field == OfficerFields.Formations ? 2
            : field == PersonalityFields.Condition ? 11 : field == PersonalityFields.Filter ? PersonalityRules.FilterNoShipsOrSiege
            : field.Read(bytes) + 1;
        if (field.Record == RecordKind.Officer)
        {
            var officer = data.OfficerById[5];
            var request = field == OfficerFields.Spouse ? new OfficerEdits.Request([], 6, null, [])
                : field == OfficerFields.Sworn ? new OfficerEdits.Request([], null, 6, [])
                : new OfficerEdits.Request([new(field, Next(officer.Data))], null, null, []);
            plan = OfficerEdits.Plan(data, 5, request);
        }
        else if (field.Record == RecordKind.City)
            plan = CityEdits.Plan(data, 1, new([new(field, Next(data.Cities[1].Data))], [], null));
        else if (field.Record == RecordKind.Gate)
            plan = GateEdits.Plan(data, 52, new([new(field, Next(data.Gates[0].Data))], null));
        else if (field.Record == RecordKind.Unit)
            plan = UnitEdits.Plan(data, 0, 5, [new(field, Next(data.Units[0].Data))]);
        else if (field.Record == RecordKind.Item)
            plan = ItemEdits.Plan(data, 1, [new(field, Next(data.Items[0].Data))]);
        else if (field.Record == RecordKind.Personality)
            plan = PersonalityEdits.Plan(data, 1, [new(field, Next(data.Personalities[0].Data))]);
        else if (field.Record == RecordKind.Force)
            plan = ForceEdits.Plan(data, 1, [new(field, Next(data.ForceRecords[1].Data))], []);
        else throw new InvalidOperationException("Uncovered direct editor field: " + label);
        Assert.False(plan.Refused, label + ": " + plan.Refusal);
        Assert.NotEmpty(plan.Edits);
        RoundTrip(data, plan);
    }

    private static void RoundTrip(GameSnapshot data, EditPlan plan)
    {
        ProfiledEdits.RequireFields(Profile, plan.Edits);
        ProfiledEdits.RequireCurrentRecords(data, plan.Edits);
        var memory = new FakeMemory(data);
        GameMemory.Apply(memory, TestGame.Classes, plan.Edits);
        foreach (var edit in plan.Edits)
        {
            var expected = (byte[])edit.Expected.Clone();
            foreach (var change in edit.Changes) change.Field.Encode(change.Value).CopyTo(expected, change.Field.Offset);
            foreach (var change in edit.Kind == RecordKind.Officer ? OfficerEdits.SlotStateCleared(edit.Expected, edit.Changes) : [])
                change.Field.Encode(change.Value).CopyTo(expected, change.Field.Offset);
            Assert.Equal(expected, memory[edit.Address]);
        }
        var undo = Undo.Of(plan.Edits);
        ProfiledEdits.RequireFields(Profile, undo);
        ProfiledEdits.RequireCurrentRecords(data.With(plan.Edits), undo);
        GameMemory.Apply(memory, TestGame.Classes, undo);
        foreach (var edit in plan.Edits) Assert.Equal(edit.Expected, memory[edit.Address]);
    }

    [Fact]
    public void MilitaryBalanceAndRankUseTheirGuardAndUndoWholeBatch()
    {
        var data = EditorCoverageFixture.Snapshot();
        var plan = ForceEdits.Plan(data, 1, [new(ForceFields.Rank, 4)], [], [], [new(1, 1, 1, 100000)]);
        Assert.False(plan.Refused, plan.Refusal); RoundTrip(data, plan);
        var edit = plan.Edits.Single(e => e.Kind == RecordKind.Corps);
        Assert.Throws<InvalidDataException>(() => ProfiledEdits.RequireFields(Profile, [edit with { MilitaryPoints = null }]));
        using var proof = Native();
        Assert.Equal($"0x{Profile.MilitaryCapsRva:x}", proof.RootElement.GetProperty("military")[1].GetProperty("capsRva").GetString());
        Assert.Equal($"0x{Profile.PlayRecordRva:x}", proof.RootElement.GetProperty("play")[1].GetProperty("recordRva").GetString());
    }

    [Fact]
    public void AncientUnlockMatchesBothNativeMappingsAndKeepsOtherBytes()
    {
        using var proof = Native();
        foreach (var version in proof.RootElement.GetProperty("play").EnumerateArray())
            foreach (var mapping in version.GetProperty("officerBits").EnumerateObject())
                Assert.Equal(AncientOfficers.Bit(int.Parse(mapping.Name)) ?? 0, mapping.Value.GetInt32());
        var data = EditorCoverageFixture.Snapshot();
        var plan = AncientOfficers.Unlock(data, Enumerable.Range(AncientOfficers.First, AncientOfficers.Last - AncientOfficers.First + 1));
        Assert.False(plan.Refused); ProfiledEdits.RequireFields(Profile, plan.Edits);
        ProfiledEdits.RequireCurrentRecords(data, plan.Edits);
        var memory = new FakeMemory(data);
        GameMemory.Apply(memory, TestGame.Classes, plan.Edits);
        Assert.Equal(1, PlayRecordFields.Changed.Read(memory[data.Play!.Address]!));
        var undo = Undo.Of(plan.Edits);
        GameMemory.Apply(memory, TestGame.Classes, undo);
        var expected = (byte[])data.Play.Data.Clone(); expected[PlayRecordFields.Changed.Offset] = 1;
        Assert.Equal(expected, memory[data.Play.Address]);
    }

    [Fact]
    public void PersonalityTargetChangesRestartOnlyCurrentHoldersAndRollbackTogether()
    {
        var data = EditorCoverageFixture.Snapshot();
        var holder = data.OfficerById[5];
        EditorCoverageFixture.Put(holder.Data, OfficerFields.Personalities[0], 1);
        EditorCoverageFixture.Put(holder.Data, OfficerFields.PersonalitiesInEffect, 1);
        var plan = PersonalityEdits.Plan(data, 1, [new(PersonalityFields.Condition, 11)]);
        Assert.False(plan.Refused, plan.Refusal);
        Assert.Single(plan.Edits.Single().Restarts!);
        ProfiledEdits.RequireFields(Profile, plan.Edits); ProfiledEdits.RequireCurrentRecords(data, plan.Edits);
        var memory = new FakeMemory(data);
        GameMemory.Apply(memory, TestGame.Classes, plan.Edits);
        Assert.Equal(0, OfficerFields.PersonalitiesInEffect.Read(memory[holder.Address]!));
        Assert.Equal(data.OfficerById[6].Data, memory[data.OfficerById[6].Address]);
        var stale = data.With([new(RecordKind.Officer, holder.Address, holder.Data, holder.Name, [new(OfficerFields.Personalities[0], 2)])]);
        Assert.Throws<InvalidOperationException>(() => ProfiledEdits.RequireCurrentRecords(stale, plan.Edits));
        var fail = new FailSecondWrite(data);
        Assert.ThrowsAny<Exception>(() => GameMemory.Apply(fail, TestGame.Classes, plan.Edits));
        Assert.Equal(data.Personalities[0].Data, fail.Memory[data.Personalities[0].Address]);
        Assert.Equal(holder.Data, fail.Memory[holder.Address]);
    }

    private sealed class FailSecondWrite(GameSnapshot data) : IRecordMemory
    {
        internal readonly FakeMemory Memory = new(data);
        private int writes;
        public byte[] Read(ulong address, int length) => Memory.Read(address, length);
        public void Write(ulong address, byte[] bytes)
        {
            Memory.Write(address, bytes);
            if (++writes == 2) throw new IOException("Injected failure after partial linked write");
        }
    }
}

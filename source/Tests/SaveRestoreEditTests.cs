using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class SaveRestoreEditTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Folder = Path.Combine(Path.GetTempPath(), "san14-trait-intent-" + Guid.NewGuid().ToString("N"));
        internal string PathName => Path.Combine(Folder, "edits.json");
        internal readonly GameIdentity Identity = new(123, DateTime.UnixEpoch, 0x140000000, "fixture", true, TestGame.Classes);
        internal readonly RuntimeRuleSettings Settings = new(true, AbilityRuleMode.Cap200);
        internal readonly GameSnapshot Data;
        internal readonly FakeMemory Memory;
        internal readonly OfficerRecord Officer;
        internal readonly GameLayout Layout;
        internal readonly SaveOfficer Saved;
        internal Fixture()
        {
            var builder = new TestGame().Officer(2, "edited", 0, 1, 5).With(2, OfficerFields.Leadership, 150);
            for (int i = 0; i < 6; i++) builder.With(2, OfficerFields.Personalities[i], i + 1);
            Data = builder.Build(Settings); Officer = Data.OfficerById[2]; Memory = new(Data);
            Layout = new(new(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0),0,0,0,Officer.Address - GameMemory.OfficerStride,2,0,0,0,[],[],0,0,0x1000);
            byte[] stream = new byte[GameSaveFile.RecordSize];
            foreach (var (at, offset, length) in GameSaveFile.Segments) Officer.Data.AsSpan(offset, length).CopyTo(stream.AsSpan(at));
            Saved = new(1, 2, Officer.Data.AsSpan(SaveRestore.AbilityOffset, 5).ToArray(), new int[5], [1,2,3,4,5,6,0,0,0], stream);
        }
        internal SaveRestoreEdits Open() => new(PathName, Identity, Layout);
        internal List<RecordEdit> Clear()
        {
            var plan = OfficerEdits.Plan(Data, 2, new(OfficerFields.Personalities.Select(f => new FieldChange(f, 0)).ToList(), null, null, []));
            Assert.False(plan.Refused);
            Apply(plan.Edits); return plan.Edits.ToList();
        }
        internal void Apply(IReadOnlyList<RecordEdit> edits)
        {
            var history = Open();
            GameMemory.Apply(Memory, TestGame.Classes, edits, () => history.Record(Memory, edits));
        }
        internal RuntimeRuleWrite[] Recovery(SaveRestoreEdits? history = null)
        {
            history ??= Open(); history.Observe(address => Memory[address]);
            byte[] record = Memory[Officer.Address]!;
            return SaveRestore.Plan(record, Officer.Address, Saved, Settings, history.PreservesTraits(Officer.Address, record));
        }
        public void Dispose() { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); }
    }

    [Fact]
    public void VerifiedDeletionSurvivesRepeatedChecksAndNewJournalInstances()
    {
        using var f = new Fixture(); f.Clear();
        for (int i = 0; i < 4; i++) Assert.Empty(f.Recovery());
        Assert.All(OfficerFields.Personalities, field => Assert.Equal(0, field.Read(f.Memory[f.Officer.Address]!)));
    }

    [Fact]
    public void DeletionDoesNotDisableAnotherOfficerOrAbilityRecovery()
    {
        using var f = new Fixture(); f.Clear();
        f.Memory.Set(f.Officer.Address, OfficerFields.Leadership, 100);
        var ability = Assert.Single(f.Recovery()); Assert.False(SaveRestore.IsTraitRestore(ability)); Assert.Equal((byte)150, ability.After[0]);
        byte[] other = f.Memory[f.Officer.Address]!;
        Assert.Single(SaveRestore.Plan(other, f.Officer.Address + GameMemory.OfficerStride, f.Saved, f.Settings,
            f.Open().PreservesTraits(f.Officer.Address + GameMemory.OfficerStride, other)), SaveRestore.IsTraitRestore);
    }

    [Fact]
    public void UndoRetiresDeletionSoALaterLoadCanRecoverTraits()
    {
        using var f = new Fixture(); var edits = f.Clear();
        f.Apply(Undo.Of(edits));
        Assert.False(f.Open().PreservesTraits(f.Officer.Address, f.Memory[f.Officer.Address]!));
        foreach (var field in OfficerFields.Personalities) f.Memory.Set(f.Officer.Address, field, 0);
        Assert.Single(f.Recovery(), SaveRestore.IsTraitRestore);
        f.Apply(edits); Assert.Empty(f.Recovery()); // an explicit redo creates fresh evidence
    }

    [Theory]
    [InlineData("pid")] [InlineData("start")] [InlineData("module")] [InlineData("build")]
    [InlineData("array")] [InlineData("count")] [InlineData("state")]
    public void EvidenceDoesNotCrossProcessOrOfficerStorage(string change)
    {
        using var f = new Fixture(); f.Clear();
        var identity = change switch { "pid" => f.Identity with { ProcessId = 124 }, "start" => f.Identity with { StartTimeUtc = DateTime.UnixEpoch.AddSeconds(1) },
            "module" => f.Identity with { ModuleBase = 0x150000000 }, "build" => f.Identity with { ExeHash = "other" }, _ => f.Identity };
        var layout = change switch { "array" => f.Layout with { OfficerArray = f.Layout.OfficerArray + 0x10000 },
            "count" => f.Layout with { OfficerCount = 3 }, "state" => f.Layout with { State = 0x2000 }, _ => f.Layout };
        var history = new SaveRestoreEdits(f.PathName, identity, layout);
        Assert.False(history.PreservesTraits(f.Officer.Address, f.Memory[f.Officer.Address]!));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ObservingDifferentTraitsOrOtherStateRetiresEvidencePermanently(bool traits)
    {
        using var f = new Fixture(); f.Clear();
        FieldDef field = traits ? OfficerFields.Personalities[0] : OfficerFields.Loyalty;
        long before = field.Read(f.Memory[f.Officer.Address]!);
        f.Memory.Set(f.Officer.Address, field, before + 1);
        f.Open().Observe(address => f.Memory[address]);
        f.Memory.Set(f.Officer.Address, field, before);
        Assert.Single(f.Recovery(), SaveRestore.IsTraitRestore);
    }

    [Fact]
    public void FurtherSuccessfulOfficerEditUpdatesTheDeletionEvidence()
    {
        using var f = new Fixture(); f.Clear();
        var edit = new RecordEdit(RecordKind.Officer, f.Officer.Address, f.Memory[f.Officer.Address]!, "edited", [new(OfficerFields.Loyalty, 100)]);
        f.Apply([edit]);
        Assert.True(f.Open().PreservesTraits(f.Officer.Address, f.Memory[f.Officer.Address]!));
        Assert.False(f.Open().PreservesTraits(f.Officer.Address, f.Officer.Data));
    }

    [Fact]
    public void GrowthModeExperienceConversionsDoNotRetireTraitDeletion()
    {
        using var f = new Fixture(); f.Clear();
        var experience = OfficerFields.ExperienceOf(OfficerFields.Leadership);
        long before = experience.Read(f.Memory[f.Officer.Address]!);
        f.Memory.Set(f.Officer.Address, experience, AbilityProgress.Threshold(150, false, true));
        f.Open().Observe(address => f.Memory[address]);
        Assert.True(f.Open().PreservesTraits(f.Officer.Address, f.Memory[f.Officer.Address]!));
        f.Memory.Set(f.Officer.Address, experience, before);
        Assert.Empty(f.Recovery());
    }

    [Fact]
    public void FailureToPersistEvidenceRollsBackTheUserEdit()
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.PathName);
        byte[] before = f.Memory[f.Officer.Address]!;
        var error = Record.Exception(() => f.Clear());
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal(before, f.Memory[f.Officer.Address]);
        Assert.False(File.Exists(f.PathName));
    }

    [Fact]
    public void FailedBatchNeverRecordsDeletion()
    {
        using var f = new Fixture(); var history = f.Open();
        var deletion = OfficerEdits.Plan(f.Data, 2, new(OfficerFields.Personalities.Select(field => new FieldChange(field, 0)).ToList(), null, null, []));
        var missing = new RecordEdit(RecordKind.Officer, 0x999999, f.Officer.Data, "missing", [new(OfficerFields.Loyalty, 100)]);
        var edits = deletion.Edits.Concat([missing]).ToArray();
        Assert.ThrowsAny<Exception>(() => GameMemory.Apply(f.Memory, TestGame.Classes, edits, () => history.Record(f.Memory, edits)));
        Assert.Equal(f.Officer.Data, f.Memory[f.Officer.Address]); Assert.False(File.Exists(f.PathName));
    }

    [Fact]
    public void CorruptOrOutOfRangeReceiptsAreRejected()
    {
        using var f = new Fixture(); f.Clear();
        File.WriteAllText(f.PathName, "{}"); Assert.Throws<InvalidDataException>(() => f.Open());
        File.WriteAllText(f.PathName, JsonSerializer.Serialize(new SaveRestoreEdits.Receipt(1, SaveRestoreEdits.Scope.For(f.Identity, f.Layout),
            [new(f.Officer.Address + 1, f.Memory[f.Officer.Address]!)])));
        Assert.Throws<InvalidDataException>(() => f.Open());
    }
}

using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class RuleProfileTests
{
    [Fact]
    public void IndexedLookupsFollowTableAdditionsEditsRestoresAndSerialization()
    {
        var fixture = new TacticFixture();
        RuleProfile original = fixture.Formations.Snapshot().Baseline(fixture.Identity.ExeHash);
        RuleKey formation = FormationRules.Key(1, FormationRules.Fields[0]);
        Assert.Equal(10, original.Default(formation));
        Assert.Equal(10, original.Desired(formation));
        RuleProfile extended = original.AddTactics(fixture.Snapshot());
        foreach (RuleDefault entry in extended.Defaults) Assert.Equal(entry.Value, extended.Desired(entry.Key));
        RuleKey tactic = TacticRules.Key(1, TacticRules.Fields.Single(f => f.Id == "amount1"));
        RuleProfile edited = extended.WithChanges([new(formation, 10, 16), new(tactic, extended.Default(tactic), 20)]);
        Assert.Equal(16, edited.Desired(formation));
        Assert.Equal(20, edited.Desired(tactic));
        Assert.Equal(10, original.Desired(formation));
        Assert.Equal(extended.Default(tactic), extended.Desired(tactic));
        RuleProfile restored = edited.WithChanges([new(tactic, 20, edited.Default(tactic))], restoreDefaults: true);
        Assert.Equal(16, restored.Desired(formation));
        Assert.Equal(extended.Default(tactic), restored.Desired(tactic));
        Assert.Equal(20, edited.Desired(tactic));
        string before = JsonSerializer.Serialize(edited);
        Parallel.For(0, 1000, _ =>
        {
            Assert.Equal(10, edited.Default(formation));
            Assert.Equal(16, edited.Desired(formation));
            Assert.Equal(20, edited.Desired(tactic));
        });
        Assert.Equal(before, JsonSerializer.Serialize(edited));
        RuleProfile roundTrip = JsonSerializer.Deserialize<RuleProfile>(before)!;
        roundTrip.Validate();
        Assert.True(edited.SameAs(roundTrip));
        foreach (RuleDefault entry in edited.Defaults)
        {
            Assert.Equal(edited.Default(entry.Key), roundTrip.Default(entry.Key));
            Assert.Equal(edited.Desired(entry.Key), roundTrip.Desired(entry.Key));
        }
        var current = fixture.Formations.Snapshot().Values.Concat(fixture.Snapshot().Values).ToDictionary();
        Assert.Equal(edited.Reapply(current).Apply, roundTrip.Reapply(current).Apply);
    }

    private static RuleChange Change(FormationSnapshot s, int id = 1, int value = 25) => new(FormationRules.Key(id, FormationRules.Fields[0]), s.Record(id).Value(FormationRules.Fields[0]), value);
    [Fact]
    public void NativeGettersAgreeForEveryByteEveryIdAndBothStoragePaths()
    {
        using var compressed = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Vectors", "formation_rules.json.gz")), System.IO.Compression.CompressionMode.Decompress);
        using JsonDocument doc = JsonDocument.Parse(compressed);
        Assert.False(doc.RootElement.GetProperty("nativeCodePatched").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("liveGameAccess").GetBoolean());
        int count = 0;
        foreach (JsonElement row in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            int id = row[0].GetInt32(), raw = row[3].GetInt32(), encoded = row[4].GetInt32();
            FormationField field = FormationRules.Field(row[1].GetString()!);
            Assert.Equal(encoded, FormationRules.Encode(raw, field.Rotate, id));
            Assert.Equal(raw, FormationRules.Decode((byte)encoded, field.Rotate, id));
            Assert.Equal(row[5].GetInt32(), FormationRules.Effective(raw, field, row[2].GetString() == "encoded"));
            ++count;
        }
        Assert.Equal(51200, count);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void WritesBothRepresentationsPreservesUnknownBytesAndCanUndo(bool encoded)
    {
        var memory = new FormationFixture(encoded);
        FormationSnapshot before = memory.Snapshot();
        RuleChange[] changes = FormationRules.Fields.Select(f => new RuleChange(FormationRules.Key(20, f), 10, f.Maximum)).ToArray();
        FormationSnapshot after = FormationMemory.Apply(memory, memory.Identity, before, changes);
        FormationRecord r = after.Record(20), old = before.Record(20);
        foreach (FormationField f in FormationRules.Fields) Assert.Equal(f.Maximum, r.Value(f));
        Assert.True(r.MirrorsAgree);
        for (int i = 0; i < 0xC8; i++) if (!FormationRules.Fields.Any(f => f.Plain == i)) Assert.Equal(old.Data[i], r.Data[i]);
        for (int i = 0; i < r.Vector.Length; i++) if (!FormationRules.Fields.Any(f => f.Vector == i)) Assert.Equal(old.Vector[i], r.Vector[i]);
        FormationSnapshot undone = FormationMemory.Apply(memory, memory.Identity, after, changes.Select(c => c with { Before = c.After, After = c.Before }).ToArray());
        Assert.Equal(old.Data, undone.Record(20).Data);
        Assert.Equal(old.Vector, undone.Record(20).Vector);
    }

    [Fact]
    public void PartialWriteFailureRollsBackEveryByteAndLeavesProfileUnchanged()
    {
        var memory = new FormationFixture();
        FormationSnapshot before = memory.Snapshot();
        memory.FailWrite = 2;
        Assert.Throws<IOException>(() => FormationMemory.Apply(memory, memory.Identity, before, [Change(before), Change(before, 2)]));
        foreach (FormationRecord r in before.Records)
        {
            Assert.Equal(r.Data, memory.Snapshot().Record(r.Id).Data);
            Assert.Equal(r.Vector, memory.Snapshot().Record(r.Id).Vector);
        }
    }

    [Fact]
    public void ReloadAndChangedValuesRefuseStaleWritesAndReapplyUsesFreshPointers()
    {
        var memory = new FormationFixture();
        FormationSnapshot before = memory.Snapshot();
        RuleChange change = Change(before);
        RuleProfile profile = before.Baseline(memory.Identity.ExeHash).WithChanges([change]);
        FormationMemory.Apply(memory, memory.Identity, before, [change]);
        Assert.Empty(profile.Reapply(memory.Snapshot().Values).Apply);
        memory.Array += 0x10000; memory.Vectors += 0x10000; memory.Load();
        int writes = memory.Writes;
        Assert.Throws<InvalidOperationException>(() => FormationMemory.Apply(memory, memory.Identity, before, [change]));
        Assert.Equal(writes, memory.Writes);
        FormationSnapshot loaded = memory.Snapshot();
        var plan = profile.Reapply(loaded.Values);
        Assert.Single(plan.Apply);
        FormationSnapshot applied = FormationMemory.Apply(memory, memory.Identity, loaded, plan.Apply);
        Assert.Equal(25, applied.Values[change.Key]);
        memory.Load(); // Same addresses, reset values: also detected without any date inference.
        Assert.Single(profile.Reapply(memory.Snapshot().Values).Apply);
    }

    [Fact]
    public void ThirdValueConflictsAndRestoreRemovesOverrideAndIsUndoable()
    {
        var memory = new FormationFixture();
        FormationSnapshot s = memory.Snapshot();
        RuleProfile baseline = s.Baseline(memory.Identity.ExeHash), edited = baseline.WithChanges([Change(s)]);
        var third = s.Values; third[Change(s).Key] = 17;
        Assert.Empty(edited.Reapply(third).Apply);
        Assert.Single(edited.Reapply(third).Conflicts);
        FormationSnapshot applied = FormationMemory.Apply(memory, memory.Identity, s, [Change(s)]);
        RuleChange restore = Change(s) with { Before = 25, After = 10 };
        RuleProfile restored = edited.WithChanges([restore], restoreDefaults: true);
        FormationSnapshot reset = FormationMemory.Apply(memory, memory.Identity, applied, [restore]);
        Assert.Empty(restored.Overrides);
        Assert.Empty(restored.Reapply(reset.Values).Apply);
        FormationSnapshot undone = FormationMemory.Apply(memory, memory.Identity, reset, [restore with { Before = 10, After = 25 }]);
        Assert.Equal(25, undone.Values[restore.Key]);
        Assert.Empty(edited.Reapply(undone.Values).Apply);
    }

    [Fact]
    public void RejectsWrongClassDiscontinuousTableMalformedVectorAndDisagreeingBaseline()
    {
        foreach (int mode in Enumerable.Range(0, 4))
        {
            var memory = new FormationFixture();
            FormationSnapshot s = memory.Snapshot(); FormationRecord r = s.Record(1);
            if (mode == 0) memory.Set(r.Address, BitConverter.GetBytes(123UL));
            if (mode == 1) memory.Set(s.Root + FormationMemory.TableOffset + 8, BitConverter.GetBytes(r.Address + 8));
            if (mode == 2) memory.Set(r.Address + 0xB0, BitConverter.GetBytes(r.VectorAddress + 24));
            if (mode == 3) memory.Set(r.Address + 0x7F, [11]);
            Assert.Throws<InvalidDataException>(() => memory.Snapshot().Baseline(memory.Identity.ExeHash));
            Assert.Equal(0, memory.Writes);
        }
    }

    [Fact]
    public void FileRoundtripConcurrencyMalformedAndIncompatibleProfilesAreGuarded()
    {
        string folder = Path.Combine(Path.GetTempPath(), "San14Rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var memory = new FormationFixture(); FormationSnapshot s = memory.Snapshot();
            RuleProfile baseline = s.Baseline(memory.Identity.ExeHash);
            var file = new RuleProfileFile(Path.Combine(folder, "profile.json"));
            RuleProfileRead first = file.Save(baseline, "");
            Assert.True(baseline.SameAs(file.Read().Profile!));
            Assert.Throws<InvalidOperationException>(() => file.Save(baseline, ""));
            bool applied = false, rolledBack = false;
            Assert.Throws<InvalidOperationException>(() => file.Commit(baseline.WithChanges([Change(s)]), first.Stamp,
                () => { applied = true; file.Save(baseline with { AutoApply = false }, first.Stamp); }, () => rolledBack = true));
            Assert.True(applied && rolledBack);
            Assert.False(file.Read().Profile!.AutoApply);
            Assert.Throws<InvalidDataException>(() => s.Check(baseline with { Build = new string('B', 64) }, memory.Identity.ExeHash));
            File.WriteAllText(Path.Combine(folder, "profile.json"), "{\"Version\":1}");
            Assert.Throws<InvalidDataException>(() => file.Read());
            Assert.Throws<InvalidDataException>(() => (baseline with { Defaults = baseline.Defaults[..99] }).Validate());
            Assert.Throws<InvalidDataException>(() => (baseline with { Overrides = [new(Change(s).Key, 31)] }).Validate());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void SaveFailureAfterRealMemoryWriteRestoresMemory()
    {
        string folder = Path.Combine(Path.GetTempPath(), "San14Rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var memory = new FormationFixture(); FormationSnapshot before = memory.Snapshot();
            RuleChange[] changes = [Change(before)]; RuleProfile baseline = before.Baseline(memory.Identity.ExeHash);
            var file = new RuleProfileFile(Path.Combine(folder, "profile.json"));
            RuleProfileRead kept = file.Save(baseline, ""); FormationSnapshot? written = null;
            Assert.Throws<InvalidOperationException>(() => file.Commit(baseline.WithChanges(changes), kept.Stamp,
                () => { written = FormationMemory.Apply(memory, memory.Identity, before, changes); file.Save(baseline with { AutoApply = false }, kept.Stamp); },
                () => FormationMemory.Apply(memory, memory.Identity, written!, changes.Select(c => c with { Before = c.After, After = c.Before }).ToArray())));
            Assert.Equal(before.Record(1).Data, memory.Snapshot().Record(1).Data);
            Assert.Equal(before.Record(1).Vector, memory.Snapshot().Record(1).Vector);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void LoadDuringWriteDoesNotRollbackIntoOldObjectsAndReportsRecoveryFailure()
    {
        var memory = new FormationFixture(); FormationSnapshot before = memory.Snapshot();
        memory.OnWrite = count => { if (count == 1) { memory.Array += 0x10000; memory.Vectors += 0x10000; memory.Load(); } };
        AggregateException failure = Assert.Throws<AggregateException>(() => FormationMemory.Apply(memory, memory.Identity, before, [Change(before)]));
        Assert.StartsWith(L.Rules.RollbackFailed, failure.Message);
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal(1, memory.Writes); // Old vector/record are no longer touched, including during recovery.
        Assert.Equal(10, memory.Snapshot().Values[Change(before).Key]);
    }

    [Fact]
    public void ChangedScalarOrVectorOwnerAndInvalidGameStateAreRefusedBeforeWriting()
    {
        for (int mode = 0; mode < 3; mode++)
        {
            var memory = new FormationFixture(); FormationSnapshot before = memory.Snapshot(); FormationRecord r = before.Record(1);
            if (mode == 0) memory.Set(r.VectorAddress, [FormationRules.Encode(17, 5, 1)]);
            if (mode == 1) memory.Set(r.Address + 0xA8, BitConverter.GetBytes(r.VectorAddress + 8));
            if (mode == 2) memory.Set(before.State + 0x34, [0, 0, 0, 0, 0]);
            Assert.ThrowsAny<Exception>(() => FormationMemory.Apply(memory, memory.Identity, before, [Change(before)]));
            Assert.Equal(0, memory.Writes);
        }
    }

    [Fact]
    public void LayoutComparisonUsesPointerValuesAndDetectsNewStorage()
    {
        var tables = new GameTables(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18);
        var before = new GameLayout(tables, 1, 2, 100, 3, 1001, 4, 5, 100, [6, 7], [8, 9], 10, 100, 11, Gates: [12, 13]);
        Assert.True(before.SameStorage(before with { Forces = [6, 7], Corps = [8, 9], Gates = [12, 13] }));
        Assert.False(before.SameStorage(before with { CityArray = 100 }));
        Assert.False(before.SameStorage(before with { State = 100 }));
        Assert.False(before.SameStorage(before with { Forces = [6, 100] }));
        Assert.False(before.SameStorage(before with { Gates = [12, 100] }));
    }

    [Fact]
    public void RemovedProfileDoesNotRecaptureCurrentModifiedValuesAsDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), "San14Rules-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var memory = new FormationFixture(); var file = new RuleProfileFile(path);
            file.Save(memory.Snapshot().Baseline(memory.Identity.ExeHash), "");
            File.Delete(path);
            Assert.Throws<InvalidOperationException>(() => file.Read());
            Assert.Throws<InvalidOperationException>(() => file.Save(memory.Snapshot().Baseline(memory.Identity.ExeHash), ""));
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); File.Delete(path + ".lock"); }
    }
}

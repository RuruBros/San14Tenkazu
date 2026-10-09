using System.Text.Json;

namespace San14Tenkazu.Tests;

public class TacticRuleTests
{
    private static RuleChange Change(TacticSnapshot s, string field = "amount1", int after = 25, int id = 1) => new(TacticRules.Key(id, TacticRules.Field(field)), s.Record(id).Value(TacticRules.Field(field)), after);

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void TwoSlotsAndAbilityWriteOnlyTheirMirrorsAndEncodedLowBytes(bool encoded)
    {
        var m = new TacticFixture(encoded); TacticSnapshot before = m.Snapshot();
        RuleChange[] changes = [Change(before, "kind2", 8), Change(before, "range2", 3), Change(before, "amount2", 240), Change(before, "level2", 10), Change(before, "ability", 2)];
        TacticSnapshot after = TacticMemory.Apply(m, m.Identity, before, changes);
        TacticRecord b = before.Record(1), a = after.Record(1);
        foreach (var c in changes) Assert.Equal(c.After, after.Values[c.Key]);
        for (int i = 0; i < b.Data.Length; ++i)
            if (!changes.Any(c => TacticRules.Field(c.Key.Field).Plain == i)) Assert.Equal(b.Data[i], a.Data[i]);
        for (int i = 0; i < b.Vector.Length; ++i)
            if (!changes.Any(c => TacticRules.Field(c.Key.Field).Vector == i)) Assert.Equal(b.Vector[i], a.Vector[i]);
        Assert.True(a.MirrorsAgree); Assert.Equal(before.Record(2), after.Record(2), TacticRecordDataComparer.Instance);
    }

    private sealed class TacticRecordDataComparer : IEqualityComparer<TacticRecord>
    {
        internal static readonly TacticRecordDataComparer Instance = new();
        public bool Equals(TacticRecord? x, TacticRecord? y) => x!.Data.SequenceEqual(y!.Data) && x.Vector.SequenceEqual(y.Vector);
        public int GetHashCode(TacticRecord r) => r.Id;
    }

    [Fact]
    public void LegacyFileUpgradeAndTacticCapturePreserveFormationDefaultsOverridesAndAutoFlag()
    {
        var m = new TacticFixture(); var original = m.Formations.Snapshot().Baseline(m.Identity.ExeHash);
        original = original.WithChanges([new(FormationRules.Key(1, FormationRules.Fields[0]), 10, 16)]) with { AutoApply = false };
        string path = Path.Combine(Path.GetTempPath(), "TacticProfile-" + Guid.NewGuid().ToString("N"), "rules.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            byte[] legacy = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, original.Build, Schema = "formation-v1", original.Catalog, original.AutoApply, original.Defaults, original.Overrides });
            File.WriteAllBytes(path, legacy); var file = new RuleProfileFile(path); var read = file.Read();
            Assert.Equal(legacy, File.ReadAllBytes(path));
            RuleProfile upgraded = read.Profile!.AddTactics(m.Snapshot());
            Assert.False(upgraded.AutoApply); Assert.Equal(original.Defaults, upgraded.Defaults.Where(d => d.Key.Table == "formation"));
            Assert.Equal(original.Overrides, upgraded.Overrides);
            var saved = file.Save(upgraded, read.Stamp);
            Assert.True(upgraded.SameAs(file.Read().Profile!)); Assert.Equal(1900, saved.Profile!.Defaults.Length);
            Assert.Equal(2, saved.Profile.Catalogs!.Length);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void PartialWritesRestoreBothCopies(int fail)
    {
        var m = new TacticFixture(); var s = m.Snapshot(); m.FailWrite = fail;
        Assert.Throws<IOException>(() => TacticMemory.Apply(m, m.Identity, s, [Change(s)]));
        Assert.True(s.Values.All(v => m.Snapshot().Values[v.Key] == v.Value)); Assert.True(m.Snapshot().Records.All(r => r.MirrorsAgree));
    }

    [Fact]
    public void ReloadChangedStorageRejectsOldWriteAndDoesNotRollbackIntoOldAddresses()
    {
        var m = new TacticFixture(); var s = m.Snapshot();
        m.OnWrite = _ => { m.Array += 0x100000; m.Vectors += 0x100000; m.Load(); };
        Assert.Throws<AggregateException>(() => TacticMemory.Apply(m, m.Identity, s, [Change(s)]));
        Assert.Equal(1, m.Writes);
        Assert.Equal(10, m.Snapshot().Values[Change(s).Key]);
        Assert.Throws<InvalidOperationException>(() => TacticMemory.Apply(m, m.Identity, s, [Change(s)]));
    }

    [Fact]
    public void RestoreUndoReapplyAndThirdValueKeepFormationOverridesSeparate()
    {
        var m = new TacticFixture(); var s = m.Snapshot(); var baseProfile = m.Profile();
        RuleChange c = Change(s); var profile = baseProfile.WithChanges([c]);
        var plan = profile.Reapply(new RuleReadValues(baseProfile, s).Values);
        Assert.Single(plan.Apply); Assert.Empty(plan.Conflicts);
        var applied = TacticMemory.Apply(m, m.Identity, s, [c]);
        RuleChange restore = c with { Before = c.After, After = c.Before };
        var restoredProfile = profile.WithChanges([restore], true);
        var restored = TacticMemory.Apply(m, m.Identity, applied, [restore]);
        Assert.Empty(restoredProfile.Overrides);
        var undone = TacticMemory.Apply(m, m.Identity, restored, [c]); Assert.Equal(25, undone.Values[c.Key]);
        var values = baseProfile.Defaults.ToDictionary(d => d.Key, d => d.Value); values[c.Key] = 19;
        Assert.Single(profile.Reapply(values).Conflicts); Assert.Empty(profile.Reapply(values).Apply);
    }
    private sealed record RuleReadValues(RuleProfile Profile, TacticSnapshot Tactics)
    {
        internal Dictionary<RuleKey, int> Values => Profile.Defaults.Where(d => d.Key.Table == "formation").Select(d => new KeyValuePair<RuleKey, int>(d.Key, d.Value)).Concat(Tactics.Values).ToDictionary();
    }

    [Fact]
    public void MalformedVectorsMirrorsClassesAndCatalogDefaultsAreRejected()
    {
        foreach (int mode in Enumerable.Range(0, 4))
        {
            var m = new TacticFixture(); var s = m.Snapshot(); var r = s.Record(1);
            if (mode == 0) m.Set(r.Address + 0x78, BitConverter.GetBytes(r.VectorAddress + 39));
            if (mode == 1) m.Set(r.Address + 0x66, [11]);
            if (mode == 2) m.Set(r.Address, BitConverter.GetBytes(123UL));
            if (mode == 3) m.Set(r.Address + 0x44, [0]);
            Assert.Throws<InvalidDataException>(() => m.Profile()); Assert.Equal(0, m.Writes);
        }
        var profile = new TacticFixture().Profile();
        Assert.Throws<InvalidDataException>(() => (profile with { Defaults = profile.Defaults[..^1] }).Validate());
        Assert.Throws<InvalidDataException>(() => profile.WithChanges([new(new("tactic", 1, "ability"), 1, 0)]));
        Assert.Throws<InvalidDataException>(() => profile.WithChanges([new(new("tactic", 1, "kind1"), 1, 24)]));
    }

    [Fact]
    public void ProfileSaveConflictRollsBackTacticMemoryWithoutChangingFormationDefaults()
    {
        var m = new TacticFixture(); var s = m.Snapshot(); var baseline = m.Profile(); RuleChange c = Change(s);
        string path = Path.Combine(Path.GetTempPath(), "TacticCommit-" + Guid.NewGuid().ToString("N"), "rules.json");
        var file = new RuleProfileFile(path); var first = file.Save(baseline, ""); TacticSnapshot? written = null;
        try
        {
            Assert.Throws<InvalidOperationException>(() => file.Commit(baseline.WithChanges([c]), first.Stamp,
                () => { written = TacticMemory.Apply(m, m.Identity, s, [c]); file.Save(baseline with { AutoApply = false }, first.Stamp); },
                () => TacticMemory.Apply(m, m.Identity, written!, [c with { Before = c.After, After = c.Before }])));
            Assert.Equal(10, m.Snapshot().Values[c.Key]); Assert.True(m.Snapshot().Records.All(r => r.MirrorsAgree));
            Assert.Equal(baseline.Defaults, file.Read().Profile!.Defaults); Assert.Empty(file.Read().Profile!.Overrides);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}

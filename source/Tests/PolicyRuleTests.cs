namespace San14Tenkazu.Tests;

public class PolicyRuleTests
{
    private static RuleChange Level(PolicySnapshot s, int slot, int value) => new(new("policyeffect", 1, $"level{slot}"), s.Record("policyeffect", 1).Value(PolicyRules.Levels[slot - 1]), value);
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WritesBothTablesAndRestoresAllUnchangedBytes(bool encoded)
    {
        var m = new PolicyFixture(encoded); var s = m.Snapshot();
        var record = s.Record("policyeffect", 1); byte[] oldVector = record.Vector;
        RuleChange[] changes = [new(new("policy", 1, "effect8"), 0, 2), Level(s, 10, 65535)];
        var after = PolicyMemory.Apply(m, m.Identity, s, changes);
        Assert.Equal(2, after.Values[changes[0].Key]); Assert.Equal(65535, after.Values[changes[1].Key]);
        Assert.All(after.Records, r => Assert.True(r.MirrorsAgree));
        if (encoded) for (int i = 0; i < 40; ++i) if (i is not (36 or 37)) Assert.Equal(oldVector[i], after.Record("policyeffect", 1).Vector[i]);
        var restored = PolicyMemory.Apply(m, m.Identity, after, changes.Select(c => c with { Before = c.After, After = c.Before }).ToArray());
        Assert.Equal(s.Values.OrderBy(p => p.Key.Table).ThenBy(p => p.Key.Record).ThenBy(p => p.Key.Field), restored.Values.OrderBy(p => p.Key.Table).ThenBy(p => p.Key.Record).ThenBy(p => p.Key.Field));
        Assert.Equal(oldVector, restored.Record("policyeffect", 1).Vector);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PartialWriteInEitherTableRollsBack(int fail)
    {
        var m = new PolicyFixture { FailWrite = fail }; var s = m.Snapshot();
        RuleChange[] changes = [new(new("policy", 1, "effect8"), 0, 2), Level(s, 1, 500)];
        Assert.ThrowsAny<IOException>(() => PolicyMemory.Apply(m, m.Identity, s, changes));
        Assert.Equal(s.Record("policy", 1).Data, m.Snapshot().Record("policy", 1).Data);
        Assert.Equal(s.Record("policyeffect", 1).Data, m.Snapshot().Record("policyeffect", 1).Data);
        Assert.Equal(s.Record("policyeffect", 1).Vector, m.Snapshot().Record("policyeffect", 1).Vector);
    }
    [Fact]
    public void RefusesChangedVectorAndMismatchedMirror()
    {
        var m = new PolicyFixture(); var s = m.Snapshot(); var r = s.Record("policyeffect", 1);
        m.Set(r.VectorAddress, [0]);
        Assert.Throws<InvalidOperationException>(() => PolicyMemory.Apply(m, m.Identity, s, [Level(s, 2, 500)])); Assert.Equal(0, m.Writes);
    }
    [Fact]
    public void RefusesPartiallyConstructedVector()
    {
        var m = new PolicyFixture(); var r = m.Snapshot().Record("policyeffect", 1);
        m.Set(r.Address + 0xA0, BitConverter.GetBytes(r.VectorAddress + 39));
        Assert.Throws<InvalidDataException>(() => m.Snapshot()); Assert.Equal(0, m.Writes);
    }
    [Fact]
    public void DoesNotRecoverIntoReplacedStorage()
    {
        var m = new PolicyFixture(); var s = m.Snapshot();
        m.OnWrite = _ => { m.PolicyArray += 0x100000; m.EffectArray += 0x100000; m.Load(); };
        Assert.Throws<AggregateException>(() => PolicyMemory.Apply(m, m.Identity, s, [Level(s, 1, 500)]));
        Assert.Equal(1, m.Writes); Assert.Equal(2, m.Snapshot().Values[new("policyeffect", 1, "level1")]);
    }
    [Fact]
    public void AddingPoliciesPreservesFormationAndTacticBaselinesAndOverrides()
    {
        var m = new PolicyFixture(); var old = m.Tactics.Profile();
        RuleChange[] edits = [new(new("formation", 1, "mobility"), 10, 16), new(new("tactic", 1, "amount1"), 10, 50)];
        old = old.WithChanges(edits);
        var next = old.AddPolicies(m.Snapshot()); next.Validate();
        Assert.Equal(old.Defaults, next.Defaults.Where(d => d.Key.Table is "formation" or "tactic"));
        Assert.Equal(old.Overrides, next.Overrides); Assert.Equal(old.AutoApply, next.AutoApply);
        Assert.True(next.SameAs(next.AddPolicies(m.Snapshot())));
    }
    [Fact]
    public void ValidatesUshortLevelsAndEffectIdsSeparately()
    {
        var m = new PolicyFixture(); var p = m.Profile();
        Assert.Equal(65535, p.WithChanges([Level(m.Snapshot(), 10, 65535)]).Desired(new("policyeffect", 1, "level10")));
        Assert.Throws<InvalidDataException>(() => p.WithChanges([Level(m.Snapshot(), 10, 65536)]));
        Assert.Throws<InvalidDataException>(() => p.WithChanges([new(new("policy", 1, "effect1"), 1, 101)]));
        Assert.Throws<InvalidDataException>(() => (p with { Catalogs = p.Catalogs!.Where(c => c.Table != "policyeffect").ToArray() }).Validate());
    }
    [Fact]
    public void ReapplyAllowsOnlyBaselineAndRestoreRemovesOverride()
    {
        var m = new PolicyFixture(); var s = m.Snapshot(); var c = Level(s, 1, 500); var p = m.Profile().WithChanges([c]);
        Assert.Single(p.Reapply(s.Values).Apply);
        var third = s.Values; third[c.Key] = 300; Assert.Single(p.Reapply(third).Conflicts);
        Assert.Empty(p.WithChanges([c with { Before = c.After, After = c.Before }], restoreDefaults: true).Overrides);
    }
    [Fact]
    public void FileRaceRestoresPolicyMemoryWithoutReplacingExternalProfile()
    {
        var m = new PolicyFixture(); var s = m.Snapshot(); var c = Level(s, 1, 500);
        string folder = Path.Combine(Path.GetTempPath(), "PolicyRuleTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "rules.json"); var file = new RuleProfileFile(path); var p = m.Profile(); var saved = file.Save(p, ""); PolicySnapshot? written = null;
        try
        {
            Assert.Throws<InvalidOperationException>(() => file.Commit(p.WithChanges([c]), saved.Stamp,
                () => { written = PolicyMemory.Apply(m, m.Identity, s, [c]); new RuleProfileFile(path).Save(p with { AutoApply = false }, saved.Stamp); },
                () => PolicyMemory.Apply(m, m.Identity, written!, [c with { Before = c.After, After = c.Before }])));
            Assert.Equal(c.Before, m.Snapshot().Values[c.Key]); Assert.False(file.Read().Profile!.AutoApply);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}

using System.Buffers.Binary;
using Xunit;

namespace San14Tenkazu.Tests;

public class FixedRuleMigrationTests
{
    private static RuleProfile Source(PolicyFixture f) => f.Profile() with { Build = GameBuildFacts.ExeSha256 };
    private static RuleProfile Updated(PolicyFixture f) => f.Profile() with { Build = GameNativeFactsNovember2026.ExeSha256 };
    private static RuleProfile Edit(RuleProfile profile, RuleKey key, int value) =>
        profile.WithChanges([new(key, profile.Default(key), value)]);

    [Fact]
    public void MixedProfileCarriesOnlyIdentifiedRowsAndEffectReferencesAcrossUpdate()
    {
        var fixture = new PolicyFixture();
        var tactic = new RuleKey("tactic", 1, "amount1");
        var policy = new RuleKey("policy", 1, "effect2");
        var level = new RuleKey("policyeffect", 2, "level1");
        var source = Edit(Edit(Edit(Source(fixture), tactic, 71), policy, 2), level, 260);
        fixture.Set(fixture.Tactics.Array + 2 * 0x90 + 0x40, [3]);
        var target = Updated(fixture);
        var combined = SettingsMigration.PlanRules(source, target);
        var merged = combined.Resolve(new HashSet<RuleKey> { level });
        Assert.Equal(71, merged.Desired(tactic));
        Assert.Equal(2, merged.Desired(policy));
        Assert.Equal(260, merged.Desired(level));
        Assert.False(merged.AutoApply);
        // Changing only the suffix beyond the old 12-byte hash still invalidates the referred effect.
        fixture.Set(fixture.EffectArray + 2 * 0xb0 + 0x10 + 12, [65, 0]);
        var partial = SettingsMigration.PlanRules(source, Updated(fixture));
        Assert.Contains(policy, partial.Unavailable);
        Assert.Contains(level, partial.Unavailable);
        Assert.DoesNotContain(tactic, partial.Unavailable);
        Assert.Equal(71, partial.Resolve(new HashSet<RuleKey>()).Desired(tactic));
    }

    [Fact]
    public void ChangedSlotMeaningAndNewConsumersDoNotReceiveOldValues()
    {
        var f = new PolicyFixture();
        var source = Source(f);
        var key = new RuleKey("tactic", 1, "amount1");
        var level = new RuleKey("policyeffect", 2, "level1");
        source = Edit(Edit(source, key, 75), level, 300);
        var target = Edit(Edit(Updated(f), key with { Field = "kind1" }, 2), new("policy", 2, "effect8"), 2);
        var plan = SettingsMigration.PlanRules(source, target);
        Assert.Contains(key, plan.Unavailable);
        Assert.Contains(level, plan.Unavailable);
        Assert.Empty(plan.Changes);
        var current = target.Defaults.ToDictionary(d => d.Key, d => target.Desired(d.Key));
        var restored = source.Reapply(current);
        Assert.Contains(key, restored.Conflicts);
        Assert.Contains(level, restored.Conflicts);
        Assert.Empty(restored.Apply);
    }

    [Fact]
    public void EditsRejectSlotOrReferenceChangesSincePreview()
    {
        var tactics = new TacticFixture(encoded: false);
        var expected = tactics.Snapshot();
        var row = expected.Record(1);
        tactics.Set(row.Address + (ulong)TacticRules.Field("kind1").Plain, [2]);
        Assert.Throws<InvalidOperationException>(() => TacticMemory.Apply(tactics, tactics.Identity, expected,
            [new(new("tactic", 1, "amount1"), row.Value(TacticRules.Field("amount1")), 75)]));
        var policies = new PolicyFixture();
        var before = policies.Snapshot();
        policies.Set(before.Record("policy", 1).Address + (ulong)PolicyRules.Effects[0].Plain, [2]);
        var effect = before.Record("policyeffect", 2);
        Assert.Throws<InvalidOperationException>(() => PolicyMemory.Apply(policies, policies.Identity, before,
            [new(new("policyeffect", 2, "level1"), effect.Value(PolicyRules.Levels[0]), 260)]));
        Assert.Equal(0, policies.Writes + tactics.Writes);
    }

    [Fact]
    public void SourceCannotBeReplacedDuringGuardedSettingOperation()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2]);
            DateTime started = DateTime.UtcNow.AddMinutes(1);
            string digest = RuleDataset.Fingerprint(path, started);
            using (RuleDataset.Guard(path, started, digest))
            {
                if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => File.WriteAllBytes(path, [3, 4]));
            }
            File.WriteAllBytes(path, [3, 4]);
            Assert.Throws<IOException>(() => RuleDataset.Guard(path, started, digest));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingOrAmbiguousLayoutDoesNotHideOtherTables()
    {
        var layout = FileRuleLayout.All.Single(l => l.Table == "policy");
        var body = new byte[16 + layout.Count * layout.Stride];
        BinaryPrimitives.WriteInt32LittleEndian(body, layout.Preceding);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), layout.Count);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(body.Length - 8), layout.Preceding + 1);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(body.Length - 4), layout.FollowingCount);
        body[8 + 47 * layout.Stride + 142] = 46;
        var tables = FileRuleLayout.Read([0, 0, 0, .. body]);
        Assert.Single(tables);
        Assert.Equal("46", tables["policy"][47].Values[6]);
        Assert.Empty(FileRuleLayout.Read([.. body, .. body]));
    }
}

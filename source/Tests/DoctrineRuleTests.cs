namespace San14Tenkazu.Tests;

public class DoctrineRuleTests
{
    [Fact]
    public void FiveFieldsPreserveTopologyCategoryAndCostScratchBytes()
    {
        var m=new PlainRuleFixture(DoctrineRules.Spec); var before=m.Snapshot(); var row=before.Record(1);
        RuleChange[] changes=DoctrineRules.Spec.Fields.Select(f => new RuleChange(new("doctrine",1,f.Id),row.Value(f),f.Id=="effect" ? -32768 : f.Maximum)).ToArray();
        var after=PlainRuleMemory.Apply(m,m.Identity,before,changes);
        Assert.All(changes,c => Assert.Equal(c.After,after.Values[c.Key]));
        for (int i=0;i<row.Data.Length;++i) if (!DoctrineRules.Spec.Fields.Any(f => i>=f.Offset && i<f.Offset+f.Storage.Size)) Assert.Equal(row.Data[i],after.Record(1).Data[i]);
        var restored=PlainRuleMemory.Apply(m,m.Identity,after,changes.Select(c => c with {Before=c.After,After=c.Before}).ToArray());
        Assert.Equal(row.Data,restored.Record(1).Data);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void SignedWordAndLaterBytePartialFailuresRecoverAll(int fail)
    {
        var m=new PlainRuleFixture(DoctrineRules.Spec) {FailWrite=fail}; var before=m.Snapshot();
        RuleChange[] changes=[new(new("doctrine",1,"effect"),5,-200),new(new("doctrine",1,"kind1"),2,6),new(new("doctrine",1,"cost1"),5,255)];
        Assert.Throws<IOException>(() => PlainRuleMemory.Apply(m,m.Identity,before,changes));
        Assert.Equal(before.Record(1).Data,m.Snapshot().Record(1).Data);
    }
    [Theory]
    [InlineData(-32768)]
    [InlineData(32767)]
    public void SignedBaselineAndOverrideRoundTrip(int value)
    {
        var m=new PlainRuleFixture(DoctrineRules.Spec); var p=m.Profile(); var c=new RuleChange(new("doctrine",1,"effect"),5,value);
        var edited=PlainRuleMemory.Apply(m,m.Identity,m.Snapshot(),[c]); Assert.Equal(value,edited.Values[c.Key]);
        var saved=p.WithChanges([c]); saved.Validate(); Assert.Equal(value,saved.Desired(c.Key));
        var baseline=m.Profile(); Assert.Equal(value,baseline.Default(c.Key));
        Assert.Equal(p.Defaults,p.WithChanges([c]).WithChanges([c],true).Defaults);
    }
    [Fact]
    public void ProfileRejectsOutOfRangeOrZeroTypedCosts()
    {
        var m=new PlainRuleFixture(DoctrineRules.Spec); var p=m.Profile();
        foreach (var bad in new[] {("effect",-32769),("effect",32768),("kind1",7),("kind2",-1),("cost1",256),("cost2",0)})
            Assert.Throws<InvalidDataException>(() => p.WithChanges([new(new("doctrine",1,bad.Item1),p.Desired(new("doctrine",1,bad.Item1)),bad.Item2)]));
        var none=p.WithChanges([new(new("doctrine",1,"kind2"),2,0),new(new("doctrine",1,"cost2"),5,0)]);
        none.Validate(); Assert.Equal(0,none.Desired(new("doctrine",1,"cost2")));
    }
    [Fact]
    public void ExtensionPreservesEveryOldTableOverrideAndRawCostBaseline()
    {
        var m=new PlainRuleFixture(DoctrineRules.Spec); var row=m.Snapshot().Record(1); m.Set(row.Address+0x3D,[255]); m.Set(row.Address+0x44,BitConverter.GetBytes((short)-100));
        var old=m.Ranks!.Profile() with {AutoApply=false}; old=old.WithChanges([new(new("rank",1,"capacity"),18000,20000)]);
        var p=old.AddPlain(m.Snapshot()); Assert.Equal(old.Defaults,p.Defaults.Where(d => d.Key.Table!="doctrine")); Assert.Equal(old.Overrides,p.Overrides); Assert.False(p.AutoApply);
        Assert.Equal(255,p.Default(new("doctrine",1,"kind1"))); Assert.Equal(-100,p.Default(new("doctrine",1,"effect")));
        Assert.True(p.SameAs(p.AddPlain(m.Snapshot())));
        RuleChange[] changes=[new(new("doctrine",1,"kind1"),255,6),new(new("doctrine",1,"effect"),-100,300)];
        var after=PlainRuleMemory.Apply(m,m.Identity,m.Snapshot(),changes);
        var restored=PlainRuleMemory.Apply(m,m.Identity,after,changes.Select(c => c with {Before=c.After,After=c.Before}).ToArray());
        Assert.Equal(255,restored.Values[new("doctrine",1,"kind1")]); Assert.Equal(-100,restored.Values[new("doctrine",1,"effect")]);
        Assert.True(p.SameAs(p.WithChanges(changes).WithChanges(changes,true)));
    }
    [Fact]
    public void RejectsDuplicatesStorageOverflowWrongTableAndStaleValue()
    {
        var m=new PlainRuleFixture(DoctrineRules.Spec); var before=m.Snapshot(); var c=new RuleChange(new("doctrine",1,"effect"),5,-32768);
        Assert.Throws<InvalidDataException>(() => PlainRuleMemory.Apply(m,m.Identity,before,[c,c]));
        Assert.Throws<InvalidDataException>(() => PlainRuleMemory.Apply(m,m.Identity,before,[c with {After=-32769}]));
        Assert.Throws<InvalidDataException>(() => PlainRuleMemory.Apply(m,m.Identity,before,[c with {Key=new("rank",1,"effect")}]));
        m.Set(before.Record(1).Address+0x44,BitConverter.GetBytes((short)7)); Assert.Throws<InvalidOperationException>(() => PlainRuleMemory.Apply(m,m.Identity,before,[c])); Assert.Equal(0,m.Writes);
    }
    [Fact]
    public void ReplacedTableRefusesRollbackAtOldAddress()
    {
        var m=new PlainRuleFixture(DoctrineRules.Spec); var before=m.Snapshot(); m.OnWrite=_ => {m.Array+=0x100000; m.Load();};
        Assert.Throws<AggregateException>(() => PlainRuleMemory.Apply(m,m.Identity,before,[new(new("doctrine",1,"effect"),5,-32768)]));
        Assert.Equal(1,m.Writes); Assert.Equal(5,m.Snapshot().Values[new("doctrine",1,"effect")]);
    }
    [Fact]
    public void FileRaceRestoresSignedMemoryAndKeepsExternalSettings()
    {
        var m=new PlainRuleFixture(DoctrineRules.Spec); var s=m.Snapshot(); var c=new RuleChange(new("doctrine",1,"effect"),5,-32768); var p=m.Profile();
        string folder=Path.Combine(Path.GetTempPath(),"DoctrineRuleTests",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var file=new RuleProfileFile(Path.Combine(folder,"rules.json")); var kept=file.Save(p,""); PlainRuleSnapshot? after=null;
        try {
            Assert.Throws<InvalidOperationException>(() => file.Commit(p.WithChanges([c]),kept.Stamp,
                () => {after=PlainRuleMemory.Apply(m,m.Identity,s,[c]); file.Save(p with {AutoApply=false},kept.Stamp);},
                () => PlainRuleMemory.Apply(m,m.Identity,after!,[c with {Before=c.After,After=c.Before}])));
            Assert.Equal(c.Before,m.Snapshot().Values[c.Key]); Assert.False(file.Read().Profile!.AutoApply);
        } finally {Directory.Delete(folder,true);}
    }
}

namespace San14Tenkazu.Tests;

public class RankRuleTests
{
    [Fact]
    public void WritesSevenScalarsWithoutChangingNamesFlagsOrPadding()
    {
        var m=new PlainRuleFixture(); var before=m.Snapshot(); var row=before.Record(1);
        RuleChange[] changes=RankRules.Spec.Fields.Select(f => new RuleChange(new("rank",1,f.Id),row.Value(f),f.Maximum)).ToArray();
        var after=PlainRuleMemory.Apply(m,m.Identity,before,changes);
        Assert.All(changes,c => Assert.Equal(c.After,after.Values[c.Key]));
        for (int i=0;i<row.Data.Length;++i) if (!RankRules.Spec.Fields.Any(f => i>=f.Offset && i<f.Offset+f.Storage.Size)) Assert.Equal(row.Data[i],after.Record(1).Data[i]);
        var restored=PlainRuleMemory.Apply(m,m.Identity,after,changes.Select(c => c with {Before=c.After,After=c.Before}).ToArray());
        Assert.Equal(row.Data,restored.Record(1).Data);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PartialScalarFailureRecoversAllAttemptedWrites(int fail)
    {
        var m=new PlainRuleFixture {FailWrite=fail}; var before=m.Snapshot();
        Assert.Throws<IOException>(() => PlainRuleMemory.Apply(m,m.Identity,before,[new(new("rank",1,"capacity"),18000,65535),new(new("rank",1,"grade"),2,9)]));
        Assert.Equal(before.Record(1).Data,m.Snapshot().Record(1).Data);
    }
    [Fact]
    public void RejectsDuplicateOverflowStaleValueAndWrongTable()
    {
        var m=new PlainRuleFixture(); var before=m.Snapshot(); var c=new RuleChange(new("rank",1,"capacity"),18000,20000);
        Assert.Throws<InvalidDataException>(() => PlainRuleMemory.Apply(m,m.Identity,before,[c,c]));
        Assert.Throws<InvalidDataException>(() => PlainRuleMemory.Apply(m,m.Identity,before,[c with {After=65536}]));
        Assert.Throws<InvalidDataException>(() => PlainRuleMemory.Apply(m,m.Identity,before,[c with {Key=new("office",1,"capacity")}]));
        m.Set(before.Record(1).Address+0xAA,BitConverter.GetBytes((ushort)19000));
        Assert.Throws<InvalidOperationException>(() => PlainRuleMemory.Apply(m,m.Identity,before,[c])); Assert.Equal(0,m.Writes);
    }
    [Fact]
    public void NewStorageRefusesRecoveryAtTheOldAddress()
    {
        var m=new PlainRuleFixture(); var before=m.Snapshot(); m.OnWrite=_ => {m.Array+=0x100000; m.Load();};
        Assert.Throws<AggregateException>(() => PlainRuleMemory.Apply(m,m.Identity,before,[new(new("rank",1,"capacity"),18000,20000)]));
        Assert.Equal(1,m.Writes); Assert.Equal(18000,m.Snapshot().Values[new("rank",1,"capacity")]);
    }
    [Fact]
    public void BaselineExtensionPreservesPriorSettingsAndKeepsExactRawGrade()
    {
        var m=new PlainRuleFixture(); var s=m.Snapshot(); m.Set(s.Record(1).Address+0xAC,[255]); s=m.Snapshot();
        var old=m.Offices.Profile() with {AutoApply=false}; old=old.WithChanges([new(new("formation",1,"mobility"),10,16)]);
        var p=old.AddPlain(s); Assert.Equal(old.Defaults,p.Defaults.Where(d => d.Key.Table!="rank")); Assert.Equal(old.Overrides,p.Overrides); Assert.False(p.AutoApply);
        Assert.True(p.SameAs(p.AddPlain(s))); var c=new RuleChange(new("rank",1,"grade"),255,1);
        Assert.Throws<InvalidDataException>(() => p.WithChanges([c with {After=0}])); Assert.Throws<InvalidDataException>(() => p.WithChanges([c with {After=10}]));
        var edited=PlainRuleMemory.Apply(m,m.Identity,s,[c]); var restored=PlainRuleMemory.Apply(m,m.Identity,edited,[c with {Before=1,After=255}]);
        Assert.Equal(255,restored.Values[c.Key]); Assert.DoesNotContain(p.WithChanges([c]).WithChanges([c],true).Overrides,o => o.Key.Table=="rank");
    }
    [Fact]
    public void FileRaceRestoresMemoryWithoutOverwritingExternalProfile()
    {
        var m=new PlainRuleFixture(); var s=m.Snapshot(); var c=new RuleChange(new("rank",1,"capacity"),18000,20000); var p=m.Profile();
        string folder=Path.Combine(Path.GetTempPath(),"RankRuleTests",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var file=new RuleProfileFile(Path.Combine(folder,"rules.json")); var kept=file.Save(p,""); PlainRuleSnapshot? after=null;
        try {
            Assert.Throws<InvalidOperationException>(() => file.Commit(p.WithChanges([c]),kept.Stamp,
                () => {after=PlainRuleMemory.Apply(m,m.Identity,s,[c]); file.Save(p with {AutoApply=false},kept.Stamp);},
                () => PlainRuleMemory.Apply(m,m.Identity,after!,[c with {Before=c.After,After=c.Before}])));
            Assert.Equal(c.Before,m.Snapshot().Values[c.Key]); Assert.False(file.Read().Profile!.AutoApply);
        } finally {Directory.Delete(folder,true);}
    }
}

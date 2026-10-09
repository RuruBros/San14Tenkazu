namespace San14Tenkazu.Tests;

public class OfficeRuleTests
{
    private static RuleChange Change(OfficeSnapshot s, string field, int value, int id = 1) =>
        new(new("office", id, field), s.Record(id).Value(OfficeRules.Field(field)), value);
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WritesAllSixFieldsPreservingOtherRecordAndVectorBytes(bool encoded)
    {
        var m = new OfficeFixture(encoded); var s = m.Snapshot(); var r = s.Record(1);
        RuleChange[] changes = [Change(s,"capacity",65535),Change(s,"leadership",255),Change(s,"strength",0),Change(s,"intelligence",120),Change(s,"politics",40),Change(s,"grade",9)];
        var after = OfficeMemory.Apply(m,m.Identity,s,changes); var changed = after.Record(1);
        Assert.All(changes,c => Assert.Equal(c.After,after.Values[c.Key])); Assert.True(changed.MirrorsAgree);
        for (int i=0;i<r.Data.Length;++i) if (!OfficeRules.Fields.Any(f => i>=f.Plain && i<f.Plain+f.Width)) Assert.Equal(r.Data[i],changed.Data[i]);
        if (encoded) for (int i=0;i<20;++i) if (!OfficeRules.Fields.Any(f => f.Vector>=0 && i>=f.Vector && i<f.Vector+f.Width)) Assert.Equal(r.Vector[i],changed.Vector[i]);
        var restored = OfficeMemory.Apply(m,m.Identity,after,changes.Select(c => c with { Before=c.After,After=c.Before }).ToArray());
        Assert.Equal(r.Data,restored.Record(1).Data); Assert.Equal(r.Vector,restored.Record(1).Vector);
    }
    [Fact]
    public void GradeOnlyDoesNotWriteEncodedVector()
    {
        var m = new OfficeFixture(); var s = m.Snapshot();
        OfficeMemory.Apply(m,m.Identity,s,[Change(s,"grade",1)]); Assert.Equal(1,m.Writes);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PartialWritesRestoreBothMirrors(int fail)
    {
        var m = new OfficeFixture { FailWrite=fail }; var s=m.Snapshot();
        Assert.ThrowsAny<IOException>(() => OfficeMemory.Apply(m,m.Identity,s,[Change(s,"capacity",65535),Change(s,"grade",1)]));
        Assert.Equal(s.Record(1).Data,m.Snapshot().Record(1).Data); Assert.Equal(s.Record(1).Vector,m.Snapshot().Record(1).Vector);
    }
    [Fact]
    public void ChangedMirrorAndDuplicateOrOverflowingChangesAreRejected()
    {
        var m = new OfficeFixture(); var s=m.Snapshot(); var c=Change(s,"capacity",65535);
        Assert.Throws<InvalidDataException>(() => OfficeMemory.Apply(m,m.Identity,s,[c,c]));
        Assert.Throws<InvalidDataException>(() => OfficeMemory.Apply(m,m.Identity,s,[c with {After=65536}]));
        m.Set(s.Record(1).VectorAddress,[0,0]);
        Assert.Throws<InvalidOperationException>(() => OfficeMemory.Apply(m,m.Identity,s,[Change(s,"grade",1)])); Assert.Equal(0,m.Writes);
    }
    [Fact]
    public void PartiallyConstructedVectorAndReplacedStorageAreRejected()
    {
        var m = new OfficeFixture(); var s=m.Snapshot();
        m.Set(s.Record(1).Address+0x48,BitConverter.GetBytes(s.Record(1).VectorAddress+19));
        Assert.Throws<InvalidDataException>(() => m.Snapshot()); m.Load();
        m.OnWrite=_ => { m.Array+=0x100000; m.Vectors+=0x100000; m.Load(); };
        Assert.Throws<AggregateException>(() => OfficeMemory.Apply(m,m.Identity,s,[Change(s,"capacity",20000)]));
        Assert.Equal(1,m.Writes); Assert.Equal(18000,m.Snapshot().Values[new("office",1,"capacity")]);
    }
    [Fact]
    public void AddsOfficeBaselinesWithoutReplacingEarlierSettings()
    {
        var m = new OfficeFixture(); var old=m.Policies.Profile() with { AutoApply=false };
        old=old.WithChanges([new(new("formation",1,"mobility"),10,16),new(new("tactic",1,"amount1"),10,50),new(new("policyeffect",1,"level1"),2,65535)]);
        var next=old.AddOffices(m.Snapshot()); next.Validate();
        Assert.Equal(old.Defaults,next.Defaults.Where(d => d.Key.Table!="office")); Assert.Equal(old.Overrides,next.Overrides); Assert.False(next.AutoApply);
        Assert.True(next.SameAs(next.AddOffices(m.Snapshot()))); Assert.Contains(next.Defaults,d => d.Key.Record==73 && d.Key.Table=="office");
    }
    [Fact]
    public void ExcludesUnusedOfficesButKeepsTheNamedNoneOffice()
    {
        var m = new OfficeFixture();
        m.Set(m.Array + 255UL * 0x58 + 0x10, System.Text.Encoding.Unicode.GetBytes("무효"));
        var snapshot = m.Snapshot();
        Assert.Equal(new[] { 1, 2, 73 }, snapshot.Records.Select(r => r.Id));
        Assert.DoesNotContain(m.Profile().Defaults, d => d.Key.Table == "office" && d.Key.Record == 255);
        Assert.Equal(0, m.Writes);
    }
    [Fact]
    public void KeepsExactBaselineButRestrictsNewGradeOverrides()
    {
        var m=new OfficeFixture(); var s=m.Snapshot(); m.Set(s.Record(1).Address+0x37,[255]); s=m.Snapshot(); var p=m.Policies.Profile().AddOffices(s);
        Assert.Equal(255,p.Default(new("office",1,"grade"))); var c=Change(s,"grade",1);
        var edited=p.WithChanges([c]); Assert.Single(edited.Reapply(s.Values).Apply);
        Assert.Throws<InvalidDataException>(() => p.WithChanges([c with {After=0}])); Assert.Throws<InvalidDataException>(() => p.WithChanges([c with {After=10}]));
        var after=OfficeMemory.Apply(m,m.Identity,s,[c]);
        var restored=OfficeMemory.Apply(m,m.Identity,after,[c with {Before=1,After=255}]);
        Assert.Equal(255,restored.Record(1).Value(OfficeRules.Field("grade"))); Assert.Empty(edited.WithChanges([c],restoreDefaults:true).Overrides);
        var conflict=s.Values; conflict[c.Key]=5; Assert.Single(edited.Reapply(conflict).Conflicts);
    }
    [Fact]
    public void FileRaceRestoresOfficeMemoryAndPreservesExternalProfile()
    {
        var m=new OfficeFixture(); var s=m.Snapshot(); var c=Change(s,"capacity",20000);
        string folder=Path.Combine(Path.GetTempPath(),"OfficeRuleTests",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var file=new RuleProfileFile(Path.Combine(folder,"rules.json")); var p=m.Profile(); var kept=file.Save(p,""); OfficeSnapshot? written=null;
        try {
            Assert.Throws<InvalidOperationException>(() => file.Commit(p.WithChanges([c]),kept.Stamp,
                () => { written=OfficeMemory.Apply(m,m.Identity,s,[c]); file.Save(p with {AutoApply=false},kept.Stamp); },
                () => OfficeMemory.Apply(m,m.Identity,written!,[c with {Before=c.After,After=c.Before}])));
            Assert.Equal(c.Before,m.Snapshot().Values[c.Key]); Assert.False(file.Read().Profile!.AutoApply);
        } finally { Directory.Delete(folder,recursive:true); }
    }
}

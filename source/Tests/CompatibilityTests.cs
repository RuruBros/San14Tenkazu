using System.Text;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class CompatibilityTests
{
    private static GameIdentity Identity => new(1,DateTime.UnixEpoch,0x140000000,GameBuildFacts.ExeSha256,true,TestGame.Classes with {DoctrinePanel=0x7015});
    [Fact]
    public void MissingOptionalClassBlocksOnlyDependentFeaturesAndNeverCertifiesExecution()
    {
        var identity=Identity with{Classes=Identity.Classes with{Formation=0}};
        Assert.True(GameCapabilities.Inspect(identity,GameFeature.BasicRead).CanAttempt);
        Assert.False(GameCapabilities.Inspect(identity,GameFeature.RuleTables).CanAttempt);
        Assert.True(GameCapabilities.Inspect(identity,GameFeature.Environment).CanAttempt);
        Assert.All(GameCapabilities.Inspect(identity),c=>Assert.NotEqual(CapabilityState.Passed,c.State));
        foreach(var altered in new[]{identity with{Verified=false},identity with{ExeHash=new string('A',64)}})
        {
            Assert.True(GameCapabilities.Inspect(altered,GameFeature.BasicRead).CanAttempt);
            Assert.False(GameCapabilities.Inspect(altered,GameFeature.Seasons).CanAttempt);
            Assert.Throws<InvalidDataException>(()=>GameCapabilities.Require(altered,GameFeature.RuntimeAbilities));
        }
    }
    [Fact]
    public void EvidenceCannotPromoteBlockedFeaturesOrClaimAnUntestedNativeSession()
    {
        var eligibility=GameCapabilities.Inspect(Identity,GameFeature.Seasons);
        CapabilityEvidence[] reads=[new("storage",true,"stable"),new("engine-code",true,"exact"),new("engine-protection",true,"exact"),new("bundle",true,"pinned")];
        Assert.Equal(CapabilityState.Eligible,GameCapabilities.Record(eligibility,reads).State);
        Assert.Equal(CapabilityState.Passed,GameCapabilities.Record(eligibility,[..reads,new("native-session",true,"fixture")]).State);
        Assert.Equal(CapabilityState.Blocked,GameCapabilities.Record(eligibility,[..reads,new("native-session",false,"refused")]).State);
        var blocked=GameCapabilities.Inspect(Identity with{Verified=false},GameFeature.Seasons);
        Assert.Equal(CapabilityState.Blocked,GameCapabilities.Record(blocked,[..reads,new("native-session",true,"fixture")]).State);
        Assert.Throws<InvalidDataException>(()=>GameCapabilities.Record(eligibility,new("storage",true,"one"),new("storage",true,"two")));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(10)]
    public void UnknownCurveNeverPassesEmptyOrSmallPopulationTolerance(int count)
    {
        var fixture=new TestGame();for(int i=1;i<=count;i++)fixture.Officer(i,"장수"+i,0,1,5);
        var data=fixture.Build();
        var unknown=new GameSnapshot{Cities=data.Cities,Counties=data.Counties,Officers=data.Officers,CastleByCity=data.CastleByCity,
            TileOwners=data.TileOwners,TileCounties=data.TileCounties,Forces=data.Forces,CorpsForce=data.CorpsForce,OfficerById=data.OfficerById,Units=data.Units,RuntimeRules=null};
        var checks=GameChecks.Run(unknown,GameCatalog.Empty);
        Assert.Contains(checks,c=>c.Group==GameChecks.Abilities&&!c.EvidenceKnown&&!c.Passed);
        Assert.NotNull(GameChecks.WriteBlock(true,true,checks,[GameChecks.Abilities]));
        Assert.NotNull(GameChecks.WriteBlock(false,true,checks,[GameChecks.Abilities]));
        Assert.Null(GameChecks.WriteBlock(true,true,checks,[GameChecks.Years]));
        Assert.True(new GameChecks.Check("known","normal existing tolerance",0,2,1).Passed);
        Assert.NotNull(GameChecks.WriteBlock(false,true,[],["missing-group"]));
    }
    [Theory]
    [InlineData("missing")] [InlineData("descriptor-ambiguous")] [InlineData("vtable-ambiguous")]
    public void RttiDiscoveryLeavesOptionalUnknownClassesZeroWithoutWeakeningStrictMode(string failure)
    {
        var bytes=Pe(failure,false);
        var found=GameClasses.Find(bytes,0x140000000,requireAll:false);
        Assert.NotEqual(0UL,found.City);Assert.Equal(0UL,found.Unit);
        Assert.Throws<InvalidOperationException>(()=>GameClasses.Find(bytes,0x140000000));
        Assert.Throws<InvalidOperationException>(()=>GameClasses.Find(Pe(failure,true),0x140000000,requireAll:false));
    }
    // Independently constructed PE/RTTI image. It never reads an installed game or live process.
    private static byte[] Pe(string failure,bool required)
    {
        byte[] bytes=new byte[0x3000];void U16(int at,ushort n)=>BitConverter.GetBytes(n).CopyTo(bytes,at);
        void U32(int at,uint n)=>BitConverter.GetBytes(n).CopyTo(bytes,at);void U64(int at,ulong n)=>BitConverter.GetBytes(n).CopyTo(bytes,at);
        U32(0x3c,0x80);U32(0x80,0x4550);U16(0x86,1);U16(0x94,0xf0);U16(0x98,0x20b);U64(0xb0,0x140000000);
        Encoding.ASCII.GetBytes(".rdata").CopyTo(bytes,0x188);U32(0x194,0x1000);U32(0x198,0x2000);U32(0x19c,0x400);
        string[] names=["CCityData","CAreaData","CPersonData","CHexData","CObjectData","CForceData","CDistrictData","CArmyUnitData"];
        for(int i=0;i<names.Length;i++)
        {
            bool broken=i==(required?0:7);if(broken&&failure=="missing")continue;
            int descriptor=0x500+i*0x100,locator=descriptor+0x50,slot=descriptor+0x80;
            uint Rva(int at)=>(uint)(at-0x400+0x1000);
            Encoding.ASCII.GetBytes(".?AV"+names[i]+"@@\0").CopyTo(bytes,descriptor+0x10);
            U32(locator,1);U32(locator+12,Rva(descriptor));U32(locator+20,Rva(locator));U64(slot,0x140000000UL+Rva(locator));
            if(broken&&failure=="descriptor-ambiguous")Encoding.ASCII.GetBytes(".?AV"+names[i]+"@@\0").CopyTo(bytes,descriptor+0xb0);
            if(broken&&failure=="vtable-ambiguous")U64(slot+8,0x140000000UL+Rva(locator));
        }
        return bytes;
    }
}

public sealed class SettingsMigrationTests
{
    private const string Next="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static SettingsMigrationPolicy Policy(bool route=true,string schema="runtime-settings-v1")=>new(
        [new(GameBuildFacts.ExeSha256,"runtime-settings-v1",RuleProfile.CurrentSchema),new(Next,schema,RuleProfile.CurrentSchema)],
        route?[new(GameBuildFacts.ExeSha256,Next,true,true)]:[]);
    private static RuleProfile Baseline()=>new FormationFixture().Snapshot().Baseline(GameBuildFacts.ExeSha256);
    [Fact]
    public void CrossBuildMigrationRequiresReviewedDirectionalRouteAndIdenticalSemanticContract()
    {
        var source=new RuntimeRulePreference(1,GameBuildFacts.ExeSha256,new(true,AbilityRuleMode.Growth200,true),true);
        Assert.Throws<InvalidDataException>(()=>SettingsMigration.Runtime(source,Next));
        Assert.Throws<InvalidDataException>(()=>SettingsMigration.Runtime(source,Next,Policy(false)));
        Assert.Throws<InvalidDataException>(()=>SettingsMigration.Runtime(source,Next,Policy(schema:"different")));
        var migrated=SettingsMigration.Runtime(source,Next,Policy());
        Assert.Equal(source.Settings,migrated.Settings);Assert.Equal(Next,migrated.Build);
        Assert.Throws<InvalidDataException>(()=>SettingsMigration.Runtime(migrated,source.Build,Policy()));
        Assert.Equal(source,SettingsMigration.Runtime(source,source.Build));
    }
    [Fact]
    public void RuleImportKeepsTargetDefaultsAndDisablesAutoApply()
    {
        var baseline=Baseline();var key=baseline.Defaults[0].Key;
        var source=baseline.WithChanges([new(key,baseline.Default(key),20)]);
        var target=baseline with{Build=Next,Defaults=baseline.Defaults.Select(v=>v.Key==key?v with{Value=12}:v).ToArray()};
        var result=SettingsMigration.Rules(source,target,Policy());
        Assert.Equal(12,result.Default(key));Assert.Equal(20,result.Desired(key));Assert.False(result.AutoApply);
        Assert.Equal(10,baseline.Default(key));
        Assert.Throws<InvalidDataException>(()=>SettingsMigration.Rules(source,target));
        var changed=target with{Catalogs=target.Catalogs!.Select(c=>c with{Fingerprint=new string('B',64)}).ToArray(),Catalog=new string('B',64)};
        Assert.Throws<InvalidDataException>(()=>SettingsMigration.Rules(source,changed,Policy()));
        Assert.Throws<InvalidDataException>(()=>SettingsMigration.Rules(source with{Overrides=[new(key,999)]},target,Policy()));
    }
    [Fact]
    public void GuardedImportPreservesExactOriginalBytesAndRefusesSourceOrTargetRaces()
    {
        string directory=Path.Combine(Path.GetTempPath(),"migration-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var baseline=Baseline();var key=baseline.Defaults[0].Key;
            string sourcePath=Path.Combine(directory,"source.json"),targetPath=Path.Combine(directory,"target.json");
            byte[] original=JsonSerializer.SerializeToUtf8Bytes(baseline.WithChanges([new(key,10,20)]));File.WriteAllBytes(sourcePath,original);
            var source=SettingsImportSource.Read(sourcePath);var file=new RuleProfileFile(targetPath);var kept=file.Save(baseline,"");byte[] before=File.ReadAllBytes(targetPath);
            var imported=file.Import(source,baseline,kept.Stamp);
            Assert.Equal(20,imported.Profile!.Desired(key));Assert.False(imported.Profile.AutoApply);Assert.Equal(original,File.ReadAllBytes(sourcePath));
            var history=Directory.GetFiles(Path.Combine(directory,"settings-history"));
            Assert.Contains(history,p=>File.ReadAllBytes(p).AsSpan().SequenceEqual(original));Assert.Contains(history,p=>File.ReadAllBytes(p).AsSpan().SequenceEqual(before));
            Assert.Throws<IOException>(()=>file.Import(source,baseline,kept.Stamp));
            Assert.Throws<InvalidDataException>(()=>file.Import(SettingsImportSource.Read(targetPath),imported.Profile,imported.Stamp));
            File.WriteAllText(sourcePath,"{}");Assert.Throws<IOException>(()=>source.Guard());
            File.WriteAllText(sourcePath,"{\"Writes\":[],\"Phase\":\"committed\"}");
            Assert.Throws<InvalidDataException>(()=>SettingsImportSource.Read(sourcePath).Parse<RuntimeRulePreference>());
            Assert.Equal(imported.Stamp,file.Read().Stamp);
        }
        finally{Directory.Delete(directory,true);}
    }
    [Fact]
    public void RuntimeSettingsCasAndArchiveDoNotCopyProcessReceipts()
    {
        string directory=Path.Combine(Path.GetTempPath(),"migration-"+Guid.NewGuid().ToString("N"));
        try
        {
            var file=new RuntimeRulePreferenceFile(directory);var source=new RuntimeRulePreference(1,GameBuildFacts.ExeSha256,RuntimeRuleSettings.Original,false);
            file.Save(source);string stamp=file.Stamp();byte[] original=File.ReadAllBytes(file.PathName);
            var next=source with{Settings=new(true,AbilityRuleMode.Cap200)};file.Save(next,true,stamp);
            Assert.Throws<IOException>(()=>file.Save(source,true,stamp));Assert.Equal(next,file.Read());
            Assert.Equal(original,File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.Combine(directory,"settings-history")))));
            Assert.Single(Directory.GetFiles(directory));Assert.Empty(Directory.GetFiles(directory,"*.tmp"));
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}

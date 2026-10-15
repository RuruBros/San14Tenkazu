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

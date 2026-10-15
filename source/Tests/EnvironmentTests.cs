using System.Runtime.InteropServices;
using San14Tenkazu.EnvironmentRules;
namespace San14Tenkazu.Tests;

public class EnvironmentTests
{
    private static EnvironmentProtocol.Reply Reply(EnvironmentEffects flags=EnvironmentEffects.RainMovement)=>new(){Magic=EnvironmentProtocol.Magic,
        Version=EnvironmentProtocol.Version,Size=58552,Generation=7,Sequence=1,Applied=1,Date=192|(4u<<16)|(21u<<24),Flags=(uint)flags,Weather=new byte[46],Events=new EnvironmentProtocol.Event[1024],RecentDates=new uint[230]};
    private sealed class Session:IEnvironmentSession
    {
        internal TaskCompletionSource<EnvironmentProtocol.Reply>? Pending;
        internal EnvironmentProtocol.Reply Next=Reply();internal int Stops;internal bool RecoveryFails;
        public ValueTask<EnvironmentProtocol.Reply> ApplyAsync(EnvironmentSettings settings,CancellationToken token)=>Pending is {} pending?new(pending.Task):ValueTask.FromResult(Next);
        public ValueTask<EnvironmentProtocol.Reply> QueryAsync(CancellationToken token)=>ValueTask.FromResult(Next);
        public ValueTask StopAsync(){Stops++;if(RecoveryFails)throw new IOException("Recovery unconfirmed");return ValueTask.CompletedTask;}
    }
    private sealed class Backend(Session value):IEnvironmentBackend{internal int Starts;public ValueTask<IEnvironmentSession> StartAsync(CancellationToken token){Starts++;return ValueTask.FromResult<IEnvironmentSession>(value);}}
    [Fact]
    public void ProtocolHasNativeLayoutAndPreservesEveryEvent()
    {
        Assert.Equal(376,Marshal.SizeOf<EnvironmentProtocol.Bootstrap>());Assert.Equal(2352,Marshal.SizeOf<EnvironmentProtocol.Message>());
        Assert.Equal(40,Marshal.SizeOf<EnvironmentProtocol.Recovery>());Assert.Equal(56,Marshal.SizeOf<EnvironmentProtocol.Event>());Assert.Equal(58552,Marshal.SizeOf<EnvironmentProtocol.Reply>());
        Assert.Equal(92,Marshal.SizeOf<EnvironmentValues>());
        var value=Reply();value.Events[1023]=new(){Id=1024,Commander=42,FoodBefore=5000,FoodAfter=4500};
        value.RecentDates[229]=value.Date;
        value.Values=new(7,31,13,49,3,17,11,9,27,43,19,8,23,61,37);
        var round=EnvironmentProtocol.Read<EnvironmentProtocol.Reply>(EnvironmentProtocol.Bytes(value));Assert.Equal(1024ul,round.Events[1023].Id);Assert.Equal(4500,round.Events[1023].FoodAfter);
        Assert.Equal(new GameCalendarDate(192,4,21),EnvironmentProtocol.Date(round.Date));
        Assert.Equal(value.Values,round.Values);
        Assert.Equal(value.Date,round.RecentDates[229]);
    }
    [Theory]
    [InlineData(1,false,false,-1)]
    [InlineData(5,true,false,-1)]
    [InlineData(1029,true,false,3)]
    [InlineData(15877,true,false,61)]
    [InlineData(9,false,true,-1)]
    public void DiagnosticSoundReceiptDistinguishesQueueCallAndAcceptedChannel(int reserved,bool attempted,bool missing,int channel)
    {
        var value=Reply();value.Reserved=(uint)reserved;var round=EnvironmentProtocol.Read<EnvironmentProtocol.Reply>(EnvironmentProtocol.Bytes(value));
        Assert.Equal(attempted,round.DiagnosticSoundAttempted);Assert.Equal(missing,round.DiagnosticAudioMissing);Assert.Equal(channel,round.DiagnosticSoundChannel);
    }
    [Theory]
    [InlineData(0x65,0,1)]
    [InlineData(0x6d,7,2)]
    [InlineData(0x5f,3,3)]
    [InlineData(0x60,-1,4)]
    [InlineData(0x77,0,7)]
    public void DisasterSoundReceiptPreservesOriginalIdOwnershipStateAndChannelZero(int id,int channel,int state)
    {
        var value=Reply();value.Reserved=1u|4u|((uint)(channel+1)<<8)|((uint)(id+1)<<16)|((uint)state<<24);
        var round=EnvironmentProtocol.Read<EnvironmentProtocol.Reply>(EnvironmentProtocol.Bytes(value));
        Assert.Equal(id,round.DiagnosticSoundId);Assert.Equal(state,round.DiagnosticSoundState);Assert.Equal(channel,round.DiagnosticSoundChannel);
        Assert.True(round.DiagnosticSoundAttempted);Assert.False(round.DiagnosticAudioMissing);
    }
    [Fact]
    public void OverlayUsesActualReceiptAndExpiresRecentDamageWithoutRollingDisasters()
    {
        var reply=Reply(EnvironmentEffects.All);reply.Weather[0]=2;reply.Weather[1]=8;reply.RecentDates[0]=192|(4u<<16)|(12u<<24);
        var value=EnvironmentOverlay.From(new(EnvironmentPhase.On,EnvironmentEffects.All,EnvironmentEffects.All,false,reply))!;
        Assert.Equal(1,value.WeatherKind(1));Assert.Equal(2,value.WeatherKind(2));Assert.Equal(3,value.Shade(1));Assert.Equal(2,value.Marks(1).Count());
        Assert.Equal(0u,(value with{Date=192|(4u<<16)|(22u<<24)}).Recent(1,3));
        Assert.Equal(0u,(value with{Date=192|(4u<<16)|(11u<<24)}).Recent(1,3));
        Assert.Equal(0u,(value with{Flags=EnvironmentEffects.RainMovement}).Recent(1,3));
        Assert.Null(EnvironmentOverlay.From(new(EnvironmentPhase.Off,EnvironmentEffects.None,EnvironmentEffects.None,true,reply)));
    }
    [Fact]
    public void SavingCombinationsIsAtomicAndRejectsConcurrentChanges()
    {
        string root=Path.Combine(Path.GetTempPath(),"san14-environment-"+Guid.NewGuid().ToString("N"));var file=new EnvironmentSettingsFile(Path.Combine(root,"settings.json"));
        try
        {
            Assert.Null(file.Read().Settings);var value=EnvironmentSettings.New() with{Seasonal=true,Effects=EnvironmentEffects.Flood|EnvironmentEffects.SnowMovement,Values=EnvironmentValues.Default with{RainReduction=17,HeavyRainReduction=41,FloodChance=17,FloodTroopsPercent=13,WildfireFoodPercent=29}};
            string stamp=file.Save(value,"");Assert.Equal(value,file.Read().Settings);Assert.Equal(stamp,file.Read().Stamp);
            var next=value with{Effects=EnvironmentEffects.Drought};file.Save(next,stamp);Assert.Throws<IOException>(()=>file.Save(value,stamp));Assert.Equal(next,file.Read().Settings);
            Assert.Throws<InvalidDataException>(()=>(value with{Seed=0}).Validate());Assert.Throws<InvalidDataException>(()=>(value with{Effects=(EnvironmentEffects)1024}).Validate());
            Assert.Throws<InvalidDataException>(()=>(value with{Values=value.Values with{RainReduction=100}}).Validate());Assert.Throws<InvalidDataException>(()=>(value with{Values=value.Values with{FloodChance=1001}}).Validate());
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]
    public void ExistingVersionThreeSettingsDefaultSeasonalOffWithoutRewritingTheFile()
    {
        string root=Path.Combine(Path.GetTempPath(),"san14-env-seasonal-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var value=EnvironmentSettings.New() with{Effects=EnvironmentEffects.RainVisual};
            var node=System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(value))!.AsObject();node.Remove("Seasonal");
            string path=Path.Combine(root,"settings.json"),text=node.ToJsonString();File.WriteAllText(path,text);
            var file=new EnvironmentSettingsFile(path);var read=file.Read();
            Assert.Equal(value,read.Settings);Assert.False(read.Settings!.Seasonal);Assert.Equal(text,File.ReadAllText(path));
            var selected=value with{Seasonal=true};file.Save(selected,read.Stamp);Assert.Equal(selected,file.Read().Settings);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact]
    public void LegacyPresetsMigrateToExactNumbersWithoutChangingTheFileUntilSave()
    {
        string root=Path.Combine(Path.GetTempPath(),"san14-environment-migration-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string path=Path.Combine(root,"settings.json");var file=new EnvironmentSettingsFile(path);
        try
        {
            for(int strength=0;strength<3;strength++)for(int frequency=0;frequency<3;frequency++){
                string text=$"{{\"Version\":1,\"Effects\":63,\"Strength\":{strength},\"Frequency\":{frequency},\"Seed\":123}}";File.WriteAllText(path,text);
                var read=file.Read();var value=read.Settings!;Assert.Equal(text,File.ReadAllText(path));Assert.Equal(3,value.Version);Assert.Equal(123ul,value.Seed);
                Assert.Equal(new[]{5,10,20}[strength],value.Values.RainReduction);Assert.Equal(new[]{10,20,40}[strength],value.Values.HeavyRainReduction);
                Assert.Equal(new[]{20,40,60}[strength],value.Values.DeepSnowReduction);Assert.Equal(new[]{10,30,60}[frequency],value.Values.FloodChance);
                file.Save(value,read.Stamp);Assert.Equal(value,file.Read().Settings);
            }
        }finally{Directory.Delete(root,true);}
    }
    [Fact]
    public void VersionTwoPreservesExistingChoicesAndValuesWhileNewDisastersRemainOff()
    {
        string root=Path.Combine(Path.GetTempPath(),"san14-env-v2-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            var previous=EnvironmentSettings.New() with{Version=2,Effects=EnvironmentEffects.Flood|EnvironmentEffects.RainVisual,Values=EnvironmentValues.Default with{FloodChance=17,WildfireFoodPercent=29}};
            var node=System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(previous))!;
            var values=node["Values"]!.AsObject();foreach(string key in values.Select(v=>v.Key).Where(k=>k.StartsWith("Blizzard")||k.StartsWith("Heatwave")).ToArray())values.Remove(key);
            string path=Path.Combine(root,"settings.json"),text=node.ToJsonString();File.WriteAllText(path,text);
            var file=new EnvironmentSettingsFile(path);var loaded=file.Read();var current=loaded.Settings!;
            Assert.Equal(3,current.Version);Assert.Equal(previous.Effects,current.Effects);Assert.Equal(previous.Values,current.Values);Assert.Equal(previous.Seed,current.Seed);
            Assert.Equal(EnvironmentEffects.None,current.Effects&(EnvironmentEffects.Blizzard|EnvironmentEffects.Heatwave));Assert.Equal(text,File.ReadAllText(path));
            file.Save(current,loaded.Stamp);Assert.Equal(current,file.Read().Settings);
        }finally{Directory.Delete(root,true);}
    }
    [Fact]
    public void NewDisasterReceiptsKeepTheirOwnFlagsAndDatesInTheLastRegion()
    {
        var reply=Reply(EnvironmentEffects.Blizzard|EnvironmentEffects.Heatwave);reply.RecentDates[228]=reply.Date;reply.RecentDates[229]=reply.Date;
        var value=EnvironmentOverlay.From(new(EnvironmentPhase.On,(EnvironmentEffects)reply.Flags,(EnvironmentEffects)reply.Flags,false,reply))!;
        Assert.Equal(new[]{6,7},value.Marks(46).Select(v=>v.Kind));Assert.Equal(reply.Date,value.Recent(46,7));
        Assert.Equal(0u,(value with{Flags=EnvironmentEffects.Blizzard}).Recent(46,7));Assert.Equal(0u,(value with{Flags=EnvironmentEffects.RainVisual|EnvironmentEffects.SnowVisual}).Recent(46,6));
        Assert.Throws<InvalidDataException>(()=>(EnvironmentValues.Default with{BlizzardChance=1001}).Validate());
        Assert.Throws<InvalidDataException>(()=>(EnvironmentValues.Default with{HeatwaveTroopsPercent=100}).Validate());
    }
    [Fact]
    public void AuthenticatedEventArtRejectsChangedPixelsAndTruncatedPayload()
    {
        byte[] data=EnvironmentArtData.Read();Assert.Equal(16777248,data.Length);EnvironmentArtData.Validate(data);
        data[32+3*1024*1024*4+128]^=1;Assert.Throws<InvalidDataException>(()=>EnvironmentArtData.Validate(data));
        Assert.Throws<InvalidDataException>(()=>EnvironmentArtData.Validate(data.AsSpan(0,data.Length-1)));
    }
    [Fact]
    public async Task RequestedStateIsNotActualUntilTheFrameReceipt()
    {
        var session=new Session{Pending=new(TaskCreationOptions.RunContinuationsAsynchronously)};var backend=new Backend(session);
        await using var controller=new EnvironmentController(backend,_=>{});
        var task=controller.ApplyAsync(EnvironmentSettings.New() with{Effects=EnvironmentEffects.RainMovement});
        Assert.Equal(EnvironmentPhase.Applying,controller.Status.Phase);Assert.Equal(EnvironmentEffects.None,controller.Status.Actual);
        session.Pending.SetResult(Reply());await task;Assert.Equal(EnvironmentPhase.On,controller.Status.Phase);Assert.Equal(EnvironmentEffects.RainMovement,controller.Status.Actual);
        await controller.ApplyAsync(EnvironmentSettings.New());Assert.Equal(1,backend.Starts);Assert.True(controller.Status.Restored);Assert.Equal(EnvironmentPhase.Off,controller.Status.Phase);
    }
    [Fact]
    public async Task RecoveryFailureRetainsTheSessionForRetry()
    {
        var session=new Session();await using var controller=new EnvironmentController(new Backend(session),_=>{});
        await controller.ApplyAsync(EnvironmentSettings.New() with{Effects=EnvironmentEffects.RainMovement});session.RecoveryFails=true;
        await Assert.ThrowsAsync<IOException>(()=>controller.StopAsync());Assert.False(controller.Status.Restored);Assert.Equal(EnvironmentPhase.Faulted,controller.Status.Phase);
        session.RecoveryFails=false;await controller.StopAsync();Assert.True(controller.Status.Restored);Assert.Equal(2,session.Stops);
    }
    [Fact]
    public async Task IncompleteJournalFailsAndStopsInsteadOfInventingMissingEvents()
    {
        var session=new Session();session.Next.LastEvent=2;session.Next.Events[1]=new(){Id=2};var rows=new List<string>();
        await using var controller=new EnvironmentController(new Backend(session),rows.Add);
        await Assert.ThrowsAsync<InvalidDataException>(()=>controller.ApplyAsync(EnvironmentSettings.New() with{Effects=EnvironmentEffects.RainMovement}));
        Assert.True(controller.Status.Restored);Assert.Equal(1,session.Stops);Assert.Contains(rows,r=>r.Contains("gap"));
    }
    [Theory]
    [InlineData("EnvironmentBridge.dll")][InlineData("build.json")][InlineData("sources/EnvironmentBridge.cpp")]
    public void BundleRejectsModifiedFilesWithoutOverwritingThem(string name)
    {
        string root=Path.Combine(Path.GetTempPath(),"san14-environment-bundle-"+Guid.NewGuid().ToString("N"));
        try
        {
            string directory=EnvironmentNativeBundle.Extract(root);EnvironmentNativeBundle.VerifyDirectory(directory);string path=Path.Combine(directory,name);
            byte[] bytes=File.ReadAllBytes(path);bytes[0]^=1;File.WriteAllBytes(path,bytes);
            Assert.Throws<InvalidDataException>(()=>EnvironmentNativeBundle.VerifyDirectory(directory));Assert.Throws<InvalidDataException>(()=>EnvironmentNativeBundle.Extract(root));Assert.Equal(bytes,File.ReadAllBytes(path));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}

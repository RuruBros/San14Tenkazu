#if LOCAL_DEVELOPER
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.EnvironmentRules;
using San14Tenkazu.Seasons;
using San14Tenkazu.Tests;
internal static partial class RenderChecks
{
    private static void CheckDeveloperNoticeDiagnostic()
    {
        string directory=Path.Combine(Path.GetTempPath(),"San14Tenkazu-notice-evidence-"+Guid.NewGuid().ToString("N"));
        var identity=new GameIdentity(111,DateTime.UnixEpoch,0x140000000,"fixture",true,TestGame.Classes);
        var off=new SeasonStatus(SeasonPhase.Off,null,1,true);
        EnvironmentProtocol.Reply receipt=new(){TestId=9,TestMode=1,TestState=2,TestCity=1,TestKind=2};
        var environment=new EnvironmentStatus(EnvironmentPhase.Off,0,0,true,receipt);
        DeveloperNoticeDiagnostic Create()=>new(directory,identity,GeographyFixture.Create(51),1,2);
        void Trace(DeveloperNoticeDiagnostic run,int received,int swaps,int restores)
        {
            ulong[] calls=new ulong[6],changed=new ulong[6],restored=new ulong[6];calls[received]=9;changed[received]=(ulong)swaps;restored[received]=(ulong)restores;
            File.WriteAllText(run.TracePath,JsonSerializer.Serialize(new{stages=new ulong[16],kindCalls=calls,kindSwaps=changed,kindRestores=restored,textures=Array.Empty<object>()}));
        }
        JsonDocument Complete(DeveloperNoticeDiagnostic run,GameSnapshot? after)
        {run.Observe(environment);run.Complete(after,environment,off);return JsonDocument.Parse(File.ReadAllText(run.ResultPath));}
        var valid=Create();Trace(valid,3,4,4);using(var result=Complete(valid,GeographyFixture.Create(51))){
            var r=result.RootElement;Require(r.GetProperty("imageSwapped").GetBoolean()&&r.GetProperty("bindingRestored").GetBoolean()&&
                r.GetProperty("gameplayUnchanged").GetBoolean()&&r.GetProperty("noNewDamageEvent").GetBoolean()&&!r.GetProperty("screenVerified").GetBoolean(),"Actual native swap/restore must remain separate from screen evidence");
            Require(r.GetProperty("receipts")[0].GetProperty("TestKind").GetUInt32()==2,"Internal wire fields must be explicitly projected into diagnostic JSON");
        }
        var timeout=new DeveloperNoticeDiagnostic(directory,identity,GeographyFixture.Create(51),1,2,
            started:System.Diagnostics.Stopwatch.GetTimestamp()-121*System.Diagnostics.Stopwatch.Frequency);
        Require(timeout.DeadlineExpired,"Overall deadline must retain the original click clock");timeout.RecordControllerDeadline();
        using(var result=Complete(timeout,GeographyFixture.Create(51)))Require(result.RootElement.GetProperty("controllerStopReason").GetString()=="OverallDeadline"&&
            result.RootElement.GetProperty("controllerDeadlineElapsedSeconds").GetDouble()>=120,"Controller timeout must be distinct from Native cancel-command receipt");
        var binary=new byte[168+536+128+2*168+48*56];
        void U(int at,uint value)=>System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(at),value);
        void Q(int at,ulong value)=>System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(binary.AsSpan(at),value);
        U(0,0x4e434231);U(4,2);U(8,168);U(12,536);U(16,128);U(20,168);Q(24,67);Q(32,17);Q(40,100);Q(48,1000);U(56,1);U(60,1);U(64,2);
        Q(128,10);Q(136,100);Q(144,100);U(152,2);U(156,48);U(160,56);
        Q(168,1);Q(168+16,110);Q(168+24,120);Q(168+72,17);Q(168+80,67);Q(168+88,100);Q(168+192,100);Q(168+200,100);
        U(168+216,1);U(168+220,1);U(168+264,1);binary[168+268]=1;U(168+524,1);
        U(704+84,2);Convert.FromHexString("3bd4b1d2d18b50ad64613ca80dc06102b52f56877788767d10a618dfa9147bac").CopyTo(binary,704+92);
        for(int i=0;i<2;i++){int at=832+i*168;Q(at,(ulong)(i+1));Q(at+24,17);Q(at+32,67);U(at+72,i==0?3u:12u);}
        Q(1168,1);Q(1168+16,1);Q(1168+24,1);Q(1168+32,2);
        string causalPath=Path.Combine(valid.DirectoryPath,"fixture.causal.bin");
        JsonDocument Parse(){File.WriteAllBytes(causalPath,binary);return JsonDocument.Parse(JsonSerializer.Serialize(DeveloperNoticeCausality.Read(causalPath)));}
        void Reject(string message){File.WriteAllBytes(causalPath,binary);bool rejected=false;try{DeveloperNoticeCausality.Read(causalPath);}catch(InvalidDataException){rejected=true;}Require(rejected,message);}
        using(var parsed=Parse()){
            var v=parsed.RootElement;
            Require(v.GetProperty("authenticatedRows").GetUInt64()==1&&v.GetProperty("recordSetComplete").GetBoolean()&&
                !v.GetProperty("rootCauseConfirmed").GetBoolean()&&!v.GetProperty("drawInputProven").GetBoolean(),"Causal authentication must not manufacture root cause or actual Draw input proof");
            Require(v.GetProperty("gates")[0].GetProperty("rejected").GetUInt64()==1&&v.GetProperty("gates")[1].GetProperty("notEvaluated").GetBoolean()&&
                v.GetProperty("stopReason").GetString()=="CaptureDeadline"&&!v.GetProperty("pictureReached").GetBoolean(),"Preparation rejection and unevaluated gates remain distinct from picture evidence");
        }
        U(68,1);using(var parsed=Parse())Require(!parsed.RootElement.GetProperty("recordSetComplete").GetBoolean(),"Causal overflow must remain incomplete");U(68,0);
        U(168+528,1);using(var parsed=Parse())Require(!parsed.RootElement.GetProperty("recordSetComplete").GetBoolean(),"Capture boundary must remain incomplete");U(168+528,0);
        U(168+220,2);using(var parsed=Parse())Require(parsed.RootElement.GetProperty("orphanRows").GetUInt32()==1&&!parsed.RootElement.GetProperty("recordSetComplete").GetBoolean(),"Return without Prepare must remain incomplete");U(168+220,1);
        U(4,1);Reject("Old causal ABI must be explicitly rejected");U(4,2);
        Q(1168+8,2);Reject("Corrupt preparation counts must reject evidence");Q(1168+8,0);
        binary[704+92]^=1;Reject("Causal accepted hash corruption must reject evidence");binary[704+92]^=1;
        var full=binary;binary=new byte[168+2*168+48*56];full.AsSpan(0,168).CopyTo(binary);full.AsSpan(832,2*168+48*56).CopyTo(binary.AsSpan(168));
        U(56,0);U(60,0);Q(136,0);Q(144,0);U(152,1);U(168+72,2);
        using(var parsed=Parse())Require(parsed.RootElement.GetProperty("preparationOnly").GetBoolean()&&parsed.RootElement.GetProperty("finished").GetBoolean()&&
            !parsed.RootElement.GetProperty("recordSetComplete").GetBoolean()&&parsed.RootElement.GetProperty("stopReason").GetString()=="PreparationDeadline","Preparation-only shutdown retains reasons without fabricating complete Draw evidence");
        var missing=Create();using(var result=Complete(missing,GeographyFixture.Create(51)))
            Require(!result.RootElement.GetProperty("imageSwapped").GetBoolean()&&result.RootElement.GetProperty("traceError").ValueKind==JsonValueKind.String,"Missing trace must never be a successful image substitution");
        var wrong=Create();Trace(wrong,2,4,4);using(var result=Complete(wrong,GeographyFixture.Create(51)))
            Require(!result.RootElement.GetProperty("imageSwapped").GetBoolean(),"Another disaster's successful draws cannot verify the selected drought");
        var unrestored=Create();Trace(unrestored,3,4,3);using(var result=Complete(unrestored,GeographyFixture.Create(51)))
            Require(!result.RootElement.GetProperty("bindingRestored").GetBoolean(),"Unmatched original binding restoration must remain unconfirmed");
        var observed=Create();File.WriteAllText(observed.TracePath,JsonSerializer.Serialize(new{formatVersion=2,stages=new ulong[16],kindCalls=new ulong[6],kindSwaps=new ulong[6],kindRestores=new ulong[6],
            bindings=new[]{new{accepted=true,slot=5,callerRva=0x960b6a,readError=0}},gateDropped=3,candidateBindingDropped=1}));
        using(var result=Complete(observed,GeographyFixture.Create(51)))Require(!result.RootElement.GetProperty("imageSwapped").GetBoolean()&&!result.RootElement.GetProperty("bindingRestored").GetBoolean()&&
            result.RootElement.GetProperty("trace").GetProperty("bindings")[0].GetProperty("accepted").GetBoolean(),"Correlated authentication and overflow are evidence, never an actual substitution");
        var oversized=Create();Trace(oversized,3,4,4);File.AppendAllText(oversized.TracePath,new string(' ',256*1024));
        using(var result=Complete(oversized,GeographyFixture.Create(51)))Require(!result.RootElement.GetProperty("imageSwapped").GetBoolean()&&result.RootElement.GetProperty("traceError").ValueKind==JsonValueKind.String,"Oversized diagnostic evidence must still be rejected");
        var changed=Create();var after=GeographyFixture.Create(51);CityFields.Food.Encode(after.Cities[49][CityFields.Food]+1).CopyTo(after.Cities[49].Data,CityFields.Food.Offset);
        using(var result=Complete(changed,after))Require(!result.RootElement.GetProperty("gameplayUnchanged").GetBoolean(),"A single changed city food value must fail no-damage comparison");
        var eventChanged=Create();eventChanged.Observe(environment);receipt.LastEvent=1;environment=environment with{Receipt=receipt};
        using(var result=Complete(eventChanged,GeographyFixture.Create(51)))Require(!result.RootElement.GetProperty("noNewDamageEvent").GetBoolean(),"A new native damage event must not pass preview verification");
        var closed=Create();closed.Complete(null,null,null,"Game exited",ownerRestored:true);using(var result=JsonDocument.Parse(File.ReadAllText(closed.ResultPath)))
            Require(result.RootElement.GetProperty("environmentRestored").GetBoolean()&&result.RootElement.GetProperty("rendererRestored").GetBoolean()&&!result.RootElement.GetProperty("gameplayUnchanged").GetBoolean(),"Owner-close recovery must not manufacture a missing after snapshot");
        Console.WriteLine("PASS developer picture evidence: target-specific GPU swaps/restores, explicit wire receipt, no-damage/state comparison, missing trace/read and owner-close limits; gameAccess=false");
    }
}
#endif

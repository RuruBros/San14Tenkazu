#if LOCAL_DEVELOPER
using System.Buffers.Binary;
using System.Security.Cryptography;
namespace San14Tenkazu;

internal static class DeveloperNoticeCausality
{
    // These sizes are asserted by the native writer. Parsing never authorizes a swap.
    private static readonly string[] Stops=["None","PreparationDeadline","CaptureDeadline","RowCapacity","DescriptorCapacity","EventCapacity","ManualCancel","StateChanged","QueueFailure","OwnerStop"];
    private static string GateName(int index){
        string[] basic=["ForegroundPid","EngineForeground","RequestAge","ForegroundAge","NotBusy","BeginNotBusy","KindValid","LayoutPresent","TextPages","QueueHead","QueueNode","QueueReadback","ReadyFixture"];
        string[] ready=["CountNonzero","CountBounded","ItemsPresent","QueueEmpty","ManagerHead","ManagerReady","NoExistingMessage","GameplayPresent","LayoutPresent","NotShown","NotAnimating","AllowedTop"];
        if(index<basic.Length)return basic[index];int offset=index>=32?index-32:index-16;
        return offset>=0&&offset<ready.Length?(index>=32?"QueueRecheck.":"Preparation.")+ready[offset]:"Reserved";
    }
    internal static object Read(string path)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length is <168 or >64*1024*1024)throw new InvalidDataException("Causal evidence length differs");
        byte[] header=new byte[168];file.ReadExactly(header);
        uint U(int at)=>BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(at));
        ulong Q(int at)=>BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(at));
        if(U(0)!=0x4e434231||U(4)!=2||U(8)!=168||U(12)!=536||U(16)!=128||U(20)!=168||Q(24)==0||U(156)!=48||U(160)!=56||U(164)!=0)
            throw new InvalidDataException("Causal evidence version/ABI differs (version 2 required)");
        uint rows=U(56),descriptors=U(60),events=U(64);
        if(U(152)>=Stops.Length||Q(136)!=0&&(Q(128)==0||Q(136)<Q(128)||Q(144)==0)||Q(136)==0&&Q(144)!=0||rows>65536||descriptors>4096||events>4096||file.Length!=168L+rows*536L+descriptors*128L+events*168L+48*56)
            throw new InvalidDataException("Causal evidence counts differ");
        var authenticated=new HashSet<ushort>();uint pending=0;
        file.Position=168L+rows*536L;byte[] descriptor=new byte[128];
        for(ushort index=1;index<=descriptors;index++){
            file.ReadExactly(descriptor);uint state=BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(84));
            if(state>5)throw new InvalidDataException("Causal hash state differs");
            if(state==2){
                if(Convert.ToHexString(descriptor.AsSpan(92,32))!="3BD4B1D2D18B50AD64613CA80DC06102B52F56877788767D10A618DFA9147BAC")
                    throw new InvalidDataException("Causal authenticated hash differs");
                authenticated.Add(index);
            }
            if(state is 1 or 5)pending++;
        }
        file.Position=168;byte[] row=new byte[536];uint incomplete=0,unknownPath=0,boundaryRows=0,orphanRows=0;ulong authenticatedRows=0;
        var prepared=new HashSet<ulong>();var entered=new HashSet<ulong>();var samples=new List<object>();var paths=new HashSet<uint>();
        for(uint index=0;index<rows;index++){
            file.ReadExactly(row);
            uint R(int at)=>BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(at));
            ulong V(int at)=>BinaryPrimitives.ReadUInt64LittleEndian(row.AsSpan(at));
            uint phase=R(220),drawPath=R(216);if(phase is <1 or >6||drawPath>10||R(528)>1||R(532)!=0||R(524)>2)throw new InvalidDataException("Causal row kind differs");
            if(R(264)!=1)incomplete++;if(drawPath==0)unknownPath++;paths.Add(drawPath);boundaryRows+=R(528);
            if(phase==1)prepared.Add(V(0));if((phase is 2 or 3)&&!prepared.Contains(V(0)))orphanRows++;
            if(phase==4)entered.Add(V(0));if(phase==5&&!entered.Contains(V(0)))orphanRows++;
            if(V(80)!=Q(24)||V(72)!=Q(32)||Q(40)!=0&&V(88)!=Q(40)||V(192)!=Q(136)||V(200)!=Q(144)||V(16)<Q(144)||V(24)<V(16))throw new InvalidDataException("Causal generations differ");
            bool accepted=false;
            for(int slot=0;slot<128;slot++){
                ushort id=BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(268+slot*2));
                if(id==ushort.MaxValue){incomplete++;continue;}
                if(id>descriptors)throw new InvalidDataException("Causal descriptor reference differs");
                if(authenticated.Contains(id)){accepted=true;if(samples.Count<32)samples.Add(new{drawId=V(0),parent=V(8),qpc=V(16),path=drawPath,phase,slot,gate=R(248),kind=R(252),changed=R(260),counterBefore=R(240),counterAfter=R(244)});}
            }
            if(accepted)authenticatedRows++;
        }
        file.Position=168L+rows*536L+descriptors*128L;byte[] entry=new byte[168];uint invalidEvents=0;bool queueReached=false,pictureReached=false,finished=false;
        for(uint index=0;index<events;index++){
            file.ReadExactly(entry);
            uint phase=BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(72));
            queueReached|=phase==3;pictureReached|=phase==7;finished|=phase==12;if(phase is <1 or >12)invalidEvents++;
            if(BinaryPrimitives.ReadUInt64LittleEndian(entry)!=index+1||BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(24))!=Q(32)||BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(32))!=Q(24))invalidEvents++;
        }
        byte[] gate=new byte[56];var gates=new List<object>();
        for(int index=0;index<48;index++){
            file.ReadExactly(gate);ulong G(int offset)=>BinaryPrimitives.ReadUInt64LittleEndian(gate.AsSpan(offset));
            ulong evaluated=G(0),passed=G(8),rejected=G(16),firstQpc=G(24),lastQpc=G(32),firstValue=G(40),lastValue=G(48);
            if(passed>evaluated||rejected!=evaluated-passed||evaluated==0&&(firstQpc!=0||lastQpc!=0||firstValue!=0||lastValue!=0)||evaluated>0&&(firstQpc==0||lastQpc<firstQpc))
                throw new InvalidDataException("Causal preparation counters differ");
            gates.Add(new{index,name=GateName(index),notEvaluated=evaluated==0,evaluated,passed,rejected,firstQpc,lastQpc,firstValue,lastValue});
        }
        bool complete=Q(136)>0&&queueReached&&finished&&boundaryRows==0&&orphanRows==0&&rows>0&&events>0&&Q(40)!=0&&Q(48)>0&&incomplete==0&&invalidEvents==0&&unknownPath==0&&pending==0&&
            U(68)==0&&U(72)==0&&U(76)==0&&U(80)==0&&U(84)==0&&U(92)==0;
        file.Position=0;string sha256=Convert.ToHexString(SHA256.HashData(file));
        return new{formatVersion=2,rows,descriptors,events,season=Q(24),environment=Q(32),test=Q(40),frequency=Q(48),
            requestedMs=Q(128),captureStartedMs=Q(136),captureStartedQpc=Q(144),stopReason=Stops[U(152)],queueReached,pictureReached,finished,preparationOnly=!queueReached,gates,boundaryRows,orphanRows,
            rowDropped=U(68),descriptorDropped=U(72),snapshotMissed=U(76),excluded=U(80),pending,deadline=U(88),environmentMissing=U(92),
            incomplete,invalidEvents,unknownPath,recordSetComplete=complete,authenticatedRows,paths=paths.Order().ToArray(),samples,
            maxCallbackQpc=Q(112),totalCallbackQpc=Q(120),sha256,rootCauseConfirmed=false,drawInputProven=false};
    }
}
#endif

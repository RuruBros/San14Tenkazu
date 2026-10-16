#if LOCAL_DEVELOPER
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
namespace San14Tenkazu;

internal sealed record NoticeGetterEvidence(ulong Rva,string CodeHex,bool Interpreted,ulong Data);
internal sealed class NoticeReadRaceException(string stage,ulong address,string beforeHex,string afterHex,NoticeTypographyEvidence evidence)
    :InvalidOperationException(L.DeveloperErrors.NoticeReadChanged+stage+L.DeveloperErrors.NoticeReadUnconfirmed)
{
    public string Stage{get;}=stage;
    public ulong Address{get;}=address;
    public string BeforeHex{get;}=beforeHex;
    public string AfterHex{get;}=afterHex;
    public NoticeTypographyEvidence UnconfirmedEvidence{get;}=evidence;
}
internal sealed record NoticeRenderTextEvidence(ulong Address,ulong ClassRva,float? ScaleX,float? ScaleY,
    int Width,int Height,string RawHex,string? ReadError);
internal sealed record NoticeTextDetailsEvidence(int MeasuredWidth,int MeasuredHeight,int MeasuredLines,
    float? ContentScale,int LayoutMode,byte ModeByte5d,string WidgetExtendedHex,NoticeRenderTextEvidence? RenderText,string? ReadError);
internal sealed record NoticeTypographyEvidence(ulong State,ulong Dialog,ulong Widget,ulong WidgetClass,
    short TextAreaWidth,short TextAreaHeight,string WidgetHeaderHex,NoticeGetterEvidence FontGetter,
    NoticeGetterEvidence TextGetter,float? FontScaleX,float? FontScaleY,int? LineSpacing,int? FontId,
    string? FontDataHex,string? DisplayText,string? FontReadError,string? TextReadError,NoticeTextDetailsEvidence? Details=null);

internal static partial class GameMemory
{
    // READ access only. Never attach a native session, call a game function, or change a dialog.
    internal static NoticeTypographyEvidence ReadNoticeTypography(GameIdentity identity)
    {
        if(!identity.Verified||!identity.ExeHash.Equals(VerifiedExeHash,StringComparison.OrdinalIgnoreCase)||!IsSameGame(identity))
            throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        IntPtr handle=OpenRead(identity.ProcessId);
        try {
            var result=ReadNoticeTypography(identity.ModuleBase,(address,length)=>ReadBytes(handle,address,length));
            if(!IsSameGame(identity))throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
            return result;
        }finally{CloseHandle(handle);}
    }

    // The injected dependency exposes reads only; fixtures cannot silently call a game writer.
    internal static NoticeTypographyEvidence ReadNoticeTypography(ulong module,Func<ulong,int,byte[]> read)
    {
        byte[] Bytes(ulong address,int length){if(address==0)throw new InvalidDataException(L.DeveloperErrors.NoticePointerMissing);var data=read(address,length);if(data.Length!=length)throw new IOException(L.DeveloperErrors.NoticeReadIncomplete);return data;}
        var observed=new List<(ulong Address,byte[] Data)>();
        byte[] Observe(ulong address,int length){var data=Bytes(address,length);observed.Add((address,data));return data;}
        ulong Q(ulong address)=>BinaryPrimitives.ReadUInt64LittleEndian(Bytes(address,8));
        int I(ulong address)=>BinaryPrimitives.ReadInt32LittleEndian(Bytes(address,4));
        bool InCode(ulong address)=>address>=module+0x1000&&address<=module+0x133f9ae-64;
        // 0x863c70 selects dialog control #4 and calls the game's text/wrap processor.
        // Exact hash is recorded from the existing verified game image; offsets are not inferred from pointer shapes.
        byte[] currentTextCode=Bytes(module+0x863c70,129);
        if(Convert.ToHexString(SHA256.HashData(currentTextCode)).ToLowerInvariant()!=NoticeCurrentTextHash)
            throw new InvalidDataException(L.DeveloperErrors.NoticeCodeChanged);
        ulong manager=module+0x1b620b0;byte[] managerHeader=Bytes(manager+0x10,24);
        ulong count=BinaryPrimitives.ReadUInt64LittleEndian(managerHeader),items=BinaryPrimitives.ReadUInt64LittleEndian(managerHeader.AsSpan(16));
        if(count>128)throw new InvalidDataException(L.DeveloperErrors.NoticeCountInvalid);
        byte[] states=count==0?[]:Bytes(items,checked((int)count*8));ulong state=0;
        for(int i=0;i<(int)count;i++){ulong candidate=BinaryPrimitives.ReadUInt64LittleEndian(states.AsSpan(i*8));if(candidate!=0&&Q(candidate)==module+0x13acdb8){if(state!=0)throw new InvalidDataException(L.DeveloperErrors.MultipleNotices);state=candidate;}}
        if(state==0)throw new InvalidOperationException(L.DeveloperErrors.NoOpenNotice);
        ulong dialog=Q(state+0x470);if(Q(dialog)!=module+0x146e2c0)throw new InvalidDataException(L.DeveloperErrors.NoticeClassChanged);
        byte[] controls=Bytes(dialog+0x130,20);ulong descriptors=BinaryPrimitives.ReadUInt64LittleEndian(controls),widgets=BinaryPrimitives.ReadUInt64LittleEndian(controls.AsSpan(8));
        int controlsCount=BinaryPrimitives.ReadInt32LittleEndian(controls.AsSpan(16));
        if(controlsCount is <5 or >4096||I(descriptors+4*48)!=2)throw new InvalidDataException(L.DeveloperErrors.NoticeTextChanged);
        ulong widget=Q(widgets+4*8);byte[] header=Bytes(widget,0x128);
        if(header[0x112]!=2)throw new InvalidDataException(L.DeveloperErrors.TextWidgetChanged);
        ulong table=BinaryPrimitives.ReadUInt64LittleEndian(header);
        if(table<module+0x1340000||table>module+0x1a1f47e-0x178)throw new InvalidDataException(L.DeveloperErrors.TextVtableChanged);
        string? textResolveError=null;
        NoticeGetterEvidence Getter(int slot){
            ulong target=Q(table+(ulong)slot);if(!InCode(target))throw new InvalidDataException(L.DeveloperErrors.FontGetterRangeChanged);
            byte[] code=Bytes(target,64);ulong data=0;bool understood=false;
            foreach(var entry in NoticeLeafGetters)if(target-module==entry.Rva&&code.AsSpan(0,entry.Code.Length).SequenceEqual(entry.Code)){
                data=entry.Inline?widget+entry.Offset:Q(widget+entry.Offset);understood=true;break;
            }
            // The real widget's style getter accepts index 0, as the original wrap/measure callers do.
            // Decode this exact conditional leaf; do not execute it or generalize its pointer offset.
            if(slot==0x80&&table==module+0x1685ed8&&target-module==0xaccd10&&code.AsSpan(0,NoticeIndexedFontCode.Length).SequenceEqual(NoticeIndexedFontCode)){
                data=checked(widget+0x198);understood=true;
            }
            if(slot==0x170&&table==module+0x1685ed8&&target-module==0xaccea0&&code.AsSpan(0,NoticeStoredTextCode.Length).SequenceEqual(NoticeStoredTextCode))try {
                var selector=Observe(widget+0x2cc,8);
                int index=BinaryPrimitives.ReadInt32LittleEndian(selector.AsSpan((BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x104))&0x08000000)!=0?4:0));
                if(index<0){understood=true;data=0;}else{
                    // Constructor 0xaca380 sets this container class; slot +0x10 is the exact
                    // index*0x38 + [container+0x30] leaf. Each element owns UTF-16 at +0x30.
                    var storage=Observe(widget+0x290,0x38);
                    ulong storageClass=BinaryPrimitives.ReadUInt64LittleEndian(storage);
                    if(storageClass!=module+0x1685e58)throw new InvalidDataException(L.DeveloperErrors.StringStorageChanged);
                    ulong indexGetter=Q(storageClass+0x10);
                    if(indexGetter!=module+0xaccb00||!Observe(indexGetter,NoticeTextIndexCode.Length).AsSpan().SequenceEqual(NoticeTextIndexCode))
                        throw new InvalidDataException(L.DeveloperErrors.StringGetterChanged);
                    uint length=BinaryPrimitives.ReadUInt32LittleEndian(storage.AsSpan(0x20));
                    ulong capacity=BinaryPrimitives.ReadUInt64LittleEndian(storage.AsSpan(0x28)),entries=BinaryPrimitives.ReadUInt64LittleEndian(storage.AsSpan(0x30));
                    if(length>1024||capacity<length||capacity>1024||index>=length||entries==0)
                        throw new InvalidDataException(L.DeveloperErrors.StringIndexInvalid);
                    var entry=Observe(checked(entries+(ulong)index*0x38),0x38);
                    if(BinaryPrimitives.ReadUInt64LittleEndian(entry)!=module+0x1685a00)
                        throw new InvalidDataException(L.DeveloperErrors.StringEntryChanged);
                    data=BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(0x30));understood=true;
                }
            }catch(Exception error) when(error is IOException or InvalidDataException or OverflowException){textResolveError=error.Message;}
            return new(target-module,Convert.ToHexString(code),understood,data);
        }
        var fontGetter=Getter(0x80);var textGetter=Getter(0x170);
        bool realStyle=fontGetter.Interpreted&&fontGetter.Rva==0xaccd10;
        int fontLength=realStyle?0x78:96;
        float? scaleX=null,scaleY=null;int? spacing=null,fontId=null;string? fontRaw=null,text=null,fontError=null,textError=null;
        if(fontGetter.Interpreted&&fontGetter.Data!=0)try {
            var font=Bytes(fontGetter.Data,fontLength);fontRaw=Convert.ToHexString(font);
            float x=BitConverter.ToSingle(font,0x10),y=BitConverter.ToSingle(font,0x14);
            if(!float.IsFinite(x)||!float.IsFinite(y))throw new InvalidDataException(L.DeveloperErrors.FontScaleInvalid);
            scaleX=x;scaleY=y;spacing=BinaryPrimitives.ReadInt16LittleEndian(font.AsSpan(0x24));fontId=font[0x26];
        }catch(Exception error) when(error is IOException or InvalidDataException){fontError=error.Message;}
        else fontError=L.DeveloperErrors.FontFieldsUnverified;
        if(textGetter.Interpreted&&textGetter.Data!=0)try {
            // Read one bounded UTF-16 string from the visible widget, not the consumed input list.
            var buffer=new List<byte>();bool terminated=false;
            // Read bounded chunks within a mapped page, rather than a syscall per character.
            // Keep only the string through its terminator; unrelated trailing allocation bytes are not evidence.
            for(int offset=0;offset<2048&&!terminated;){
                ulong address=checked(textGetter.Data+(ulong)offset);
                int length=Math.Min(128,Math.Min(2048-offset,0x1000-(int)(address&0xfff)));
                if((length&1)!=0)throw new InvalidDataException(L.DeveloperErrors.StringBoundaryChanged);
                var chunk=Bytes(address,length);
                for(int j=0;j<chunk.Length;j+=2){if(chunk[j]==0&&chunk[j+1]==0){terminated=true;break;}buffer.Add(chunk[j]);buffer.Add(chunk[j+1]);}
                offset+=length;
            }
            if(!terminated)throw new InvalidDataException(L.DeveloperErrors.StringUnterminated);text=Encoding.Unicode.GetString(buffer.ToArray());
        }catch(Exception error) when(error is IOException or InvalidDataException){textError=error.Message;}
        else textError=textResolveError??(textGetter.Interpreted?L.DeveloperErrors.StringMissing:L.DeveloperErrors.StringFieldsUnverified);
        NoticeTextDetailsEvidence? details=null;
        if(realStyle)try {
            var extended=Observe(widget+0x198,0x198);
            float contentScale=BitConverter.ToSingle(extended,0x224-0x198);
            ulong renderer=BinaryPrimitives.ReadUInt64LittleEndian(extended.AsSpan(0x248-0x198));
            NoticeRenderTextEvidence? renderText=null;string? renderError=null;
            if(renderer!=0)try {
                // 0xacbfe0 -> 0xaf0b00 -> 0xaf09a0 initializes this exact render object.
                // 0xace110 copies style to +8; 0xac1a10 writes its final X/Y scales at +0x18/+0x1c.
                var render=Observe(renderer,0xc8);ulong renderClass=BinaryPrimitives.ReadUInt64LittleEndian(render);
                if(renderClass!=module+0x1689d28)throw new InvalidDataException(L.DeveloperErrors.RenderClassChanged);
                float x=BitConverter.ToSingle(render,0x18),y=BitConverter.ToSingle(render,0x1c);
                bool valid=float.IsFinite(x)&&float.IsFinite(y);
                renderText=new(renderer,renderClass-module,valid?x:null,valid?y:null,
                    BinaryPrimitives.ReadInt32LittleEndian(render.AsSpan(0xb0)),BinaryPrimitives.ReadInt32LittleEndian(render.AsSpan(0xb4)),
                    Convert.ToHexString(render),valid?null:L.DeveloperErrors.RenderScaleInvalid);
            }catch(Exception error) when(error is IOException or InvalidDataException){renderError=error.Message;}
            else renderError=L.DeveloperErrors.RenderObjectMissing;
            details=new(BinaryPrimitives.ReadInt32LittleEndian(extended.AsSpan(0x218-0x198)),
                BinaryPrimitives.ReadInt32LittleEndian(extended.AsSpan(0x21c-0x198)),BinaryPrimitives.ReadInt32LittleEndian(extended.AsSpan(0x220-0x198)),
                float.IsFinite(contentScale)?contentScale:null,BinaryPrimitives.ReadInt32LittleEndian(extended.AsSpan(0x2c)),extended[0x5d],
                Convert.ToHexString(extended),renderText,renderError??(float.IsFinite(contentScale)?null:L.DeveloperErrors.WidgetScaleInvalid));
        }catch(Exception error) when(error is IOException or InvalidDataException){fontError??=error.Message;}
        var evidence=new NoticeTypographyEvidence(state,dialog,widget,table-module,BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(0xf0)),
            BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(0xf2)),Convert.ToHexString(header),fontGetter,textGetter,
            scaleX,scaleY,spacing,fontId,fontRaw,text,fontError,textError,details);
        // Preserve every previous consistency guard. A rejected capture records the exact mismatch
        // and both raw samples, explicitly marked unconfirmed; it is never a successful snapshot.
        void Match(ulong address,byte[] expected,string stage){var actual=Bytes(address,expected.Length);if(!actual.AsSpan().SequenceEqual(expected))
            throw new NoticeReadRaceException(stage,address,Convert.ToHexString(expected),Convert.ToHexString(actual),evidence);}
        void MatchGetter(int slot,NoticeGetterEvidence expected){var actual=Getter(slot);if(actual!=expected)
            throw new NoticeReadRaceException($"widget.getter+0x{slot:x}",table+(ulong)slot,System.Text.Json.JsonSerializer.Serialize(expected),System.Text.Json.JsonSerializer.Serialize(actual),evidence);}
        Match(manager+0x10,managerHeader,"state.manager");if(count>0)Match(items,states,"state.items");
        Match(state,BitConverter.GetBytes(module+0x13acdb8),"state.class");Match(state+0x470,BitConverter.GetBytes(dialog),"state.dialog");
        Match(dialog,BitConverter.GetBytes(module+0x146e2c0),"dialog.class");Match(dialog+0x130,controls,"dialog.controls");
        Match(widgets+4*8,BitConverter.GetBytes(widget),"dialog.text.widget");Match(widget,header,"widget.header");
        MatchGetter(0x80,fontGetter);MatchGetter(0x170,textGetter);
        if(fontRaw is not null)Match(fontGetter.Data,Convert.FromHexString(fontRaw),"widget.font");
        if(text is not null)Match(textGetter.Data,Encoding.Unicode.GetBytes(text+'\0'),"widget.text");
        foreach(var sample in observed)Match(sample.Address,sample.Data,$"storage.or.render@0x{sample.Address:x}");
        return evidence;
    }

    private const string NoticeCurrentTextHash="b87ba9b82683726413685dc47c8c60c890315ee20c5ac665375871bf5371c244";
    private static readonly byte[] NoticeIndexedFontCode=Convert.FromHexString("85D2740333C0C3488D8198010000C3");
    private static readonly byte[] NoticeStoredTextCode=Convert.FromHexString("4883EC28F781040100000000000874088B91D0020000EB068B91CC02000085D2790733C04883C428C34881C190020000488B01FF5010488B40304883C428C3");
    private static readonly byte[] NoticeTextIndexCode=Convert.FromHexString("8BC2486BC03848034130C3");
    private readonly record struct NoticeLeafGetter(ulong Rva,uint Offset,bool Inline,byte[] Code);
    // Exact leaf bodies referenced by vtables in the verified stored image.
    // Unrecognized getters are recorded, never executed or heuristically followed.
    private static readonly NoticeLeafGetter[] NoticeLeafGetters=[
        new(0x84ec80,0x178,false,Convert.FromHexString("488B8178010000C3")),
        new(0xa230a0,0x50,false,Convert.FromHexString("488B4150C3")),
        new(0xa230b0,0x58,false,Convert.FromHexString("488B4158C3")),
        new(0xa4b080,8,false,Convert.FromHexString("488B4108C3")),
        new(0xac8a40,0xd8,false,Convert.FromHexString("488B81D8000000C3")),
        new(0xaccd40,0x280,true,Convert.FromHexString("488D8180020000C3")),
        new(0xaccd50,0x270,true,Convert.FromHexString("488D8170020000C3")),
        new(0xaccd60,0x260,true,Convert.FromHexString("488D8160020000C3")),
        new(0xaefba0,0xe0,false,Convert.FromHexString("488B81E0000000C3")),
        new(0xaefd80,0x48,false,Convert.FromHexString("488B4148C3")),
        new(0xaefda0,0xc0,false,Convert.FromHexString("488B81C0000000C3")),
        new(0xaf0db0,0x98,false,Convert.FromHexString("488B8198000000C3"))];
}
#endif

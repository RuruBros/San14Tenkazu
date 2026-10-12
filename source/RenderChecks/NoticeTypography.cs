#if LOCAL_DEVELOPER
using San14Tenkazu;
using System.Buffers.Binary;
using System.Text;
internal static partial class RenderChecks
{
    private sealed class TypographyMemory
    {
        internal const ulong Module=0x10000000,State=0x70001000,Dialog=0x70002000,Widget=0x70003000,Font=0x70004000,Text=0x70005000;
        internal const ulong Entries=0x70009000,Render=0x7000a000;
        private const ulong Items=0x70006000,Descriptors=0x70007000,Widgets=0x70008000,Table=Module+0x1470800;
        internal readonly Dictionary<ulong,byte[]> Blocks=[];
        internal readonly List<ulong> Reads=[];
        internal Action<ulong>? Reading;
        internal TypographyMemory()
        {
            Add(Module+0x863c70,Convert.FromHexString("40534883EC30488B9180010000488BD948837A08017264488B1241B901000000488B8938010000458BC148C744242800000000C744242001000000488B12488B4920488B5210E80595AAFF4C8B8380010000498B00488B08488B5108488B01488902488B11488B41084889420849FF48084883C4305BE9652FB9FF4883C4305BC3"));
            Add(Module+0x1b620b0,128);Q(Module+0x1b620c0,1);Q(Module+0x1b620d0,Items);Add(Items,8);Q(Items,State);
            Add(State,0x490);Q(State,Module+0x13acdb8);Q(State+0x470,Dialog);
            Add(Dialog,0x180);Q(Dialog,Module+0x146e2c0);Q(Dialog+0x130,Descriptors);Q(Dialog+0x138,Widgets);I(Dialog+0x140,5);
            Add(Descriptors,5*48);I(Descriptors+4*48,2);Add(Widgets,40);Q(Widgets+32,Widget);
            Add(Widget,0x128);Q(Widget,Table);Q(Widget+0xd8,Font);Q(Widget+0xe0,Text);Blocks[Widget][0x112]=2;
            BinaryPrimitives.WriteInt16LittleEndian(Blocks[Widget].AsSpan(0xf0),800);BinaryPrimitives.WriteInt16LittleEndian(Blocks[Widget].AsSpan(0xf2),80);
            Add(Table,0x178);Q(Table+0x80,Module+0xac8a40);Q(Table+0x170,Module+0xaefba0);
            Add(Module+0xac8a40,64);Convert.FromHexString("488B81D8000000C3").CopyTo(Blocks[Module+0xac8a40],0);
            Add(Module+0xaefba0,64);Convert.FromHexString("488B81E0000000C3").CopyTo(Blocks[Module+0xaefba0],0);
            Add(Font,96);BitConverter.GetBytes(.55f).CopyTo(Blocks[Font],0x10);BitConverter.GetBytes(1f).CopyTo(Blocks[Font],0x14);
            BinaryPrimitives.WriteInt16LittleEndian(Blocks[Font].AsSpan(0x24),28);Blocks[Font][0x26]=3;
            Add(Text,128);Encoding.Unicode.GetBytes("양평 산불 · 표시 전용\0").CopyTo(Blocks[Text],0);
        }
        private void Add(ulong address,int size)=>Add(address,new byte[size]);
        private void Add(ulong address,byte[] bytes)=>Blocks.Add(address,bytes);
        internal void Q(ulong address,ulong value)=>Put(address,BitConverter.GetBytes(value));
        internal void I(ulong address,int value)=>Put(address,BitConverter.GetBytes(value));
        private void Put(ulong address,byte[] bytes){var block=Blocks.First(p=>address>=p.Key&&address+(ulong)bytes.Length<=p.Key+(ulong)p.Value.Length);bytes.CopyTo(block.Value,(int)(address-block.Key));}
        internal byte[] Read(ulong address,int length){Reads.Add(address);Reading?.Invoke(address);foreach(var block in Blocks)if(address>=block.Key&&address+(ulong)length<=block.Key+(ulong)block.Value.Length)return block.Value.AsSpan((int)(address-block.Key),length).ToArray();throw new System.IO.IOException("Fixture read outside known region");}
        internal string Hash()=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Blocks.OrderBy(p=>p.Key).SelectMany(p=>p.Value).ToArray()));
        internal NoticeTypographyEvidence Capture()=>GameMemory.ReadNoticeTypography(Module,Read);
        internal void RealWidget()
        {
            var header=Blocks[Widget];Blocks[Widget]=new byte[0x330];header.CopyTo(Blocks[Widget],0);
            Q(Widget,Module+0x1685ed8);Add(Module+0x1685ed8,0x178);
            Q(Module+0x1685ed8+0x80,Module+0xaccd10);Q(Module+0x1685ed8+0x170,Module+0xaccea0);
            Add(Module+0xaccd10,64);Convert.FromHexString("85D2740333C0C3488D8198010000C3").CopyTo(Blocks[Module+0xaccd10],0);
            Add(Module+0xaccea0,64);Convert.FromHexString("4883EC28F781040100000000000874088B91D0020000EB068B91CC02000085D2790733C04883C428C34881C190020000488B01FF5010488B40304883C428C3").CopyTo(Blocks[Module+0xaccea0],0);
            Blocks[Font].CopyTo(Blocks[Widget],0x198);I(Widget+0x1c4,3);Blocks[Widget][0x1f5]=2;
            I(Widget+0x218,1120);I(Widget+0x21c,80);I(Widget+0x220,2);I(Widget+0x224,0x3f800000);
            Q(Widget+0x290,Module+0x1685e58);Add(Module+0x1685e58,0x18);Q(Module+0x1685e58+0x10,Module+0xaccb00);
            Add(Module+0xaccb00,Convert.FromHexString("8BC2486BC03848034130C3"));I(Widget+0x2b0,2);Q(Widget+0x2b8,2);Q(Widget+0x2c0,Entries);
            I(Widget+0x2cc,0);I(Widget+0x2d0,1);Add(Entries,2*0x38);Q(Entries,Module+0x1685a00);Q(Entries+0x30,Text);
            Q(Entries+0x38,Module+0x1685a00);Q(Entries+0x38+0x30,Text+0x100);
            Add(Text+0x100,128);Encoding.Unicode.GetBytes("다음 문자열\0").CopyTo(Blocks[Text+0x100],0);
            Q(Widget+0x248,Render);Add(Render,0xc8);Q(Render,Module+0x1689d28);
            I(Render+0x18,BitConverter.SingleToInt32Bits(.25f));I(Render+0x1c,BitConverter.SingleToInt32Bits(1f));I(Render+0xb0,800);I(Render+0xb4,80);
        }
    }
    private static void CheckNoticeTypographyReader()
    {
        static void Reject<T>(Action action,string reason) where T:Exception {try{action();}catch(T){Console.WriteLine("PASS typography "+reason);return;}throw new InvalidOperationException("Typography guard did not reject: "+reason);}
        var memory=new TypographyMemory();string before=memory.Hash();var evidence=memory.Capture();
        Require(evidence is{TextAreaWidth:800,TextAreaHeight:80,FontScaleX:.55f,FontScaleY:1f,LineSpacing:28,FontId:3,DisplayText:"양평 산불 · 표시 전용"},"Read-only typography must preserve actual geometry and scale values");
        Require(memory.Hash()==before,"Read-only typography must not change source memory");
        var absent=new TypographyMemory();absent.Q(TypographyMemory.Module+0x1b620c0,0);Reject<InvalidOperationException>(()=>absent.Capture(),"no dialog produces no notice or writes");
        var count=new TypographyMemory();count.Q(TypographyMemory.Module+0x1b620c0,129);Reject<System.IO.InvalidDataException>(()=>count.Capture(),"bounded manager count");
        var code=new TypographyMemory();code.Blocks[TypographyMemory.Module+0x863c70][0]^=1;Reject<System.IO.InvalidDataException>(()=>code.Capture(),"changed original text processor");
        var foreign=new TypographyMemory();foreign.Q(TypographyMemory.Dialog,TypographyMemory.Module+0x146e2c8);Reject<System.IO.InvalidDataException>(()=>foreign.Capture(),"foreign dialog class");
        var kind=new TypographyMemory();kind.Blocks[TypographyMemory.Widget][0x112]=1;Reject<System.IO.InvalidDataException>(()=>kind.Capture(),"foreign widget type");
        var target=new TypographyMemory();target.Q(TypographyMemory.Module+0x1470800+0x80,ulong.MaxValue-8);Reject<System.IO.InvalidDataException>(()=>target.Capture(),"overflowing getter code pointer");
        var unknown=new TypographyMemory();unknown.Blocks[TypographyMemory.Module+0xac8a40][0]^=1;var partial=unknown.Capture();
        Require(!partial.FontGetter.Interpreted&&partial.FontScaleX is null&&partial.FontReadError!=null&&!unknown.Reads.Any(a=>a>=TypographyMemory.Font&&a<TypographyMemory.Font+96),"Unknown getter must retain raw evidence without following guessed font pointers");
        var moving=new TypographyMemory();moving.Reading=address=>{if(address==TypographyMemory.Font)moving.Q(TypographyMemory.State+0x470,0);};Reject<InvalidOperationException>(()=>moving.Capture(),"dialog change rejects mixed snapshot");
        var nan=new TypographyMemory();BitConverter.GetBytes(float.NaN).CopyTo(nan.Blocks[TypographyMemory.Font],0x10);var invalid=nan.Capture();
        Require(invalid.FontScaleX is null&&invalid.FontReadError!=null&&invalid.FontDataHex!=null,"Invalid scale must remain raw evidence, never a numeric conclusion");
        var real=new TypographyMemory();real.RealWidget();string realBefore=real.Hash();var actual=real.Capture();
        Require(actual is{FontScaleX:.55f,FontScaleY:1f,DisplayText:"양평 산불 · 표시 전용",Details:{MeasuredWidth:1120,MeasuredHeight:80,MeasuredLines:2,ContentScale:1f,LayoutMode:3,ModeByte5d:2,RenderText:{ScaleX:.25f,ScaleY:1f,Width:800,Height:80}}}
            &&actual.FontGetter.Data==TypographyMemory.Widget+0x198&&actual.FontDataHex?.Length==0x78*2&&real.Hash()==realBefore,
            "Authenticated real widget must expose source/render scales, storage text and unchanged memory");
        var next=new TypographyMemory();next.RealWidget();next.I(TypographyMemory.Widget+0x104,0x08000000);Require(next.Capture().DisplayText=="다음 문자열","Original pending-text bit must select index +0x2d0");
        var empty=new TypographyMemory();empty.RealWidget();empty.I(TypographyMemory.Widget+0x2cc,-1);var noText=empty.Capture();Require(noText.DisplayText is null&&noText.TextGetter.Interpreted&&noText.TextReadError!=null,"Negative text index must not follow storage pointers");
        var badStore=new TypographyMemory();badStore.RealWidget();badStore.Q(TypographyMemory.Widget+0x290,TypographyMemory.Module+0x1685e60);var rejectedStore=badStore.Capture();Require(rejectedStore.DisplayText is null&&!rejectedStore.TextGetter.Interpreted&&!badStore.Reads.Contains(TypographyMemory.Entries),"Foreign storage class must not follow entries");
        var badIndex=new TypographyMemory();badIndex.RealWidget();badIndex.I(TypographyMemory.Widget+0x2cc,2);Require(!badIndex.Capture().TextGetter.Interpreted&&!badIndex.Reads.Contains(TypographyMemory.Entries+2*0x38),"Out-of-range storage index must be rejected before element read");
        var badCapacity=new TypographyMemory();badCapacity.RealWidget();badCapacity.Q(TypographyMemory.Widget+0x2b8,1);Require(!badCapacity.Capture().TextGetter.Interpreted,"Storage length must not exceed capacity");
        var badLeaf=new TypographyMemory();badLeaf.RealWidget();badLeaf.Blocks[TypographyMemory.Module+0xaccb00][0]^=1;Require(!badLeaf.Capture().TextGetter.Interpreted&&!badLeaf.Reads.Contains(TypographyMemory.Entries),"Altered storage getter must not be decoded");
        var badEntry=new TypographyMemory();badEntry.RealWidget();badEntry.Q(TypographyMemory.Entries,TypographyMemory.Module+0x1685a08);Require(!badEntry.Capture().TextGetter.Interpreted,"Foreign string element class must be rejected");
        var badFont=new TypographyMemory();badFont.RealWidget();badFont.Blocks[TypographyMemory.Module+0xaccd10][0]^=1;Require(!badFont.Capture().FontGetter.Interpreted,"Conditional font getter still requires exact bytes");
        var badRenderer=new TypographyMemory();badRenderer.RealWidget();badRenderer.Q(TypographyMemory.Render,TypographyMemory.Module+0x1689d30);Require(badRenderer.Capture().Details is{RenderText:null,ReadError:not null},"Foreign renderer must not supply scale values");
        var movingStore=new TypographyMemory();movingStore.RealWidget();movingStore.Reading=address=>{if(address==TypographyMemory.Text)movingStore.I(TypographyMemory.Widget+0x2cc,1);};Reject<InvalidOperationException>(()=>movingStore.Capture(),"storage selector change rejects mixed snapshot");
        var movingRender=new TypographyMemory();movingRender.RealWidget();int renderReads=0;movingRender.Reading=address=>{if(address==TypographyMemory.Render&&++renderReads==2)movingRender.I(TypographyMemory.Render+0x18,BitConverter.SingleToInt32Bits(.5f));};Reject<InvalidOperationException>(()=>movingRender.Capture(),"render scale change rejects mixed snapshot");
        var audited=new TypographyMemory();audited.RealWidget();audited.Reading=address=>{if(address==TypographyMemory.Text)audited.Blocks[TypographyMemory.Widget][0x104]^=1;};
        try{audited.Capture();throw new InvalidOperationException("Changed widget header was accepted");}
        catch(NoticeReadRaceException race){Require(race.Stage=="widget.header"&&race.Address==TypographyMemory.Widget&&race.BeforeHex!=race.AfterHex&&race.UnconfirmedEvidence.Details?.RenderText is not null,"Rejected capture must preserve exact changed stage and unconfirmed raw evidence");}
        var nanRender=new TypographyMemory();nanRender.RealWidget();nanRender.I(TypographyMemory.Render+0x18,BitConverter.SingleToInt32Bits(float.NaN));Require(nanRender.Capture().Details?.RenderText is{ScaleX:null,ReadError:not null},"Invalid rendered scale must remain raw evidence");
        Console.WriteLine("PASS real widget conditional getter, both string indices, independent render scale and original storage/code/class/bounds/race guards; reads only");
        Console.WriteLine("PASS read-only typography: actual fields, UTF-16 widget text, unchanged memory, bounded/class/code/getter/race/invalid-scale guards; game access false");
    }
}
#endif

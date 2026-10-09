using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace San14Tenkazu.Tests;

internal sealed class PlainRuleFixture : IRecordMemory
{
    private readonly OfficeFixture officeFallback=new();
    internal readonly PlainRuleFixture? Ranks;
    internal OfficeFixture Offices => Ranks?.Offices ?? officeFallback;
    internal GameIdentity Identity => Offices.Identity with {Classes=Offices.Identity.Classes with {DoctrinePanel=0x140020000}};
    internal PlainRuleSpec Spec {get;}
    private readonly Dictionary<ulong,byte[]> segments=[];
    internal ulong Array=0x312000000;
    internal int Writes,FailWrite;
    internal Action<int>? OnWrite {get;set;}
    internal PlainRuleFixture(PlainRuleSpec? spec=null) {Spec=spec ?? RankRules.Spec; if (Spec.Table=="doctrine") {Ranks=new(); Array=0x314000000;} Load();}
    internal void Load()
    {
        var pointers=new byte[Spec.Count*8];
        for (int id=0;id<Spec.Count;++id) {
            ulong at=Array+(ulong)(id*Spec.Size); BinaryPrimitives.WriteUInt64LittleEndian(pointers.AsSpan(id*8),at);
            var data=new byte[Spec.Size]; data.AsSpan().Fill(0xA5); BinaryPrimitives.WriteUInt64LittleEndian(data,Spec.Class(Identity.Classes));
            data.AsSpan(0x10,Spec.NameChars*2).Clear(); if (id>0) Encoding.Unicode.GetBytes("R"+id).CopyTo(data,0x10);
            foreach (var f in Spec.Fields) f.Storage.Encode(f.Id=="capacity" ? 18000 : f.Id is "grade" or "kind1" or "kind2" ? 2 : 5).CopyTo(data,f.Offset);
            segments[at]=data;
        }
        segments[Offices.Policies.Tactics.Formations.Root+Spec.TableOffset]=pointers;
    }
    private (byte[] Data,int Offset)? Find(ulong at,int size) {
        foreach (var s in segments) if (at>=s.Key && at+(ulong)size<=s.Key+(ulong)s.Value.Length) return (s.Value,(int)(at-s.Key));
        return null;
    }
    public byte[] Read(ulong at,int size) => Find(at,size) is { } s ? s.Data.AsSpan(s.Offset,size).ToArray() : Ranks is not null ? Ranks.Read(at,size) : Offices.Read(at,size);
    internal void Set(ulong at,byte[] bytes) {if (Find(at,bytes.Length) is { } s) bytes.CopyTo(s.Data,s.Offset); else if (Ranks is not null) Ranks.Set(at,bytes); else Offices.Set(at,bytes);}
    public void Write(ulong at,byte[] bytes) {
        ++Writes; if (Writes==FailWrite) {Set(at,bytes[..Math.Max(1,bytes.Length/2)]); throw new IOException("Injected plain rule partial write");}
        Set(at,bytes); OnWrite?.Invoke(Writes);
    }
    internal PlainRuleSnapshot Snapshot() => PlainRuleMemory.Read(this,Identity,Spec);
    internal RuleProfile Profile() => (Ranks?.Profile() ?? Offices.Profile()).AddPlain(Snapshot());
}

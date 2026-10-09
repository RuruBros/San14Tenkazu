using System.Buffers.Binary;
using System.Security.Cryptography;

namespace San14Tenkazu.Seasons;

internal enum SeasonTextureRole { TerrainDiffuse=1, TerrainNormal=2, TerrainRough=3, TreeDiffuse=4, Climate=5, TreeCoverage=6 }
internal readonly record struct SeasonTexturePart(int Layer,int Mip,int Width,int Height,int Offset,int RowPitch,int Length);

// Accepts only this project's generated DX10 DDS contract, not arbitrary DDS.
// A validated snapshot owns its bytes. Upload must consume these bytes rather
// than reopen the file after verification. This does not verify a game Pass.
internal sealed class SeasonTextureData
{
    private const int HeaderBytes=148, MaxFileBytes=128*1024*1024;
    private readonly byte[] bytes;
    private readonly SeasonTexturePart[] parts;
    internal SeasonTextureRole Role { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal int Mips { get; }
    internal int Layers { get; }
    internal int Format { get; }
    internal int PayloadBytes => bytes.Length-HeaderBytes;
    internal string Sha256 { get; }
    internal IReadOnlyList<SeasonTexturePart> Parts => Array.AsReadOnly(parts);
    internal ReadOnlySpan<byte> Data(SeasonTexturePart part)
    {
        int index=part.Layer*Mips+part.Mip;
        if(index<0 || index>=parts.Length || parts[index]!=part)throw new ArgumentException("Foreign texture subresource",nameof(part));
        return bytes.AsSpan(part.Offset,part.Length);
    }

    internal static SeasonTextureData Load(string path,SeasonTextureRole role,string sha256)
    {
        // Sharing only reads prevents a concurrent Windows writer while copying.
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length<HeaderBytes || file.Length>MaxFileBytes)throw new InvalidDataException("Season texture file size rejected");
        var copy=new byte[(int)file.Length];file.ReadExactly(copy);
        if(file.ReadByte()!=-1)throw new InvalidDataException("Season texture file changed while reading");
        return new(copy,role,sha256);
    }
    internal static SeasonTextureData Parse(ReadOnlySpan<byte> input,SeasonTextureRole role,string sha256)
    {
        if(input.Length<HeaderBytes || input.Length>MaxFileBytes)throw new InvalidDataException("Season texture file size rejected");
        return new(input.ToArray(),role,sha256);
    }
    private SeasonTextureData(byte[] owned,SeasonTextureRole role,string expectedHash)
    {
        if(!Enum.IsDefined(role))throw new InvalidDataException("Unknown season texture role");
        if(expectedHash is null || expectedHash.Length!=64 || expectedHash.Any(c=>!Uri.IsHexDigit(c)))
            throw new InvalidDataException("Pinned texture SHA256 is required");
        bytes=owned;Sha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if(!string.Equals(Sha256,expectedHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Season texture SHA256 mismatch");
        Role=role;bool climate=role==SeasonTextureRole.Climate,coverage=role==SeasonTextureRole.TreeCoverage;
        uint Word(int index)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index*4,4));
        void Require(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
        Require(Word(0)==0x20534444 && Word(1)==124 && Word(19)==32 && Word(20)==4 && Word(21)==0x30315844,"Season DDS header rejected");
        Require(Word(2)==(climate?0x100Fu:0x2100Fu) && Word(6)==0,"Season DDS flags/depth rejected");
        for(int i=8;i<=18;i++)Require(Word(i)==0,"Season DDS reserved header rejected");
        for(int i=22;i<=26;i++)Require(Word(i)==0,"Season DDS legacy pixel format rejected");
        for(int i=28;i<=31;i++)Require(Word(i)==0,"Season DDS volume/cube/reserved caps rejected");
        Require(Word(27)==(climate?0x1008u:0x401008u) && Word(33)==3 && Word(34)==0 && Word(36)==0,"Season DDS resource flags rejected");
        Format=climate || coverage?16:role is SeasonTextureRole.TerrainDiffuse or SeasonTextureRole.TreeDiffuse?91:87;
        Require(Word(32)==Format,"Season DDS format differs from role");
        uint w=Word(4),h=Word(3),mips=Word(7);Layers=climate?12:1;
        Require(w>=1 && h>=1 && w<=(climate?1024:2048) && h<=(climate?1024:2048) &&
            (w&(w-1))==0 && (h&(h-1))==0,"Season DDS dimensions rejected");
        Width=(int)w;Height=(int)h;
        int completeMips=1;for(uint dimension=Math.Max(w,h);dimension>1;dimension>>=1)completeMips++;
        Mips=climate?1:completeMips;
        Require(mips==Mips && Word(35)==Layers,"Season DDS mip/layer count rejected");
        int pixelBytes=climate || coverage?8:4;Require(Word(5)==w*pixelBytes,"Season DDS row pitch rejected");
        var list=new List<SeasonTexturePart>();int offset=HeaderBytes;
        for(int layer=0;layer<Layers;layer++)for(int mip=0;mip<Mips;mip++)
        {
            int mw=Math.Max(1,Width>>mip),mh=Math.Max(1,Height>>mip),pitch=mw*pixelBytes,length=pitch*mh;
            Require(length<=bytes.Length-offset,"Truncated season DDS payload");
            list.Add(new(layer,mip,mw,mh,offset,pitch,length));offset+=length;
        }
        Require(offset==bytes.Length,"Unexpected trailing season DDS payload");parts=list.ToArray();
        if(climate)
        {
            for(int at=HeaderBytes;at<bytes.Length;at+=8)
            {
                float temperature=BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at,4));
                float rain=BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at+4,4));
                Require(float.IsFinite(temperature) && temperature is >=-60 and <=60 &&
                    float.IsFinite(rain) && rain is >=0 and <=3000,"Invalid season climate texel");
            }
        }
        else if(coverage)
        {
            for(int at=HeaderBytes;at<bytes.Length;at+=8)
            {
                float leaf=BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at,4));
                float branch=BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at+4,4));
                Require(float.IsFinite(leaf) && float.IsFinite(branch) && leaf is >=0 and <=1 &&
                    branch is >=0 and <=1 && leaf+branch<=1+1e-6f,"Invalid leaf/branch coverage texel");
            }
        }
        else if(role!=SeasonTextureRole.TreeDiffuse)
            for(int at=HeaderBytes+3;at<bytes.Length;at+=4)Require(bytes[at]==255,"Non-opaque terrain texel");
    }

    internal void ValidateCoverageSource(SeasonTextureData diffuse)
    {
        ArgumentNullException.ThrowIfNull(diffuse);
        if(Role!=SeasonTextureRole.TreeCoverage || diffuse.Role!=SeasonTextureRole.TreeDiffuse ||
            Width!=diffuse.Width || Height!=diffuse.Height || Mips!=diffuse.Mips || Layers!=diffuse.Layers)
            throw new InvalidDataException("Coverage must pair with an identical tree diffuse mip layout");
        foreach(var part in parts)
        {
            var mask=Data(part);var source=diffuse.Data(diffuse.parts[part.Mip]);
            for(int pixel=0;pixel<part.Width*part.Height;pixel++)
                SeasonTreeMaskProcessor.ValidatePair(BinaryPrimitives.ReadSingleLittleEndian(mask.Slice(pixel*8,4)),
                    BinaryPrimitives.ReadSingleLittleEndian(mask.Slice(pixel*8+4,4)),source[pixel*4+3]/255f);
        }
    }
}

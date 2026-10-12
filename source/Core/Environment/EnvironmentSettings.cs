using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using San14Tenkazu.Seasons;

namespace San14Tenkazu.EnvironmentRules;

[Flags]
internal enum EnvironmentEffects : uint
{
    None=0, RainMovement=1, SnowMovement=2, WeatherMorale=4, Flood=8, Wildfire=16, Drought=32,
    RainVisual=64,SnowVisual=128,Blizzard=256,Heatwave=512,
    All=RainMovement|SnowMovement|WeatherMorale|Flood|Wildfire|Drought|RainVisual|SnowVisual|Blizzard|Heatwave
}
[StructLayout(LayoutKind.Sequential,Pack=8)]
internal readonly record struct EnvironmentValues(int RainReduction,int HeavyRainReduction,int SnowReduction,int DeepSnowReduction,int WeatherMoraleLoss,
    int FloodChance,int FloodTroopsPercent,int FloodMoraleLoss,int FloodFoodPercent,
    int WildfireChance,int WildfireTroopsPercent,int WildfireMoraleLoss,int WildfireFoodPercent,int DroughtChance,int DroughtFoodPercent,
    int BlizzardChance=30,int BlizzardTroopsPercent=3,int BlizzardMoraleLoss=4,int BlizzardFoodPercent=3,
    int HeatwaveChance=30,int HeatwaveTroopsPercent=3,int HeatwaveMoraleLoss=4,int HeatwaveFoodPercent=3)
{
    internal static EnvironmentValues Default=>new(10,20,20,40,2,30,5,4,6,30,5,4,6,30,6);
    internal void Validate()
    {
        if(new[]{RainReduction,HeavyRainReduction,SnowReduction,DeepSnowReduction,FloodTroopsPercent,WildfireTroopsPercent,BlizzardTroopsPercent,HeatwaveTroopsPercent}.Any(v=>v is <0 or >99)
            ||new[]{WeatherMoraleLoss,FloodMoraleLoss,WildfireMoraleLoss,BlizzardMoraleLoss,HeatwaveMoraleLoss}.Any(v=>v is <0 or >130)
            ||new[]{FloodFoodPercent,WildfireFoodPercent,DroughtFoodPercent,BlizzardFoodPercent,HeatwaveFoodPercent}.Any(v=>v is <0 or >100)
            ||new[]{FloodChance,WildfireChance,DroughtChance,BlizzardChance,HeatwaveChance}.Any(v=>v is <0 or >1000))throw new InvalidDataException(L.Environment.InvalidSettings);
    }
}
internal sealed record EnvironmentSettings(int Version, EnvironmentEffects Effects,EnvironmentValues Values,ulong Seed)
{
    internal static EnvironmentSettings New() => new(3,EnvironmentEffects.None,EnvironmentValues.Default,BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8))|1);
    internal void Validate()
    {
        if(Version!=3||(Effects&~EnvironmentEffects.All)!=0||Seed==0)
            throw new InvalidDataException(L.Environment.InvalidSettings);
        Values.Validate();
    }
}

internal sealed class EnvironmentSettingsFile(string path)
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    internal string PathName=>path;
    private static string Stamp(byte[]? bytes)=>bytes is null?"":Convert.ToHexString(SHA256.HashData(bytes));
    internal (EnvironmentSettings? Settings,string Stamp) Read()
    {
        if(!File.Exists(path))return(null,"");
        byte[] bytes=File.ReadAllBytes(path);
        if(bytes.Length is <2 or >16384)throw new InvalidDataException(L.Environment.InvalidSettings);
        using var document=JsonDocument.Parse(bytes);var root=document.RootElement;
        EnvironmentSettings value;
        int version=root.GetProperty("Version").GetInt32();
        if(version==1)
        {
            int strength=root.GetProperty("Strength").GetInt32(),frequency=root.GetProperty("Frequency").GetInt32();
            if(strength is <0 or >2||frequency is <0 or >2)throw new InvalidDataException(L.Environment.InvalidSettings);
            int rain=new[]{5,10,20}[strength],snow=new[]{10,20,30}[strength],morale=new[]{1,2,3}[strength],
                troops=new[]{2,5,8}[strength],food=new[]{3,6,10}[strength],chance=new[]{10,30,60}[frequency];
            value=new(3,(EnvironmentEffects)root.GetProperty("Effects").GetUInt32(),
                new(rain,rain*2,snow,snow*2,morale,chance,troops,morale*2,food,chance,troops,morale*2,food,chance,food),root.GetProperty("Seed").GetUInt64());
        }
        else
        {
            var values=root.GetProperty("Values");
            foreach(var property in typeof(EnvironmentValues).GetProperties(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance))
                if(!(version==2&&(property.Name.StartsWith("Blizzard")||property.Name.StartsWith("Heatwave")))&&!values.TryGetProperty(property.Name,out _))throw new InvalidDataException(L.Environment.InvalidSettings);
            value=JsonSerializer.Deserialize<EnvironmentSettings>(bytes,Json)??throw new InvalidDataException(L.Environment.InvalidSettings);
            if(version==2){var defaults=EnvironmentValues.Default;value=value with{Version=3,Values=value.Values with{
                BlizzardChance=defaults.BlizzardChance,BlizzardTroopsPercent=defaults.BlizzardTroopsPercent,BlizzardMoraleLoss=defaults.BlizzardMoraleLoss,BlizzardFoodPercent=defaults.BlizzardFoodPercent,
                HeatwaveChance=defaults.HeatwaveChance,HeatwaveTroopsPercent=defaults.HeatwaveTroopsPercent,HeatwaveMoraleLoss=defaults.HeatwaveMoraleLoss,HeatwaveFoodPercent=defaults.HeatwaveFoodPercent}};}
        }
        if(version is 1 or 2 && ((uint)value.Effects&~255u)!=0)throw new InvalidDataException(L.Environment.InvalidSettings);
        value.Validate();return(value,Stamp(bytes));
    }
    internal string Save(EnvironmentSettings value,string expectedStamp)
    {
        value.Validate();Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using var guard=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        byte[]? before=File.Exists(path)?File.ReadAllBytes(path):null;
        if(Stamp(before)!=expectedStamp)throw new IOException(L.Environment.SettingsMoved);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(value,Json);
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {stream.Write(bytes);stream.Flush(true);}
            File.Move(temp,path,true);return Stamp(bytes);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}

// These are authored gameplay rules, not a claim that modern normals predict historical weather.
// Exported vectors compare this reference with the native code that runs on the game's thread.
internal static class EnvironmentPolicy
{
    internal static ulong Random(ulong seed,uint date,uint city,uint kind)
    {
        unchecked
        {
            ulong value=seed^((ulong)date<<32)^((ulong)city<<16)^kind;
            value+=0x9E3779B97F4A7C15;value=(value^(value>>30))*0xBF58476D1CE4E5B9;
            value=(value^(value>>27))*0x94D049BB133111EB;return value^(value>>31);
        }
    }
    internal static uint Serial(GameCalendarDate date)
    {
        if(!date.IsValid)throw new ArgumentOutOfRangeException(nameof(date));
        return (uint)(date.Year*360+(date.Month-1)*30+date.Day-1);
    }
    // Rain is generated per region/day. Snow cover remains for a three-day weather block.
    // Upper two bits: snow depth; lower two bits: rain strength.
    internal static uint Weather(ulong seed,uint date,uint city,int temperatureTenths,int precipitationTenths)
    {
        if(city is <1 or >46||temperatureTenths is < -600 or >600||precipitationTenths is <0 or >30000)
            throw new ArgumentOutOfRangeException(nameof(city));
        uint chance=(uint)Math.Clamp(precipitationTenths/5,50,650);
        if(temperatureTenths<=20)
        {
            uint block=date/3;
            if(Random(seed,block,city,2)%1000>=chance)return 0;
            return (Random(seed,block,city,3)%3==0?2u:1u)<<2;
        }
        if(Random(seed,date,city,1)%1000>=chance)return 0;
        return Random(seed,date,city,4)%4==0?2u:1u;
    }
    internal static int MovementPercent(uint weather,EnvironmentEffects flags,EnvironmentValues values)
    {
        int rain=(int)(weather&3),snow=(int)((weather>>2)&3);values.Validate();
        if(rain>2||snow>2||weather>10||(flags&~EnvironmentEffects.All)!=0)
            throw new ArgumentOutOfRangeException(nameof(weather));
        int value=100;
        if((flags&EnvironmentEffects.RainMovement)!=0&&rain>0)value-=rain==1?values.RainReduction:values.HeavyRainReduction;
        if((flags&EnvironmentEffects.SnowMovement)!=0&&snow>0)value-=snow==1?values.SnowReduction:values.DeepSnowReduction;
        return Math.Max(1,value);
    }
}

internal static class EnvironmentClimate
{
    // Interpolate monthly source normals using the verified hex grid and source cityTile anchors.
    internal static byte[] Pack(GameSnapshot snapshot)
    {
        using var stream=typeof(SeasonClimate).Assembly.GetManifestResourceStream("Seasons.climate.json")!;
        using var json=JsonDocument.Parse(stream);
        var sources=json.RootElement.GetProperty("stations").EnumerateArray().Select(s=>new{
            Tile=s.GetProperty("cityTile").GetInt32(),
            T=s.GetProperty("temperatureC").EnumerateArray().Select(v=>v.GetDouble()).ToArray(),
            P=s.GetProperty("precipitationMm").EnumerateArray().Select(v=>v.GetDouble()).ToArray()}).ToArray();
        static (double X,double Y) Center(int tile)=>(1.5*(tile%220),Math.Sqrt(3)*(tile/220+((tile%220&1)==0?.5:0)));
        using var result=new MemoryStream();using var writer=new BinaryWriter(result);
        for(int city=1;city<=46;city++)
        {
            if(!snapshot.CastleByCity.TryGetValue(city,out var castle))throw new InvalidDataException(L.Environment.RegionUnavailable);
            var point=Center(castle.Tile);
            for(int month=0;month<12;month++)
            {
                double t=0,p=0,weight=0;
                foreach(var source in sources)
                {
                    var anchor=Center(source.Tile);double distance=Math.Pow(anchor.X-point.X,2)+Math.Pow(anchor.Y-point.Y,2);
                    if(distance<1e-12){t=source.T[month];p=source.P[month];weight=1;break;}
                    double w=1/distance;t+=source.T[month]*w;p+=source.P[month]*w;weight+=w;
                }
                writer.Write(checked((short)Math.Round(t/weight*10)));
                writer.Write(checked((ushort)Math.Round(p/weight*10)));
            }
        }
        return result.ToArray();
    }
}

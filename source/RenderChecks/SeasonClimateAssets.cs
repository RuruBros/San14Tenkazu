using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Seasons;

internal static partial class RenderChecks
{
    private static void PrepareSeasonClimate(string root)
    {
        root=Path.GetFullPath(root);string sourcePath=Path.Combine(root,"Assets","climate.json");
        byte[] source=File.ReadAllBytes(sourcePath);using var json=JsonDocument.Parse(source);
        foreach(var station in json.RootElement.GetProperty("stations").EnumerateArray())
        {
            string file=station.GetProperty("sourceFile").GetString()!;
            string expected=station.GetProperty("sourceSha256").GetString()!;
            if(Hash(File.ReadAllBytes(Path.Combine(root,"Assets","Sources",file)))!=expected)
                throw new InvalidDataException("JMA source hash differs: "+file);
            int city=station.GetProperty("cityId").GetInt32(),tile=station.GetProperty("cityTile").GetInt32();
            if(city<1 || city>=MapTerrain.CityTiles.Length || tile!=MapTerrain.CityTiles[city])
                throw new InvalidDataException("Climate city anchor differs from recorded map data");
            int col=tile%MapTerrain.Columns,row=tile/MapTerrain.Columns;
            // Double arithmetic matches climate.py; MapTerrain's float centers are rounded for display.
            double x=GameTerrain.OriginX+GameTerrain.UnitsPerHex*(1.5*col+1);
            double z=GameTerrain.OriginZ+GameTerrain.UnitsPerHex*Math.Sqrt(3)*(row+(col%2==0?1:0.5));
            if(Math.Abs(x-station.GetProperty("gameX").GetDouble())>1e-6 ||
               Math.Abs(z-station.GetProperty("gameZ").GetDouble())>1e-6)
                throw new InvalidDataException("Climate game-coordinate mapping differs");
        }
        using var stream=new MemoryStream(source);var climate=SeasonClimate.Load(stream);
        // GameTerrain: the full field color/normal textures are 4096 texels at 50 game units/texel.
        // These are asset coordinates, not an assertion about any unverified game Draw-call transform.
        const double fieldSpan=4096*50;var extent=new ClimateExtent(0,0,fieldSpan,fieldSpan);
        var atlas=SeasonClimateAtlas.Build(climate,512,512,extent);
        string generated=Path.Combine(root,"Assets","Processed");Directory.CreateDirectory(generated);
        string output=Path.Combine(generated,"climate-monthly.dds");WriteClimateDds(output,atlas);
        var checks=new List<object>();double maxTemp=0,maxRain=0;
        foreach(var station in climate.Stations)
            for(int month=1;month<=12;month++)
            {
                var date=new MonthBlend(month,month==12?1:month+1,0);
                var direct=station.Sample(date);var sampled=atlas.Sample(station.X,station.Z,date);
                double t=Math.Abs(direct.TemperatureC-sampled.TemperatureC),p=Math.Abs(direct.PrecipitationMm-sampled.PrecipitationMm);
                maxTemp=Math.Max(maxTemp,t);maxRain=Math.Max(maxRain,p);
                checks.Add(new{station.Id,station.CityId,month,
                    source=new{direct.TemperatureC,direct.PrecipitationMm},
                    atlas=new{sampled.TemperatureC,sampled.PrecipitationMm},temperatureError=t,precipitationError=p});
            }
        if(maxTemp>0.05 || maxRain>0.5)throw new InvalidDataException($"Climate atlas anchor error: {maxTemp} C, {maxRain} mm");
        // A labeled monthly contact sheet is a data visualization, not a game effect capture.
        SaveClimateContactSheet(Path.Combine(generated,"climate-monthly-preview.png"),atlas,climate);
        string checksFolder=Path.Combine(root,"..","Reverse",".cache","season-safety","20261006-assets");Directory.CreateDirectory(checksFolder);
        int vectorCount=SaveClimateVectors(Path.Combine(checksFolder,"climate-vectors.bin"),atlas,climate);
        var manifest=new
        {
            file="climate-monthly.dds",sha256=Hash(File.ReadAllBytes(output)),source="climate.json",sourceSha256=Hash(source),
            provider="Japan Meteorological Agency ClimatView",license="CC-BY-4.0 (JMA alternative terms)",
            terms="https://www.jma.go.jp/jma/en/copyright.html",termsVerifiedDate="2026-10-06",
            attribution="Created by San14Tenkazu based on JMA ClimatView monthly normals; spatially interpolated for game artwork. Not created by JMA. No ancient temperature correction.",
            sourceStations=json.RootElement.GetProperty("stations").EnumerateArray().Select(s=>new
            {
                stationId=s.GetProperty("stationId").GetInt32(),cityId=s.GetProperty("cityId").GetInt32(),
                cityName=s.GetProperty("cityName").GetString(),sourceUrl=s.GetProperty("source").GetString(),
                sourceSha256=s.GetProperty("sourceSha256").GetString(),correspondence=s.GetProperty("correspondence").GetString()
            }),
            width=atlas.Width,height=atlas.Height,arraySize=12,mipLevels=1,format="R32G32_FLOAT",channels=new[]{"TemperatureC","PrecipitationMm"},
            payloadBytes=atlas.Data.Length*sizeof(float),extent=new{extent.MinX,extent.MinZ,extent.MaxX,extent.MaxZ},
            coordinateBasis="Full FieldEditor4 texture, 4096 texels * 50 game units; cities use GameTerrain origin and hex transform",
            sampling="Clamp + bilinear, explicit LOD 0; array slice = month - 1; interpolate adjacent months by (day - 1) / 30",
            spatialInterpolation="All verified city anchors; inverse squared distance; exact anchor retains source normal",
            texelMapping="gameXZ = extent.min + (pixelXY + 0.5) / size * (extent.max - extent.min)",
            anchorValidation=new{count=checks.Count,maxTemperatureErrorC=maxTemp,maxPrecipitationErrorMm=maxRain,checks},
            cpuGpuReferenceVectors=vectorCount,gameDrawTransformVerified=false,gameApplied=false
        };
        File.WriteAllText(Path.Combine(generated,"climate-manifest.json"),JsonSerializer.Serialize(manifest,new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
        Console.WriteLine($"Prepared 12 climate layers, {atlas.Width}x{atlas.Height}; anchor errors {maxTemp:G6} C / {maxRain:G6} mm. No game access.");
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static int SaveClimateVectors(string path,SeasonClimateAtlas atlas,SeasonClimate climate)
    {
        var vectors=new List<(float X,float Z,MonthBlend Date)>();
        var positions=climate.Stations.Select(s=>((float)s.X,(float)s.Z)).Concat(new (float,float)[]
        {
            (0,0),(204800,0),(0,204800),(204800,204800),(102400,102400),
            (-1000,80000),(205800,120000),(-float.MaxValue,-float.MaxValue),(float.MaxValue,float.MaxValue)
        });
        foreach(var (x,z) in positions)
            for(int month=1;month<=12;month++)for(int day=1;day<=30;day++)
                vectors.Add((x,z,new(month,month==12?1:month+1,(float)((day-1)/30.0))));
        var random=new Random(54511);
        for(int i=0;i<256;i++)
        {
            int month=random.Next(1,13),day=random.Next(1,31);
            vectors.Add(((float)(random.NextDouble()*204800),(float)(random.NextDouble()*204800),
                new(month,month==12?1:month+1,(float)((day-1)/30.0))));
        }
        using var file=File.Create(path);using var writer=new BinaryWriter(file);
        writer.Write(0x53434154);writer.Write(1);writer.Write(vectors.Count);writer.Write(0);
        foreach(var (x,z,date) in vectors)
        {
            var weights=date.Weights;var sample=atlas.Sample(x,z,date);var intensity=SeasonClimate.Intensity(sample,weights);
            writer.Write(x);writer.Write(z);writer.Write(date.Month-1);writer.Write(date.NextMonth-1);writer.Write((float)date.Fraction);
            for(int p=0;p<3;p++)writer.Write(0f);
            foreach(double w in new[]{weights.Spring,weights.Summer,weights.Autumn,weights.Winter})writer.Write((float)w);
            foreach(double value in new[]{sample.TemperatureC,sample.PrecipitationMm,intensity.NewLeaves,intensity.Foliage,
                intensity.Autumn,intensity.LeafRetention,intensity.SnowCover,intensity.Snowfall,intensity.HeatHaze})writer.Write((float)value);
        }
        return vectors.Count;
    }
    private static void WriteClimateDds(string path,SeasonClimateAtlas atlas)
    {
        using var f=File.Create(path);using var b=new BinaryWriter(f);
        b.Write(0x20534444);b.Write(124);b.Write(0x100F);b.Write(atlas.Height);b.Write(atlas.Width);b.Write(atlas.Width*8);b.Write(0);b.Write(1);
        for(int i=0;i<11;i++)b.Write(0);b.Write(32);b.Write(4);b.Write(0x30315844);for(int i=0;i<5;i++)b.Write(0);
        b.Write(0x1008);for(int i=0;i<4;i++)b.Write(0);b.Write(16);b.Write(3);b.Write(0);b.Write(12);b.Write(0);
        foreach(float value in atlas.Data.Span)b.Write(value);
    }
    private static void SaveClimateContactSheet(string path,SeasonClimateAtlas atlas,SeasonClimate climate)
    {
        const int side=192,gap=24,columns=4;int width=columns*(side+gap),height=3*(side+gap)+52;
        var visual=new System.Windows.Media.DrawingVisual();
        using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.Black,null,new System.Windows.Rect(0,0,width,height));
            void Label(string value,int x,int y)=>dc.DrawText(new System.Windows.Media.FormattedText(value,
                System.Globalization.CultureInfo.InvariantCulture,System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("Segoe UI"),12,System.Windows.Media.Brushes.White,1),new System.Windows.Point(x,y));
            Label("Monthly normal temperature (blue -5 C .. orange 30 C); circles = source city anchors",8,8);
            Label("Created by San14Tenkazu from JMA ClimatView; asset-coordinate data preview",8,26);
            for(int month=1;month<=12;month++)
            {
                var pixels=new byte[side*side*4];var date=new MonthBlend(month,month==12?1:month+1,0);
                for(int y=0;y<side;y++)for(int x=0;x<side;x++)
                {
                    var c=atlas.Sample((x+0.5)/side*atlas.Extent.MaxX,(y+0.5)/side*atlas.Extent.MaxZ,date);
                    double t=Math.Clamp((c.TemperatureC+5)/35,0,1);int at=(y*side+x)*4;
                    pixels[at]=(byte)Math.Round(220*(1-t)+45*t);pixels[at+1]=(byte)Math.Round(95*(1-t)+130*t);
                    pixels[at+2]=(byte)Math.Round(45*(1-t)+240*t);pixels[at+3]=255;
                }
                var image=System.Windows.Media.Imaging.BitmapSource.Create(side,side,96,96,System.Windows.Media.PixelFormats.Bgra32,null,pixels,side*4);
                int ox=((month-1)%columns)*(side+gap)+8,oy=((month-1)/columns)*(side+gap)+72;
                Label($"{month:00}",ox,oy-18);dc.DrawImage(image,new System.Windows.Rect(ox,oy,side,side));
                foreach(var station in climate.Stations)
                    dc.DrawEllipse(null,new System.Windows.Media.Pen(System.Windows.Media.Brushes.White,1),
                        new System.Windows.Point(ox+station.X/atlas.Extent.MaxX*side,oy+station.Z/atlas.Extent.MaxZ*side),2,2);
            }
        }
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file=File.Create(path);encoder.Save(file);
    }
}

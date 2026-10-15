using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Seasons;

internal static partial class RenderChecks
{
    // Installation-file benchmark only: no process discovery, injection or game writes.
    private static void CheckSeasonPreparationTimings(string[] args)
    {
        string directory=args.FirstOrDefault(a=>a.StartsWith("--game-directory="))?[17..]??throw new ArgumentException("Game directory required");
        string output=args.FirstOrDefault(a=>a.StartsWith("--output="))?[9..]??throw new ArgumentException("Output directory required");
        int iterations=int.Parse(args.FirstOrDefault(a=>a.StartsWith("--iterations="))?[13..]??"5",CultureInfo.InvariantCulture);
        if(iterations is <1 or >20)throw new ArgumentOutOfRangeException(nameof(iterations));
        Directory.CreateDirectory(output);
        using var journal=new StreamWriter(Path.Combine(output,"stages.log")){AutoFlush=true};
        var samples=new List<object>();var beforeTimes=new List<double>();var afterTimes=new List<double>();
        void Log(string line)=>journal.WriteLine($"{DateTime.UtcNow:O} {line}");
        // Warm both paths before alternating their order to reduce first-use/JIT bias.
        byte[] warm=GameSeasonSources.ReadDirectory(directory);
        var expected=GameSeasonScene.ReadDirectory(directory);
        string? expectedHash=args.FirstOrDefault(a=>a.StartsWith("--expected-scene-sha256="))?[24..];
        if(expectedHash is not null&&!Convert.ToHexString(SHA256.HashData(expected)).Equals(expectedHash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Scene differs from the recorded pre-optimization payload");
        if(!expected.AsSpan().SequenceEqual(GameSeasonScene.ReadDirectory(directory,warm)))throw new InvalidDataException("Warmup payload differs");
        for(int iteration=0;iteration<iterations;iteration++)
        {
            byte[]? oldSources=null,newSources=null,oldScene=null,newScene=null;
            foreach(bool reuse in iteration%2==0?new[]{false,true}:new[]{true,false})
            {
                Log($"Sample: iteration={iteration}, sourceReused={reuse}");
                int materialReads=0;
                void SampleLog(string line)
                {
                    if(line.Contains("operation=material-read,")&&line.Contains("stage=read-and-authenticate,"))materialReads++;
                    Log(line);
                }
                var watch=Stopwatch.StartNew();
                byte[] sources=GameSeasonSources.ReadDirectory(directory,SampleLog);
                // Baseline reproduces the previous renderer: a source upload read plus
                // an independent scene read that reads the same material archive again.
                byte[] scene=reuse?GameSeasonScene.ReadDirectory(directory,sources,SampleLog):GameSeasonScene.ReadDirectory(directory,SampleLog);
                watch.Stop();
                if(materialReads!=(reuse?1:2))throw new InvalidDataException("Observed material read count differs");
                if(reuse){newSources=sources;newScene=scene;afterTimes.Add(watch.Elapsed.TotalMilliseconds);}
                else{oldSources=sources;oldScene=scene;beforeTimes.Add(watch.Elapsed.TotalMilliseconds);}
                samples.Add(new{iteration,sourceReused=reuse,materialReads,elapsedMs=watch.Elapsed.TotalMilliseconds});
            }
            if(!oldSources!.AsSpan().SequenceEqual(newSources)||!oldScene!.AsSpan().SequenceEqual(newScene)||!expected.AsSpan().SequenceEqual(newScene))
                throw new InvalidDataException("Before/after source or scene payload differs");
        }
        // Reuse must still authenticate all source bytes, including length and content.
        int rejected=0;
        foreach(byte[] invalid in new[]{warm[..^1],warm.Concat(new byte[1]).ToArray(),warm.ToArray()})
        {
            if(invalid.Length==warm.Length)invalid[invalid.Length/2]^=1;
            try{GameSeasonMasks.FromSources(invalid);}
            catch(InvalidDataException){rejected++;continue;}
            throw new InvalidDataException("Invalid reused material was accepted");
        }
        static double Median(List<double> values)=>values.Order().ElementAt(values.Count/2);
        var climate=SeasonClimate.Load();var extent=new ClimateExtent(0,0,204800,204800);
        var atlasSamples=new List<object>();var atlasBefore=new List<double>();var atlasAfter=new List<double>();
        float[] ReferenceAtlas()
        {
            const int size=SeasonSceneData.ClimateSize;var data=new float[size*size*12*2];
            // Original monthly loop retained as an independent numerical/performance reference.
            Parallel.For(0,12,new ParallelOptions{MaxDegreeOfParallelism=2},month=>
            {
                var date=new MonthBlend(month+1,month==11?1:month+2,0);
                for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                {
                    double gx=extent.MinX+(x+0.5)/size*(extent.MaxX-extent.MinX),gz=extent.MinZ+(y+0.5)/size*(extent.MaxZ-extent.MinZ);
                    var sample=climate.Sample(gx,gz,date);int at=((month*size+y)*size+x)*2;
                    data[at]=(float)sample.TemperatureC;data[at+1]=(float)sample.PrecipitationMm;
                }
            });return data;
        }
        var reference=ReferenceAtlas();
        if(!reference.AsSpan().SequenceEqual(SeasonClimateAtlas.Build(climate,SeasonSceneData.ClimateSize,SeasonSceneData.ClimateSize,extent).Data.Span))
            throw new InvalidDataException("Climate atlas differs from original monthly sampling");
        for(int iteration=0;iteration<iterations;iteration++)
            foreach(bool reuse in iteration%2==0?new[]{false,true}:new[]{true,false})
            {
                var watch=Stopwatch.StartNew();
                ReadOnlyMemory<float> values=reuse?SeasonClimateAtlas.Build(climate,SeasonSceneData.ClimateSize,SeasonSceneData.ClimateSize,extent).Data:ReferenceAtlas();
                watch.Stop();
                if(!reference.AsSpan().SequenceEqual(values.Span))throw new InvalidDataException("Timed climate atlas differs");
                (reuse?atlasAfter:atlasBefore).Add(watch.Elapsed.TotalMilliseconds);
                atlasSamples.Add(new{iteration,coordinateWeightsReused=reuse,elapsedMs=watch.Elapsed.TotalMilliseconds});
            }
        var result=new{gameAccess=false,gameFilesReadOnly=true,iterations,samples,atlasSamples,
            atlasBeforeMedianMs=Median(atlasBefore),atlasAfterMedianMs=Median(atlasAfter),atlasIdentical=true,
            beforeMedianMs=Median(beforeTimes),afterMedianMs=Median(afterTimes),
            payloadIdentical=true,sceneBytes=expected.Length,sceneSha256=Convert.ToHexString(SHA256.HashData(expected)),invalidSourcesRejected=rejected};
        string json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(Path.Combine(output,"results.json"),json);Console.WriteLine(json);
    }
}

using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void WriteLoadedClimateVectors(string path)
    {
        // Independent reference: retain the previous managed monthly interpolation
        // and rounding, then compare its output with the real Native climate body.
        using var stream=typeof(GameSnapshot).Assembly.GetManifestResourceStream("Seasons.climate.json")!;
        using var json=JsonDocument.Parse(stream);
        var sources=json.RootElement.GetProperty("stations").EnumerateArray().ToArray();
        using var writer=new BinaryWriter(new FileStream(path,FileMode.CreateNew));
        writer.Write(San14Tenkazu.EnvironmentRules.EnvironmentClimate.Pack(GeographyFixture.Create(51)));
        writer.Write(51*360);
        static (double X,double Y) Center(int tile)=>(1.5*(tile%220),Math.Sqrt(3)*(tile/220+((tile%220&1)==0?.5:0)));
        for(int city=1;city<=51;city++)
        {
            int tile=city<=46?MapTerrain.CityTiles[city]:(city-46)*6500;
            var point=Center(tile);var monthly=new (int T,int P)[12];
            for(int month=0;month<12;month++)
            {
                double t=0,p=0,weight=0;
                foreach(var source in sources)
                {
                    var anchor=Center(source.GetProperty("cityTile").GetInt32());
                    double distance=Math.Pow(anchor.X-point.X,2)+Math.Pow(anchor.Y-point.Y,2);
                    double st=source.GetProperty("temperatureC")[month].GetDouble(),sp=source.GetProperty("precipitationMm")[month].GetDouble();
                    if(distance<1e-12){t=st;p=sp;weight=1;break;}
                    double w=1/distance;t+=st*w;p+=sp*w;weight+=w;
                }
                monthly[month]=((int)Math.Round(t/weight*10),(int)Math.Round(p/weight*10));
            }
            for(int month=0;month<12;month++)for(int day=0;day<30;day++)
            {
                var a=monthly[month];var b=monthly[(month+1)%12];
                writer.Write((uint)tile);writer.Write(192u|((uint)(month+1)<<16)|((uint)(day+1)<<24));
                writer.Write(a.T+(b.T-a.T)*day/30);writer.Write(a.P+(b.P-a.P)*day/30);
            }
        }
        Console.WriteLine("Exported 18360 original/expanded loaded-position climate cases; gameAccess=false");
    }

    // Owned snapshots and WPF controls only. No editor startup, live process access or developer-menu actions.
    private static void CheckLoadedGeography(string output, string? nativeFixture = null, string? patchMap = null)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        int count = 0;
        void Check(string name, bool passed)
        {
            log.WriteLine(JsonSerializer.Serialize(new { name, passed, gameAccess = false }));
            Require(passed, name); count++;
        }
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        Environment.SetEnvironmentVariable("SAN14_NO_GPU", "1");
        var surface = new MapSurface();
        surface.Measure(new W.Size(1000, 700)); surface.Arrange(new W.Rect(0, 0, 1000, 700));
        var ordinary = (byte[])MapTerrain.Terrain.Clone();
        var changed = (byte[])ordinary.Clone();
        int marker = Array.FindIndex(ordinary, v => v is >= 1 and <= 12);
        changed[marker] = MapTerrain.Port;
        var examples = new List<(string Name, GameSnapshot Data, int Cities)>
        {
            ("original", GeographyFixture.Create(46, terrain: ordinary), 46),
            ("alternate-49", GeographyFixture.Create(49, 1500, changed), 49),
            ("alternate-51", GeographyFixture.Create(51, 2600, changed), 51),
            ("original-after-mod", GeographyFixture.Create(46, terrain: ordinary), 46)
        };
        if (nativeFixture is not null)
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(nativeFixture));
            foreach (var item in json.RootElement.EnumerateArray())
            {
                string name = item.GetProperty("Kind").GetString()!;
                CityRecord[] records = item.GetProperty("Records").EnumerateArray().Select(row =>
                    new CityRecord(row.GetProperty("Id").GetInt32(), (ulong)(0x1000000 + row.GetProperty("Id").GetInt32() * 0x1000),
                        Convert.FromHexString(row.GetProperty("Data").GetString()!))).ToArray();
                byte[] terrain = name == "patch" && patchMap is not null ? DecodeMap(patchMap) : ordinary;
                examples.Add(("native-" + name, GeographyFixture.Create(terrain: terrain, nativeCities: records), name == "stock" ? 46 : 51));
            }
        }
        try
        {
            foreach (var (name, data, expected) in examples)
            {
                surface.SetSnapshot(data);
                Check(name + "-city-count", data.CityIds.Count() == expected);
                Check(name + "-tribal-count", data.Tribes.Length == 51 - expected);
                var models = MapModels.All(data);
                Check(name + "-city-models-use-only-observed-identities", models.Count(p => p.Model.Contains("-city-", StringComparison.Ordinal) && data.CityIds.Any(id => data.CityTile(id) == p.Tile)) ==
                    data.CityIds.Count(id => CityNames.ById.Skip(1).Take(46).Contains(data.Cities[id].Name)));
                foreach (int id in data.CityIds)
                {
                    int tile = data.CityTile(id)!.Value;
                    Check(name + "-city-" + id, data.BaseTile(id) == tile && data.TileName(tile) == data.CityName(id));
                    var place = ((int? Tile, string? Name))typeof(EditorWindow).GetMethod("PlaceOf", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, [data, new CityTarget(id)])!;
                    Check(name + "-navigation-" + id, place.Tile == tile && place.Name == data.CityName(id));
                }
                if (name == "native-patch")
                    Check("native-patch-keeps-renamed-existing-and-added-cities", data.CityName(2) == "거록" && data.CityName(47) == "북평" && data.CityName(51) == "운남");
            }
            var missing = GeographyFixture.Create(51);
            Array.Clear(missing.Cities[51].Data, CityFields.NameOffset, 8);
            missing.CastleByCity.Remove(51);
            surface.SetSnapshot(missing);
            Check("missing-castle-does-not-invent-a-location", missing.CityTile(51) is null && missing.BaseTile(51) is null);
            Check("missing-name-does-not-reuse-tribal-name", missing.CityName(51) == $"{L.Columns.City} 51");
            var view = (CityEditorCard.View)typeof(EditorWindow).GetMethod("CityView", Hidden | BindingFlags.Static)!.Invoke(null, [missing, 51])!;
            Check("missing-castle-is-absent-in-editor-values", !view.Values.ContainsKey(CityFields.Durability) && view.Values.ContainsKey(CityFields.Gold));
            var card = new CityEditorCard();card.Open(view);
            var fields = (Dictionary<FieldDef, NumberField>)typeof(CityEditorCard).GetField("fields", Hidden)!.GetValue(card)!;
            Check("missing-castle-disables-only-its-editor-field", !fields[CityFields.Durability].Box.IsEnabled && fields[CityFields.Gold].Box.IsEnabled);
            Check("unknown-city-does-not-guess-original-model", MapModels.CityModel(missing.Cities[51]) is null);
            System.Text.Encoding.Unicode.GetBytes(CityNames.ById[1]).CopyTo(missing.Cities[51].Data, CityFields.NameOffset);
            Check("known-city-model-follows-loaded-name-in-new-slot", MapModels.CityModel(missing.Cities[51]) == "obj-mc01-s-city-b-nm-001");
            missing.Cities[51].Data[CityFields.Scale.Offset] = 0;
            Check("missing-scale-does-not-guess-model-size", MapModels.CityModel(missing.Cities[51]) is null);
            var a = GeographyFixture.Create(51, terrain: ordinary);
            var b = GeographyFixture.Create(51, terrain: changed);
            Check("loaded-port-is-added", !MapModels.All(a).Any(p => p.Tile == marker) && MapModels.All(b).Any(p => p.Tile == marker && p.Model.Contains("port")));
            string Hash(WMI.BitmapSource image)
            {
                byte[] bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0);
                return Convert.ToHexString(SHA256.HashData(bytes));
            }
            int level = 4; var world = MapTerrain.TileCenter(marker); float ppu = MapTiles.PixelsPerUnit(level);
            int x = (int)(world.X * ppu / MapTiles.TileSize), y = (int)(world.Y * ppu / MapTiles.TileSize);
            var before = MapView.RenderTile(level, x, y, null, null, true, ordinary);
            var after = MapView.RenderTile(level, x, y, null, null, true, changed);
            Check("cpu-detail-changes-with-loaded-terrain", Hash(before) != Hash(after));
            Check("cpu-original-is-restored", Hash(before) == Hash(MapView.RenderTile(level, x, y, null, null, true, ordinary)));
            surface.SetSnapshot(b);
            var layer = (MapTiles)typeof(MapSurface).GetField("tiles", Hidden)!.GetValue(surface)!;
            Check("surface-passes-terrain-to-workers", ReferenceEquals(typeof(MapTiles).GetField("terrain", Hidden)!.GetValue(layer), changed));
            surface.SetSnapshot(a);
            Check("surface-restores-original-worker-terrain", ReferenceEquals(typeof(MapTiles).GetField("terrain", Hidden)!.GetValue(layer), ordinary));
            var mapRoutes = MapRoutes.Find(1000, 1002, ordinary);
            Check("route-accepts-loaded-terrain", mapRoutes is null || mapRoutes[0] == 1000 && mapRoutes[^1] == 1002);
            Environment.SetEnvironmentVariable("SAN14_NO_GPU", null);
            using (GpuMap gpu = GpuMap.TryCreate() ?? throw new InvalidOperationException("GPU renderer unavailable"))
            {
                var shaders = gpu.Shaders ?? throw new InvalidOperationException("GPU border shaders unavailable");
                const int size = 256;
                gpu.Resize(size, size, 96);
                var camera = new MapShaders.Camera(32, size / 2 - world.X * 32, size / 2 - world.Y * 32);
                byte[] Draw(byte[] terrain)
                {
                    var commands = new MapDrawList();
                    commands.Clear(WM.Colors.White);
                    commands.DrawBorders(shaders, camera, null, null, terrain);
                    Require(gpu.RenderAsync(commands), "Geography GPU frame was rejected"); Finish(gpu);
                    return ReadPixels(gpu, size, size);
                }
                byte[] originalPixels = Draw(ordinary), changedPixels = Draw(changed);
                Check("gpu-detail-changes-with-loaded-terrain", !originalPixels.AsSpan().SequenceEqual(changedPixels));
                Check("gpu-original-is-restored", originalPixels.AsSpan().SequenceEqual(Draw(ordinary)));
                Check("gpu-remembers-the-current-terrain", ReferenceEquals(typeof(MapShaders).GetField("hexTerrain", Hidden)!.GetValue(shaders), ordinary));
            }
            Console.WriteLine($"PASS loaded geography: {count} checks; gameAccess=false");
        }
        finally { app.Shutdown(); }

        static byte[] DecodeMap(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            if (file.Length != 4 + GameMemory.TileCount * 16 || BinaryPrimitives.ReadInt32LittleEndian(file) != GameMemory.TileCount)
                throw new InvalidDataException("Map record layout differs");
            var result = new byte[GameMemory.TileCount];
            for (int tile = 0; tile < result.Length; tile++)
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4 + tile * 16)) != tile) throw new InvalidDataException("Map index differs");
                ushort terrain = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(8 + tile * 16));
                if (terrain > 24) throw new InvalidDataException("Map terrain differs");
                result[tile] = (byte)terrain;
            }
            return result;
        }
    }
}

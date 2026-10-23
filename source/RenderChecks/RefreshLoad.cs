using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;

internal static partial class RenderChecks
{
    // The same fixture and measured operations are used before and after the optimization.
    // No process discovery, game connection, writes, or visible editor window.
    private static void CheckRefreshLoad(string output)
    {
        Directory.CreateDirectory(output);
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        var builder = OriginalMapCastles(new TestGame());
        for (int force = 1; force <= 30; force++) builder.Force(force, force, force);
        for (int city = 1; city <= GameMemory.CityCount; city++) builder.City(city, (city - 1) % 30 + 1);
        builder.Gate(52, 1); // supplies complete tile ownership/county arrays, as a live snapshot does
        for (int officer = 1; officer <= 1800; officer++)
        {
            builder.Officer(officer, "장수" + officer, (officer - 1) % 30 + 1, 1, 3);
            if (officer <= 180) builder.Leading(officer);
        }
        GameSnapshot data = builder.Build();
        for (int i = 0; i < data.Units.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.Units[i].Data.AsSpan(UnitFields.TileOffset),
                (ushort)(220 * (104 + i / 30 * 2) + 80 + i % 30 * 3));
        var window = OfflineEditor(() => false, ruleStorageRoot: Path.Combine(output, "settings"));
        var fields = typeof(EditorWindow);
        var map = (MapView)fields.GetField("map", Hidden)!.GetValue(window)!;
        var fill = fields.GetMethod("FillLists", Hidden)!;
        fields.GetField("snapshot", Hidden)!.SetValue(window, data);
        var samples = new List<object>();
        void Measure(string phase, Action action, int count = 40)
        {
            for (int i = 0; i < 8; i++) action();
            for (int i = 0; i < count; i++)
            {
                long bytes = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                action();
                double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                samples.Add(new { phase, milliseconds, allocated });
            }
        }
        try
        {
            map.ShowList(ListKind.Forces);
            Measure("force-list-refresh", () => fill.Invoke(window, null));
            map.ShowList(ListKind.Officers);
            Measure("officer-list-refresh", () => fill.Invoke(window, null));
            map.ShowLists(false);
            Measure("hidden-lists-refresh", () => fill.Invoke(window, null));
            var surface = new MapSurface { Width = 1280, Height = 800 };
            surface.Measure(new W.Size(1280, 800)); surface.Arrange(new W.Rect(0, 0, 1280, 800));
            var surfaceType = typeof(MapSurface);
            surfaceType.GetField("zoom", Hidden)!.SetValue(surface, 4.0);
            surfaceType.GetMethod("ApplyView", Hidden)!.Invoke(surface, null);
            surface.SetSnapshot(data);
            var draw = surfaceType.GetMethod("DrawGpuFrame", Hidden)!;
            var frame = new MapDrawList();
            Measure("unchanged-city-frame", () => { frame.Reset(); draw.Invoke(surface, [frame]); }, 80);
            ((GpuMap?)surfaceType.GetField("gpu", Hidden)!.GetValue(surface))?.Dispose();
            File.WriteAllText(Path.Combine(output, "measurements.json"), JsonSerializer.Serialize(new
            {
                gameAccess = false, officers = data.Officers.Length, units = data.Units.Length, samples
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS refresh load measurement; gameAccess=false");
        }
        finally
        {
            map.HideEditors(); window.Close(); app.Shutdown();
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    // Exercise snapshot-driven glides without pointer input, with a genuinely inactive, partially
    // covered native window. Inspect the complete shared texture at WPF's actual copy request.
    private static void CheckMotion(string output, bool quick = false)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", Path.GetFullPath(Path.Combine(output, "frames.csv")));
        // Show the real banner sprites in this native test, as the editor does after loading art.
        if (FaceLibrary.FindGameDirectory(null) is { } directory) MapArt.Load(FaceLibrary.Load(directory));
        static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Hidden)!.GetValue(obj)!;
        static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Hidden)!.SetValue(obj, value);
        static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Hidden | System.Reflection.BindingFlags.Static)!.Invoke(obj, args);
        var map = new MapView { IsHitTestVisible = false };
        var surface = Field<MapSurface>(map, "surface");
        var gpu = Field<GpuMap?>(surface, "gpu") ?? throw new InvalidOperationException("GPU renderer unavailable");
        var host = new W.Window { Content = map, Width = 1280, Height = 800, Left = 20, Top = 20,
            ShowActivated = false, ShowInTaskbar = false, Title = "천하도 부대 이동 렌더링 검사" };
        var other = new W.Window { Content = new WC.TextBox { Text = "Inactive / partial cover" },
            Width = 260, Height = 130, Left = 980, Top = 150, ShowActivated = false, ShowInTaskbar = false,
            Title = "천하도 가림 검사" };
        var game = new TestGame().Force(1, 1, 1).Officer(1, "장수", 1, 1, 4).Leading(1);
        // A city with no assigned corps is neutral and has no mark on its shield. Populate ownership
        // explicitly so this motion scene also exercises the banner's mark instead of empty flags.
        for (int city = 1; city <= GameMemory.CityCount; city++) game.City(city, 1);
        GameSnapshot made = game.Build();
        int markedCities = Enumerable.Range(1, GameMemory.CityCount).Count(city => made.ForceMark(made.ForceOfCity(city)).Length > 0);
        Require(markedCities == GameMemory.CityCount, "Motion scene contains empty city banner marks");
        int row = MapTerrain.Rows / 2, column = MapTerrain.Columns / 2;
        int initialTile = row * MapTerrain.Columns + column;
        const double snapshotIntervalSeconds = 2; // same sampling cadence as the editor
        GameSnapshot Snapshot(int tile)
        {
            UnitRecord unit = made.Units[0] with { Data = (byte[])made.Units[0].Data.Clone() };
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(unit.Data.AsSpan(UnitFields.TileOffset), (ushort)tile);
            UnitFields.Troops.Encode(7007).CopyTo(unit.Data, UnitFields.Troops.Offset);
            return new GameSnapshot { Cities = made.Cities, Counties = made.Counties, Officers = made.Officers,
                CastleByCity = made.CastleByCity, TileOwners = new byte[GameMemory.TileCount],
                TileCounties = new ushort[GameMemory.TileCount], Forces = made.Forces, ForceRecords = made.ForceRecords,
                CorpsForce = made.CorpsForce, OfficerById = made.OfficerById, Units = [unit] };
        }
        map.SetSnapshot(Snapshot(initialTile));
        var clock = Stopwatch.StartNew();
        var records = new List<object>();
        var summaries = new List<object>();
        string phase = "load";
        int copies = 0, sharedWrites = 0, changedStatic = 0, inspected = 0;
        int movingCopies = 0, inactiveCopies = 0, glideCallbacks = 0;
        byte[]? reference = null;
        var glideFrameTimes = new HashSet<TimeSpan>();
        EventHandler sample = (_, e) =>
        {
            if (reference is null || !Field<bool>(surface, "gliding")) return;
            glideCallbacks++;
            glideFrameTimes.Add(((WM.RenderingEventArgs)e).RenderingTime);
        };
        W.Rect movingArea = W.Rect.Empty;
        var dirty = typeof(System.Windows.Interop.D3DImage).GetField("_isDirty", Hidden)!;
        var locks = typeof(System.Windows.Interop.D3DImage).GetField("_lockCount", Hidden)!;
        var callback = typeof(System.Windows.Interop.D3DImage).GetField("_sendPresentDelegate", Hidden)!;
        var original = (EventHandler)callback.GetValue(gpu.Image)!;
        callback.SetValue(gpu.Image, new EventHandler((sender, e) =>
        {
            bool before = (bool)dirty.GetValue(gpu.Image)!;
            uint count = (uint)locks.GetValue(gpu.Image)!;
            bool writing = count != 0 || gpu.Pending && ReferenceEquals(Field<object?>(gpu, "front"), Field<object?>(gpu, "back"));
            int differences = 0, changedInside = 0;
            if (before && reference is not null && !gpu.Recording)
            {
                byte[] actual = ReadPixels(gpu, gpu.Image.PixelWidth, gpu.Image.PixelHeight);
                for (int i = 0; i < actual.Length; i += 4)
                {
                    if (actual.AsSpan(i, 4).SequenceEqual(reference.AsSpan(i, 4))) continue;
                    var pixel = new W.Point(i / 4 % gpu.Image.PixelWidth, i / 4 / gpu.Image.PixelWidth);
                    if (movingArea.Contains(pixel)) changedInside++; else differences++;
                }
                inspected++;
                if (differences > 0) changedStatic++;
            }
            original(sender, e);
            if (before && !(bool)dirty.GetValue(gpu.Image)!)
            {
                copies++;
                if (changedInside > 0) movingCopies++;
                if (reference is not null && !host.IsActive) inactiveCopies++;
                if (writing) sharedWrites++;
                records.Add(new { ms = clock.Elapsed.TotalMilliseconds, phase, active = host.IsActive,
                    gpu.Pending, locks = count, writing, differences, changedInside,
                    gpu.SubmittedFrames, gpu.PresentedFrames });
            }
        }));
        void Pump(double seconds, Action<double>? work = null)
        {
            double start = clock.Elapsed.TotalSeconds;
            while (clock.Elapsed.TotalSeconds - start < seconds)
            {
                work?.Invoke(clock.Elapsed.TotalSeconds - start);
                var frame = new WT.DispatcherFrame();
                surface.Dispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                WT.Dispatcher.PushFrame(frame);
                Thread.Sleep(2);
            }
        }
        try
        {
            host.Show(); other.Show();
            WM.CompositionTarget.Rendering += sample;
            Pump(10);
            Require(Field<WC.Image>(surface, "terrain").Source is not null, "Terrain did not load");
            foreach (double zoom in quick ? new[] { 4.0 } : new[] { 4.0, 8.0, 16.0 })
            {
                reference = null;
                phase = "warmup-" + zoom;
                Set(surface, "zoom", zoom);
                Set(surface, "center", (W.Point)Call(surface, "TileWorld", initialTile)!);
                map.SetSnapshot(Snapshot(initialTile));
                Call(surface, "ApplyView");
                Pump(3);
                while (gpu.Pending) Pump(.01);
                reference = ReadPixels(gpu, gpu.Image.PixelWidth, gpu.Image.PixelHeight);
                W.Point from = (W.Point)Call(surface, "ToScreen", Call(surface, "TileWorld", initialTile)!)!;
                W.Point to = (W.Point)Call(surface, "ToScreen", Call(surface, "TileWorld", initialTile + 1)!)!;
                double k = WM.VisualTreeHelper.GetDpi(surface).DpiScaleX;
                movingArea = new W.Rect(from, to);
                movingArea.Inflate(170, 170); // portrait, troop figures and force shield along this two-tile path
                movingArea = new W.Rect(movingArea.X * k, movingArea.Y * k, movingArea.Width * k, movingArea.Height * k);
                foreach (bool active in new[] { true, false })
                {
                    phase = (active ? "active-motion-" : "inactive-motion-") + zoom;
                    if (active) host.Activate(); else other.Activate();
                    int beforeCopies = copies, beforeFrames = gpu.PresentedFrames;
                    int tick = -1;
                    Pump(6, seconds =>
                    {
                        int next = (int)(seconds / snapshotIntervalSeconds);
                        if (next == tick) return;
                        tick = next;
                        map.SetSnapshot(Snapshot(initialTile + next % 2));
                    });
                    Pump(1);
                    summaries.Add(new { phase, actualActive = host.IsActive,
                        copies = copies - beforeCopies, presented = gpu.PresentedFrames - beforeFrames });
                }
            }
            var result = new { copies, inspected, sharedWrites, changedStatic, movingCopies, inactiveCopies, markedCities,
                snapshotIntervalSeconds,
                gameShieldSprites = MapArt.Has(MapArt.Shield) && MapArt.Has(MapArt.ShieldOutline),
                glideCallbacks, uniqueGlideFrames = glideFrameTimes.Count, gpu.Lost, gpu.BackBufferBindings, phases = summaries };
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "copies.json"), JsonSerializer.Serialize(records));
            Console.WriteLine(JsonSerializer.Serialize(result));
            Require(inspected >= (quick ? 30 : 60) && movingCopies >= 20 && inactiveCopies >= 20,
                "Too few actual WPF frames with visible motion and an inactive window were inspected");
            Require(sharedWrites == 0 && changedStatic == 0 && !gpu.Lost,
                "Unit motion exposed unfinished shared pixels or changed the static map outside the unit's path");
        }
        finally
        {
            reference = null;
            WM.CompositionTarget.Rendering -= sample;
            host.Close(); other.Close();
            var glide = typeof(MapSurface).GetMethod("OnGlide", Hidden)!.CreateDelegate<EventHandler>(surface);
            WM.CompositionTarget.Rendering -= glide;
            var log = Field<MapRenderLog?>(surface, "renderLog");
            log?.Dispose(); log?.Completion.GetAwaiter().GetResult();
            gpu.Dispose();
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using Vortice.Direct3D11;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    // Use a real WPF compositor. Fixtures are the default; live-scene reads a snapshot without writes.
    private static void CheckDrag(string output, bool swipe = false, bool zoomReplay = false, bool liveScene = false, bool capture = false)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", Path.GetFullPath(Path.Combine(output, "frames.csv")));
        static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Hidden)!.GetValue(obj)!;
        static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Hidden)!.SetValue(obj, value);
        static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Hidden)!.Invoke(obj, args);
        var window = OfflineEditor();
        var root = (W.FrameworkElement)window.Content;
        var map = Field<MapView>(window, "map");
        var surface = Field<MapSurface>(map, "surface");
        var gpu = Field<GpuMap?>(surface, "gpu") ?? throw new InvalidOperationException("GPU renderer unavailable");
        window.Content = null;
        // Replay calls the same view methods as native input. Keep physical input from changing
        // its camera/hover/card state while measuring the real WPF compositor's frame delivery.
        root.IsHitTestVisible = false;
        var host = new W.Window { Content = root, Width = 1280, Height = 800, ShowActivated = false, ShowInTaskbar = false,
            Title = "천하도 렌더링 검증", FontFamily = window.FontFamily, FontSize = window.FontSize };
        var fixture = OriginalMapCastles(new TestGame().Force(1, 1, 1));
        for (int i = 1; i <= 180; i++) fixture.Officer(i, "장수" + i, 1, 1, 4).Leading(i);
        GameSnapshot made = fixture.Build();
        var data = new GameSnapshot { Cities = made.Cities, Counties = made.Counties, Officers = made.Officers,
            CastleByCity = made.CastleByCity, TileOwners = new byte[GameMemory.TileCount], TileCounties = new ushort[GameMemory.TileCount],
            Forces = made.Forces, ForceRecords = made.ForceRecords, CorpsForce = made.CorpsForce, OfficerById = made.OfficerById, Units = made.Units };
        for (int i = 0; i < data.Units.Length; i++)
        {
            UnitRecord unit = data.Units[i];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(unit.Data.AsSpan(UnitFields.TileOffset), (ushort)(220 * (104 + i / 30 * 2) + 80 + (i % 30) * 3));
            UnitFields.Troops.Encode(3000 + i * 73).CopyTo(unit.Data, UnitFields.Troops.Offset);
        }
        if (liveScene)
        {
            GameIdentity identity = GameMemory.FindGame();
            GameLayout layout = GameMemory.Resolve(identity, Console.WriteLine, CancellationToken.None, []);
            data = GameMemory.ReadSnapshot(identity, layout);
            Console.WriteLine(JsonSerializer.Serialize(new { liveScene = true, units = data.Units.Length, cities = data.Cities.Length }));
        }
        map.SetSnapshot(data);
        var clock = Stopwatch.StartNew();
        string phase = "load";
        var frames = new List<object>();
        var inputMs = new List<double>();
        var inputs = new List<object>();
        var dispatchAt = new Dictionary<WT.DispatcherOperation, double>();
        var slowDispatches = new List<object>();
        WT.DispatcherHookEventHandler dispatchStarted = (_, e) => dispatchAt[e.Operation] = clock.Elapsed.TotalMilliseconds;
        WT.DispatcherHookEventHandler dispatchCompleted = (_, e) =>
        {
            if (!dispatchAt.Remove(e.Operation, out double start)) return;
            double duration = clock.Elapsed.TotalMilliseconds - start;
            if (duration < 20) return;
            var method = typeof(WT.DispatcherOperation).GetField("_method", Hidden)?.GetValue(e.Operation) as Delegate;
            slowDispatches.Add(new { ms = start, phase, duration, priority = e.Operation.Priority.ToString(),
                method = method?.Method.ToString(), owner = method?.Target?.GetType().FullName, gpu.Pending, gpu.Recording });
        };
        var gaps = new List<double>();
        var phaseGaps = new Dictionary<string, List<double>>();
        var releases = new List<object>();
        bool allCoastsSettled = true;
        int coastsStarted = 0;
        int availableChanges = 0, lostFrames = 0, zeroSizeFrames = 0, emptyAfterFirstFrame = 0, inputCount = 0;
        double lastPresentedAt = 0;
        int previousPresented = 0;
        string previousPhase = "";
        var context = Field<ID3D11DeviceContext>(gpu, "immediate");
        var query = Field<ID3D11Query>(gpu, "finished");
        var writable = Field<ManualResetEvent>(gpu.Image, "_canWriteEvent");
        var imageVersion = typeof(System.Windows.Interop.D3DImage).GetField("_version", Hidden)!;
        var imageDirty = typeof(System.Windows.Interop.D3DImage).GetField("_isDirty", Hidden)!;
        var imageLocks = typeof(System.Windows.Interop.D3DImage).GetField("_lockCount", Hidden)!;
        var presentDelegate = typeof(System.Windows.Interop.D3DImage).GetField("_sendPresentDelegate", Hidden)!;
        var originalPresent = (EventHandler)presentDelegate.GetValue(gpu.Image)!;
        int wpfCopies = 0;
        int copiesWhileWriting = 0;
        var copyTimes = new List<object>();
        var copyGaps = new List<double>();
        var cameraAges = new List<double>();
        int exerciseCopies = 0;
        double previousCopyAt = 0;
        string previousCopyPhase = "";
        int terrainCopies = 0, seaOnlyCopies = 0;
        var screenFrames = new List<object>();
        int screenSeaFrames = 0, screenChanges = 0;
        string? previousScreenHash = null;
        var screenTimer = new WT.DispatcherTimer(WT.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        screenTimer.Tick += (_, _) =>
        {
            if (phase is not ("zoom" or "zoom-resume" or "zoom-drag")) return;
            var captured = CaptureMap(surface, screenFrames.Count == 0 ? Path.Combine(output, "screen-first.png") : null);
            if (captured.Land == 0)
            {
                if (screenSeaFrames++ == 0) CaptureMap(surface, Path.Combine(output, "screen-sea.png"));
            }
            if (captured.Hash != previousScreenHash) screenChanges++;
            previousScreenHash = captured.Hash;
            screenFrames.Add(new { ms = clock.Elapsed.TotalMilliseconds, captured.Land, captured.Hash });
        };
        var mediaType = typeof(WM.CompositionTarget).Assembly.GetType("System.Windows.Media.MediaContext")!;
        var media = mediaType.GetMethod("From", Hidden | System.Reflection.BindingFlags.Static)!.Invoke(null, [surface.Dispatcher])!;
        object? MediaField(string name) => mediaType.GetField(name, Hidden)?.GetValue(media);
        presentDelegate.SetValue(gpu.Image, new EventHandler((sender, e) =>
        {
            bool before = (bool)imageDirty.GetValue(gpu.Image)!;
            originalPresent(sender, e);
            if (before && !(bool)imageDirty.GetValue(gpu.Image)!)
            {
                wpfCopies++;
                bool sharedWrite = (uint)imageLocks.GetValue(gpu.Image)! != 0
                    || (gpu.Pending && ReferenceEquals(Field<object?>(gpu, "front"), Field<object?>(gpu, "back")));
                if (sharedWrite) copiesWhileWriting++;
                double at = clock.Elapsed.TotalMilliseconds;
                bool exercise = phase is "drag" or "zoom" or "zoom-resume" or "zoom-drag" || phase.StartsWith("coast-") || phase.StartsWith("fast-drag-");
                var camera = Field<(double Zoom, bool Moving, W.Point Center, double At)>(surface, "presentedView");
                double cameraAgeMs = (Field<Stopwatch>(surface, "clock").Elapsed.TotalSeconds - camera.At) * 1000;
                if (exercise)
                {
                    if (previousCopyPhase == phase) copyGaps.Add(at - previousCopyAt);
                    previousCopyAt = at;
                    exerciseCopies++;
                    cameraAges.Add(cameraAgeMs);
                }
                previousCopyPhase = phase;
                int landSamples = -1;
                if (capture && zoomReplay && exercise && !gpu.Recording)
                {
                    byte[] pixels = ReadPixels(gpu, gpu.Image.PixelWidth, gpu.Image.PixelHeight);
                    WM.Color background = ((WM.SolidColorBrush)surface.Background).Color;
                    landSamples = 0;
                    for (int y = gpu.Image.PixelHeight / 4; y < gpu.Image.PixelHeight * 3 / 4; y += 12)
                    for (int x = gpu.Image.PixelWidth / 4; x < gpu.Image.PixelWidth * 3 / 4; x += 12)
                    {
                        int p = (y * gpu.Image.PixelWidth + x) * 4;
                        if (Math.Abs(pixels[p] - background.B) > 2 || Math.Abs(pixels[p + 1] - background.G) > 2 || Math.Abs(pixels[p + 2] - background.R) > 2) landSamples++;
                    }
                    terrainCopies++;
                    if (landSamples == 0) seaOnlyCopies++;
                }
                copyTimes.Add(new { ms = at, phase, gpu.PresentedFrames,
                    gpu.Pending, locks = imageLocks.GetValue(gpu.Image), sharedWrite, landSamples,
                    interlock = MediaField("_interlockState")?.ToString(), result = MediaField("_lastPresentationResults")?.ToString(),
                    refresh = MediaField("_animationRenderRate"), zoom = Field<double>(surface, "zoom"), cameraZoom = camera.Zoom, cameraCenter = camera.Center, cameraAgeMs });
            }
        }));
        var queryReadyAt = new Dictionary<int, double>();
        var submittedAt = new Dictionary<int, double>();
        gpu.Image.IsFrontBufferAvailableChanged += (_, _) => availableChanges++;
        EventHandler sample = (_, _) =>
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (gpu.PresentedFrames != previousPresented)
            {
                if (phase == "drag" && previousPhase == "drag") gaps.Add(now - lastPresentedAt);
                if (phase == previousPhase)
                {
                    if (!phaseGaps.TryGetValue(phase, out var measured)) phaseGaps[phase] = measured = new();
                    measured.Add(now - lastPresentedAt);
                }
                previousPresented = gpu.PresentedFrames;
                lastPresentedAt = now;
                previousPhase = phase;
            }
            if (gpu.Lost) lostFrames++;
            if (gpu.Image.PixelWidth == 0 || gpu.Image.PixelHeight == 0) zeroSizeFrames++;
            if (gpu.PresentedFrames > 0 && (gpu.Image.PixelWidth == 0 || gpu.Image.PixelHeight == 0)) emptyAfterFirstFrame++;
            frames.Add(new { ms = now, phase, submitted = gpu.SubmittedFrames, presented = gpu.PresentedFrames,
                pending = gpu.Pending, lost = gpu.Lost, available = gpu.Image.IsFrontBufferAvailable,
                width = gpu.Image.PixelWidth, height = gpu.Image.PixelHeight, drawMs = gpu.LastFrame.Draw,
                endMs = gpu.LastFrame.End, submitMs = gpu.LastFrame.Submit, delayMs = gpu.LastPresentDelay, gpuWaitMs = gpu.LastGpuWait,
                writable = writable.WaitOne(0), interopVersion = imageVersion.GetValue(gpu.Image), tier = WM.RenderCapability.Tier >> 16,
                center = Field<W.Point>(surface, "center"), velocity = Field<W.Vector>(surface, "velocity"),
                looping = Field<bool>(surface, "looping"), dragged = Field<bool>(surface, "dragged"),
                wpfCopies, imageDirty = imageDirty.GetValue(gpu.Image), imageLocks = imageLocks.GetValue(gpu.Image) });
        };
        void Pump(double seconds, Action<double>? work = null)
        {
            double start = clock.Elapsed.TotalSeconds;
            while (clock.Elapsed.TotalSeconds - start < seconds)
            {
                work?.Invoke(clock.Elapsed.TotalSeconds - start);
                var dispatcherFrame = new WT.DispatcherFrame();
                surface.Dispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => dispatcherFrame.Continue = false));
                WT.Dispatcher.PushFrame(dispatcherFrame);
                if (gpu.Pending && !gpu.Recording)
                {
                    submittedAt.TryAdd(gpu.SubmittedFrames, clock.Elapsed.TotalMilliseconds);
                    if (context.GetData(query, IntPtr.Zero, 0, AsyncGetDataFlags.DoNotFlush).Code == 0)
                        queryReadyAt.TryAdd(gpu.SubmittedFrames, clock.Elapsed.TotalMilliseconds);
                }
                Thread.Sleep(2);
            }
        }
        try
        {
            surface.Dispatcher.Hooks.OperationStarted += dispatchStarted;
            surface.Dispatcher.Hooks.OperationCompleted += dispatchCompleted;
            host.Show();
            host.Activate();
            var faceIcons = new Dictionary<int, IconCache.Icon>();
            if (liveScene)
            {
                string directory = FaceLibrary.FindGameDirectory(null) ?? throw new InvalidOperationException("Game portraits unavailable");
                FaceLibrary library = FaceLibrary.Load(directory);
                int size = (int)Math.Round(56 * WM.VisualTreeHelper.GetDpi(surface).DpiScaleX);
                faceIcons = IconCache.Load(library.Stamp, size);
                foreach (int face in data.Units.Select(unit => data.OfficerById.GetValueOrDefault(unit.Commander)?.Face ?? -1).Where(face => face >= 0).Distinct())
                    if (!faceIcons.ContainsKey(face)) faceIcons[face] = new IconCache.Icon(library.IconPixels(face, size), size);
                Console.WriteLine(JsonSerializer.Serialize(new { portraitSize = size, portraits = faceIcons.Count }));
            }
            else
            {
                // Include cold portrait mipmaps in fixture runs, as in the connected editor.
                var pixels = new byte[128 * 128 * 4];
                for (int p = 0; p < pixels.Length; p += 4) { pixels[p] = 90; pixels[p + 1] = 170; pixels[p + 2] = 210; pixels[p + 3] = 255; }
                foreach (int face in data.Officers.Select(officer => officer.Face).Distinct()) faceIcons[face] = new IconCache.Icon(pixels, 128);
            }
            map.FaceIcons = face => faceIcons.GetValueOrDefault(face);
            map.RefreshFaces();
            WM.CompositionTarget.Rendering += sample;
            Pump(8);
            Require(Field<WC.Image>(surface, "terrain").Source is not null, "Terrain did not load");
            Set(surface, "zoom", 4.0);
            Set(surface, "center", new W.Point(MapTerrain.WorldWidth / 2, MapTerrain.WorldHeight / 2));
            Call(surface, "ApplyView");
            Pump(2);
            W.Point origin = new(surface.ActualWidth / 2, surface.ActualHeight / 2);
            if (swipe)
            {
                phase = "swipe-warmup";
                Call(surface, "BeginDrag", origin);
                Pump(3, seconds => Call(surface, "MoveDrag", origin + new W.Vector(Math.Sin(seconds * 4) * 650, Math.Cos(seconds * 4) * 500)));
                Call(surface, "EndDrag");
                Set(surface, "center", new W.Point(MapTerrain.WorldWidth / 2, MapTerrain.WorldHeight / 2));
                Call(surface, "ApplyView");
                Call(surface, "Settle");
                Pump(1);
            }
            phase = "idle";
            int idleBefore = gpu.SubmittedFrames;
            Pump(5);
            int idleSubmissions = gpu.SubmittedFrames - idleBefore;
            void Move(W.Point pointer)
            {
                double at = clock.Elapsed.TotalMilliseconds;
                var timer = Stopwatch.StartNew();
                Call(surface, "MoveDrag", pointer);
                inputMs.Add(timer.Elapsed.TotalMilliseconds);
                inputCount++;
                if (swipe) inputs.Add(new { ms = at, phase, pointer, callMs = timer.Elapsed.TotalMilliseconds,
                    dragged = Field<bool>(surface, "dragged"), samples = Field<Queue<(double Time, W.Point Position)>>(surface, "dragSamples").Count });
            }
            if (swipe)
            {
                int trial = 0;
                foreach (W.Vector delta in new W.Vector[] { new(420, 0), new(-420, 0), new(0, 300), new(0, -300) })
                {
                    trial++;
                    phase = "reset-" + trial;
                    Set(surface, "center", new W.Point(MapTerrain.WorldWidth / 2, MapTerrain.WorldHeight / 2));
                    Call(surface, "ApplyView");
                    Pump(.4);
                    phase = "fast-drag-" + trial;
                    Call(surface, "BeginDrag", origin);
                    W.Point lastPointer = origin;
                    int recentSamples = 0;
                    double lastSampleAgeMs = -1;
                    bool draggedBeforeRelease = false;
                    // Replay at the actual composition cadence. DispatcherTimer can skip input ticks
                    // while Rendering keeps running, replaying a pause rather than a fast release.
                    var inputFrame = new WT.DispatcherFrame();
                    var watchdog = new WT.DispatcherTimer(WT.DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(5) };
                    watchdog.Tick += (_, _) => inputFrame.Continue = false;
                    double inputStarted = clock.Elapsed.TotalSeconds;
                    EventHandler pulse = (_, _) =>
                    {
                        double seconds = clock.Elapsed.TotalSeconds - inputStarted;
                        lastPointer = origin + delta * Math.Min(1, seconds / .16); Move(lastPointer);
                        if (seconds < .16) return;
                        double now = Field<Stopwatch>(surface, "clock").Elapsed.TotalSeconds;
                        var raw = Field<Queue<(double Time, W.Point Position)>>(surface, "dragSamples").ToArray();
                        recentSamples = raw.Count(s => now - s.Time <= .1);
                        lastSampleAgeMs = raw.Length == 0 ? -1 : (now - raw[^1].Time) * 1000;
                        draggedBeforeRelease = Field<bool>(surface, "dragged");
                        Call(surface, "ReleaseDrag", lastPointer);
                        inputFrame.Continue = false;
                    };
                    WM.CompositionTarget.Rendering += pulse;
                    watchdog.Start();
                    try { WT.Dispatcher.PushFrame(inputFrame); }
                    finally { WM.CompositionTarget.Rendering -= pulse; watchdog.Stop(); }
                    Require(lastPointer == origin + delta, "Input replay did not reach the requested release point");
                    bool started = Field<bool>(surface, "looping");
                    if (started) coastsStarted++;
                    W.Vector speed = Field<W.Vector>(surface, "velocity");
                    phase = "coast-" + trial;
                    Pump(2.5);
                    bool settled = !Field<bool>(surface, "looping");
                    allCoastsSettled &= settled;
                    releases.Add(new { trial, speed, recentSamples, lastSampleAgeMs, draggedBeforeRelease, started, settled,
                        remainingVelocity = Field<W.Vector>(surface, "velocity") });
                }
            }
            else if (zoomReplay)
            {
                Set(surface, "zoom", 1.0);
                Call(surface, "ApplyView");
                Pump(.5);
                phase = "zoom";
                if (capture) screenTimer.Start();
                W.Point anchor = new(surface.ActualWidth * .45, surface.ActualHeight * .55);
                int previousStep = -1;
                Pump(8, seconds =>
                {
                    int step = (int)(seconds / .12);
                    if (step == previousStep) return;
                    previousStep = step;
                    Call(surface, "ZoomAt", step % 26 < 13 ? 120 : -120, anchor);
                });
                phase = "zoom-idle";
                Pump(1);
                phase = "zoom-resume";
                Call(surface, "ZoomAt", 120 * 8, anchor);
                Pump(.7);
                phase = "zoom-drag";
                Call(surface, "BeginDrag", origin);
                Pump(3, seconds => Move(origin + new W.Vector(Math.Sin(seconds * 4) * 180, Math.Cos(seconds * 4) * 120)));
                Call(surface, "EndDrag");
                Call(surface, "Settle");
                Pump(.5);
                screenTimer.Stop();
            }
            else
            {
                phase = "drag";
                Call(surface, "BeginDrag", origin);
                Pump(5, seconds => Move(origin + new W.Vector(Math.Sin(seconds * 2) * 100, Math.Cos(seconds * 2) * 60)));
                Call(surface, "EndDrag");
                Call(surface, "Settle");
            }
            phase = "settle";
            Pump(2);
            var result = new { InputCount = inputCount, InputMeanMs = inputMs.Count == 0 ? 0 : inputMs.Average(), InputP95Ms = inputMs.Count == 0 ? 0 : inputMs.Order().ElementAt((int)(inputMs.Count * .95)),
                PresentSamples = gaps.Count, PresentGapMeanMs = gaps.Count == 0 ? 0 : gaps.Average(), PresentGapP95Ms = gaps.Count == 0 ? 0 : gaps.Order().ElementAt((int)(gaps.Count * .95)),
                Phases = phaseGaps.ToDictionary(p => p.Key, p => new { Samples = p.Value.Count, MeanMs = p.Value.Average(),
                    P95Ms = p.Value.Order().ElementAt((int)(p.Value.Count * .95)), MaxMs = p.Value.Max() }), Releases = releases,
                CoastsStarted = coastsStarted, AllCoastsSettled = allCoastsSettled,
                IdleSubmissions = idleSubmissions, FrontBufferChanges = availableChanges, LostSamples = lostFrames, ZeroSizeSamples = zeroSizeFrames,
                EmptyAfterFirstFrame = emptyAfterFirstFrame, InteropVersion = imageVersion.GetValue(gpu.Image),
                Submitted = gpu.SubmittedFrames, Presented = gpu.PresentedFrames, WpfCopies = wpfCopies,
                CopiesWhileWriting = copiesWhileWriting, Seconds = clock.Elapsed.TotalSeconds };
            double refreshRate = Convert.ToDouble(MediaField("_animationRenderRate"));
            var display = new { Copies = exerciseCopies, refreshRate,
                MeanMs = copyGaps.Count == 0 ? 0 : copyGaps.Average(),
                P95Ms = copyGaps.Count == 0 ? 0 : copyGaps.Order().ElementAt((int)(copyGaps.Count * .95)),
                MaxMs = copyGaps.Count == 0 ? 0 : copyGaps.Max(),
                CameraAgeMeanMs = cameraAges.Count == 0 ? 0 : cameraAges.Average(),
                CameraAgeP95Ms = cameraAges.Count == 0 ? 0 : cameraAges.Order().ElementAt((int)(cameraAges.Count * .95)),
                terrainCopies, seaOnlyCopies, screenSeaFrames, screenChanges };
            File.WriteAllText(Path.Combine(output, "screen-frames.json"), JsonSerializer.Serialize(screenFrames));
            File.WriteAllText(Path.Combine(output, "display.json"), JsonSerializer.Serialize(display, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(display));
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "composition.json"), JsonSerializer.Serialize(frames));
            File.WriteAllText(Path.Combine(output, "wpf-copies.json"), JsonSerializer.Serialize(copyTimes));
            File.WriteAllText(Path.Combine(output, "query-readiness.json"), JsonSerializer.Serialize(queryReadyAt.Select(p => new {
                frame = p.Key, readyMs = p.Value, submittedMs = submittedAt[p.Key], completionMs = p.Value - submittedAt[p.Key] })));
            if (swipe) File.WriteAllText(Path.Combine(output, "inputs.json"), JsonSerializer.Serialize(inputs));
            File.WriteAllText(Path.Combine(output, "slow-dispatches.json"), JsonSerializer.Serialize(slowDispatches));
            Console.WriteLine(JsonSerializer.Serialize(result));
            Require(lostFrames == 0 && emptyAfterFirstFrame == 0, "Map lost its device or displayed an empty surface after initialization");
            Require(ReferenceEquals(gpu, Field<GpuMap?>(surface, "gpu")), "Map restarted its GPU renderer during navigation");
            Require(allCoastsSettled, "Swipe animation failed to settle within 2.5 seconds; see recorded phases and velocities");
            Require(!swipe || coastsStarted == 4, "Swipe replay did not exercise all four release animations");
            Require(copiesWhileWriting == 0, "WPF requested a copy while the shared surface was being written");
            if (!capture)
            {
                Require(refreshRate > 0 && copyGaps.Count > 20, "Not enough compositor cadence samples");
                Require(display.P95Ms <= 2000 / refreshRate + 5, "WPF frame delivery exceeded two display intervals at P95");
                Require(display.CameraAgeP95Ms <= 3000 / refreshRate + 5, "Camera frames arrived more than three display intervals late at P95");
            }
            Require(!capture || terrainCopies > 0 && seaOnlyCopies == 0, "Zoom displayed a sea-only frame; see wpf-copies.json");
            Require(!capture || screenFrames.Count > 0 && screenSeaFrames == 0, "Native window displayed a sea-only frame; see screen-frames.json");
        }
        finally
        {
            screenTimer.Stop();
            surface.Dispatcher.Hooks.OperationStarted -= dispatchStarted;
            surface.Dispatcher.Hooks.OperationCompleted -= dispatchCompleted;
            WM.CompositionTarget.Rendering -= sample;
            Call(surface, "EndDrag");
            Call(surface, "StopLoop");
            var handler = typeof(MapSurface).GetMethod("OnGpuFrame", Hidden)!.CreateDelegate<EventHandler>(surface);
            WM.CompositionTarget.Rendering -= handler;
            var log = Field<MapRenderLog?>(surface, "renderLog");
            host.Close();
            log?.Dispose();
            log?.Completion.GetAwaiter().GetResult();
            gpu.Dispose();
        }
    }
}

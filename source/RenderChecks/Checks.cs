using System.Reflection;
using System.Runtime.InteropServices;
using San14Tenkazu;
using Vortice.Direct3D11;
using Vortice.DXGI;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckSpatialIndexes()
    {
        var random = new Random(714);
        var linear = new List<W.Rect>();
        var indexed = new MapAreas();
        for (int i = 0; i < 700; i++)
        {
            var area = new W.Rect(random.Next(-600, 1000), random.Next(-600, 1000), random.Next(1, 250), random.Next(1, 140));
            linear.Add(area); indexed.Add(area);
        }
        linear.Add(new W.Rect(-64, -64, 64, 64)); indexed.Add(linear[^1]);
        for (int i = 0; i < 1500; i++)
        {
            var area = new W.Rect(random.Next(-700, 1100), random.Next(-700, 1100), random.Next(0, 300), random.Next(0, 200));
            Require(linear.Where(r => r.IntersectsWith(area)).SequenceEqual(indexed.Near(area).Where(r => r.IntersectsWith(area))), "Rectangle lookup/order differs from linear scan");
            var disc = new TokenPlaces.Disc(area.TopLeft, 10 + random.NextDouble() * 20);
            var reference = TokenPlaces.Shield(disc, 20, 25, true, linear, linear.Take(20).ToArray());
            var actual = TokenPlaces.Shield(disc, 20, 25, true, indexed, new MapAreas(linear.Take(20)));
            Require(actual == reference, "Shield placement differs from linear scan");
        }
        indexed.Clear();
        Require(!indexed.Near(new W.Rect(-100, -100, 200, 200)).Any(), "Cleared index retained obstacles");
        var points = new MapPoints();
        points.Clear(24);
        var tokens = new List<W.Point>();
        for (int i = 0; i < 5000; i++)
        {
            var point = new W.Point(random.Next(-700, 1100) + random.NextDouble(), random.Next(-700, 1100) + random.NextDouble());
            int reference = tokens.FindIndex(p => (p - point).Length < 24);
            Require(points.FirstNear(point) == reference, "Token grouping differs from linear scan");
            if (reference < 0) { points.Add(point, tokens.Count); tokens.Add(point); }
        }
        Console.WriteLine("PASS spatial lookup, shield placement and token grouping: 6500 randomized comparisons");
    }

    private static void CheckGpuFrames()
    {
        using GpuMap gpu = GpuMap.TryCreate() ?? throw new InvalidOperationException("GPU renderer unavailable");
        gpu.Resize(128, 128, 96);
        for (int i = 0; i < 8; i++)
        {
            WM.Color colour = WM.Color.FromRgb((byte)(30 + i * 20), (byte)(20 + i), (byte)(240 - i * 20));
            Require(gpu.Render(canvas => canvas.Clear(colour)), "Frame submission failed");
            Require(!gpu.Render(canvas => canvas.Clear(WM.Colors.Black)), "Pending frame was overwritten");
            Finish(gpu);
            byte[] pixels = ReadPixels(gpu, 128, 128);
            Require(pixels[0] == colour.B && pixels[1] == colour.G && pixels[2] == colour.R && pixels[3] == 255, "Presented pixels differ from submitted frame");
            if (i == 7)
            {
                var visual = new WM.DrawingVisual();
                using (WM.DrawingContext dc = visual.RenderOpen()) dc.DrawImage(gpu.Image, new W.Rect(0, 0, 128, 128));
                var composed = new WMI.RenderTargetBitmap(128, 128, 96, 96, WM.PixelFormats.Pbgra32);
                composed.Render(visual);
                var shown = new byte[128 * 128 * 4];
                composed.CopyPixels(shown, 128 * 4, 0);
                Require(shown[0] == colour.B && shown[1] == colour.G && shown[2] == colour.R && shown[3] == 255, "WPF did not receive the completed shared surface");
            }
        }
        Require(gpu.SubmittedFrames == 8 && gpu.PresentedFrames == 8, "Submission/presentation counts differ");
        Require(gpu.BackBufferBindings == 1, "Normal frames recreated the WPF interop resource");

        byte[] imagePixels = new byte[64 * 64 * 4];
        Array.Fill(imagePixels, (byte)255);
        WMI.BitmapSource image = WMI.BitmapSource.Create(64, 64, 96, 96, WM.PixelFormats.Pbgra32, null, imagePixels, 64 * 4);
        image.Freeze();
        gpu.Render(canvas =>
        {
            canvas.Clear(WM.Colors.Black);
            canvas.DrawImage(image, new W.Rect(-200, -200, 64, 64));
            canvas.PushMatrix(new WM.Matrix(2, 0, 0, 2, 100, 100));
            canvas.DrawImage(image, new W.Rect(100, 100, 64, 64));
            canvas.Pop();
        });
        Require(gpu.Canvas.BitmapUploads == 0 && gpu.Canvas.Culled == 2, "Off-screen bitmaps were uploaded");
        Finish(gpu);

        var map = new MapSurface();
        MethodInfo ring = typeof(MapSurface).GetMethod("DrawTroopRing", Hidden)!;
        var colourOfForce = System.Drawing.Color.FromArgb(90, 150, 210);
        var centre = new W.Point(63.25, 65.5);
        const double radius = 23.7;
        const long troops = 7007;
        gpu.Render(canvas => { canvas.Clear(WM.Colors.Black); DrawReferenceRing(canvas, colourOfForce, troops, centre, radius); });
        Finish(gpu);
        byte[] referencePixels = ReadPixels(gpu, 128, 128);
        gpu.Render(canvas => { canvas.Clear(WM.Colors.Black); ring.Invoke(map, [canvas, colourOfForce, troops, centre, radius]); });
        Finish(gpu);
        byte[] actualPixels = ReadPixels(gpu, 128, 128);
        long error = referencePixels.Zip(actualPixels, (a, b) => Math.Abs(a - b)).Sum();
        Require(error / (double)actualPixels.Length < 0.25, "Cached ring changed its shape/stroke width");
        gpu.Render(canvas => { canvas.Clear(WM.Colors.Black); ring.Invoke(map, [canvas, colourOfForce, troops, new W.Point(50, 50), 17.3]); });
        Require(gpu.Canvas.GeometryBuilds == 0, "Moving/zooming rebuilt cached troop geometry");
        Finish(gpu);

        var text = new WM.FormattedText("17772", System.Globalization.CultureInfo.InvariantCulture, W.FlowDirection.LeftToRight,
            new WM.Typeface("Segoe UI"), 14, WM.Brushes.White, 1);
        WM.Geometry glyphs = text.BuildGeometry(new W.Point(0, 0));
        glyphs.Freeze();
        var halo = new WM.Pen(WM.Brushes.Black, 3);
        gpu.Render(canvas =>
        {
            canvas.Clear(WM.Colors.Transparent);
            canvas.DrawGeometryAt(null, halo, glyphs, new W.Point(32, 48));
            canvas.DrawGeometryAt(WM.Brushes.White, null, glyphs, new W.Point(32, 48));
        });
        Finish(gpu);
        byte[] directLabel = ReadPixels(gpu, 128, 128);
        gpu.Render(canvas => { canvas.Clear(WM.Colors.Transparent); canvas.DrawOutlined(glyphs, glyphs.Bounds, WM.Brushes.White, halo, 32, 48, 1, 1); });
        Finish(gpu);
        byte[] cachedLabel = ReadPixels(gpu, 128, 128);
        long labelError = directLabel.Zip(cachedLabel, (a, b) => Math.Abs(a - b)).Sum();
        if (labelError != 0)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine("source", "RenderChecks", "artifacts"));
            foreach (var capture in new[] { ("label-reference.png", directLabel), ("label-cache.png", cachedLabel) })
            {
                var encoder = new WMI.PngBitmapEncoder();
                encoder.Frames.Add(WMI.BitmapFrame.Create(WMI.BitmapSource.Create(128, 128, 96, 96, WM.PixelFormats.Pbgra32, null, capture.Item2, 128 * 4)));
                string path = System.IO.Path.Combine("source", "RenderChecks", "artifacts", capture.Item1);
                using var file = System.IO.File.Create(path);
                encoder.Save(file);
            }
            var mip = (Vortice.Direct2D1.ID2D1DeviceContext)typeof(D2DCanvas).GetField("mipContext", Hidden)!.GetValue(gpu.Canvas)!;
            Console.WriteLine($"Label rasterizer AA: {mip.AntialiasMode}; difference {labelError}");
        }
        Require(directLabel.SequenceEqual(cachedLabel), $"GPU label cache changed glyph/halo pixels (mean byte error {labelError / (double)cachedLabel.Length:F6})");
        gpu.Render(canvas => { canvas.Clear(WM.Colors.Gray); canvas.DrawOutlined(glyphs, glyphs.Bounds, WM.Brushes.White, halo, 40, 50, 1.1, 1); });
        Require(gpu.Canvas.BitmapUploads == 0 && gpu.Canvas.GeometryBuilds == 0, "Moving/scaling a label regenerated its GPU image");
        Finish(gpu);
        WM.Geometry hiddenLabel = new WM.RectangleGeometry(new W.Rect(0, 0, 20, 20));
        gpu.Render(canvas => canvas.DrawOutlined(hiddenLabel, hiddenLabel.Bounds, WM.Brushes.White, halo, -200, -200, 1, 1));
        Require(gpu.Canvas.BitmapUploads == 0 && gpu.Canvas.GeometryBuilds == 0, "Off-screen label created GPU resources");
        Finish(gpu);

        // Finish the protected frame and retain the old surface until a complete resized frame is available.
        gpu.Render(canvas => canvas.Clear(WM.Colors.Red));
        gpu.Resize(96, 72, 144);
        Require(gpu.Pending && gpu.Image.PixelWidth == 128, "Resize lost the protected shared surface");
        gpu.Bind();
        Finish(gpu);
        Require(gpu.Image.PixelWidth == 128, "Resize published an unfinished surface");
        gpu.Render(canvas => canvas.Clear(WM.Colors.Green));
        Finish(gpu);
        byte[] resized = ReadPixels(gpu, 96, 72);
        Require(resized[1] == 128 && resized[2] == 0, "Resize presented an obsolete surface");
        Console.WriteLine($"PASS GPU delivery, pending-frame protection, culling before upload, cached ring/label pixels and resize/DPI (ring mean byte error {error / (double)actualPixels.Length:F4})");
        CheckTreeBatches(gpu);
    }

    private static void DrawReferenceRing(D2DCanvas canvas, System.Drawing.Color colour, long troops, W.Point centre, double radius)
    {
        static WM.Brush Brush(System.Drawing.Color colour) => new WM.SolidColorBrush(WM.Color.FromRgb(colour.R, colour.G, colour.B));
        var dim = System.Drawing.Color.FromArgb((colour.R + 2 * 26) / 3, (colour.G + 2 * 21) / 3, (colour.B + 2 * 15) / 3);
        canvas.DrawEllipse(null, new WM.Pen(Brush(dim), 2), centre, radius, radius);
        double turn = troops / 10000.0 * Math.PI * 2;
        int steps = Math.Max(1, (int)Math.Ceiling(turn / (Math.PI / 24)));
        var shape = new WM.StreamGeometry();
        using (WM.StreamGeometryContext g = shape.Open())
        {
            g.BeginFigure(new W.Point(centre.X, centre.Y - radius), false, false);
            for (int i = 1; i <= steps; i++) g.LineTo(new W.Point(centre.X + radius * Math.Sin(turn * i / steps), centre.Y - radius * Math.Cos(turn * i / steps)), true, true);
        }
        shape.Freeze();
        canvas.DrawGeometry(null, new WM.Pen(Brush(colour), 2), shape);
    }

    private static byte[] ReadPixels(GpuMap gpu, int width, int height)
    {
        var device = (ID3D11Device)typeof(GpuMap).GetField("d3d", Hidden)!.GetValue(gpu)!;
        var context = (ID3D11DeviceContext)typeof(GpuMap).GetField("immediate", Hidden)!.GetValue(gpu)!;
        object front = typeof(GpuMap).GetField("front", Hidden)!.GetValue(gpu)!;
        var texture = (ID3D11Texture2D)front.GetType().GetField("Texture", Hidden)!.GetValue(front)!;
        using ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
        });
        context.CopyResource(staging, texture);
        MappedSubresource data = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var result = new byte[width * height * 4];
            for (int y = 0; y < height; y++) Marshal.Copy(IntPtr.Add(data.DataPointer, y * (int)data.RowPitch), result, y * width * 4, width * 4);
            return result;
        }
        finally { context.Unmap(staging, 0); }
    }

    private static void CheckTreeBatches(GpuMap gpu)
    {
        gpu.Resize(128, 128, 96);
        MapShaders shaders = gpu.Shaders ?? throw new InvalidOperationException("Map shaders unavailable");
        MapTrees.Sprite[]? previous = MapTrees.Sprites;
        var sprite = new MapTrees.Sprite([new MapTrees.Level(Enumerable.Repeat(0xFF70C090u, 16).ToArray(), 4, 4)], -0.12f, -0.24f, 0.24f, 0.24f);
        MapTrees.Set(sprite, sprite);
        try
        {
            var device = (ID3D11Device)typeof(GpuMap).GetField("d3d", Hidden)!.GetValue(gpu)!;
            object woods = typeof(MapShaders).GetMethod("MakeWoods", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [device, MapTrees.Sprites])!;
            typeof(MapShaders).GetField("woods", Hidden)!.SetValue(shaders, woods);
            typeof(MapShaders).GetField("woodsAsked", Hidden)!.SetValue(shaders, MapTrees.Sprites);
            var batchesField = woods.GetType().GetField("Batches", Hidden)!;
            var batches = ((W.Rect Bounds, int Start, int Count)[])batchesField.GetValue(woods)!;
            int count = (int)woods.GetType().GetField("Count", Hidden)!.GetValue(woods)!;
            MapTrees.Tree tree = MapTrees.Trees(0, MapTerrain.Columns - 1, 0, MapTerrain.Rows - 1)[count / 2];
            var camera = new MapShaders.Camera(64, 64 - tree.X * 64, 64 - tree.Y * 64);
            MethodInfo begin = typeof(MapShaders).GetMethod("Begin", Hidden)!;
            MethodInfo draw = typeof(MapShaders).GetMethod("DrawTrees", Hidden)!;
            void RenderTrees()
            {
                gpu.Render(canvas =>
                {
                    canvas.Clear(WM.Colors.Black);
                    gpu.Direct3D((dc, target) =>
                    {
                        begin.Invoke(shaders, [dc, target, camera, new System.Numerics.Vector4(0, 0, MapTerrain.WorldWidth, MapTerrain.WorldHeight), 1f]);
                        draw.Invoke(shaders, [dc, target, camera, 1f]);
                    });
                });
                Finish(gpu);
            }
            batchesField.SetValue(woods, new[] { (new W.Rect(0, 0, MapTerrain.WorldWidth, MapTerrain.WorldHeight), 0, count) });
            RenderTrees();
            byte[] reference = ReadPixels(gpu, 128, 128);
            Require(reference.Where((_, i) => i % 4 != 3).Any(b => b != 0), "Tree comparison scene is empty");
            batchesField.SetValue(woods, batches);
            RenderTrees();
            Require(reference.SequenceEqual(ReadPixels(gpu, 128, 128)), "Tree culling changed pixels or blend order");
            Console.WriteLine($"PASS tree batch culling: identical GPU pixels to all {count} instances");
        }
        finally { MapTrees.Set(previous?[0], previous is { Length: > 1 } ? previous[1] : null); }
    }

    private static void CheckFrameReplay(GameSnapshot data)
    {
        string path = System.IO.Path.Combine(Environment.CurrentDirectory, "source", "RenderChecks", "artifacts", "replay.csv");
        string? previous = Environment.GetEnvironmentVariable("SAN14_RENDER_LOG");
        Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", path);
        MapSurface map;
        try { map = new MapSurface { Width = 1280, Height = 800 }; }
        finally { Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", previous); }
        var log = (MapRenderLog?)typeof(MapSurface).GetField("renderLog", Hidden)!.GetValue(map);
        Require(log is not null, "Render log could not be opened");
        try
        {
            map.Measure(new W.Size(1280, 800));
            map.Arrange(new W.Rect(0, 0, 1280, 800));
            map.SetSnapshot(data);
            map.SetTerritory(data.TileOwners);
            map.SetCounties(data.TileCounties);
            FieldInfo zoom = typeof(MapSurface).GetField("zoom", Hidden)!;
            FieldInfo dirty = typeof(MapSurface).GetField("gpuDirty", Hidden)!;
            FieldInfo looping = typeof(MapSurface).GetField("looping", Hidden)!;
            MethodInfo apply = typeof(MapSurface).GetMethod("ApplyView", Hidden)!;
            MethodInfo render = typeof(MapSurface).GetMethod("RenderGpu", Hidden)!;
            GpuMap gpu = (GpuMap)typeof(MapSurface).GetField("gpu", Hidden)!.GetValue(map)!;
            looping.SetValue(map, true);
            for (int i = 0; i < 30; i++)
            {
                zoom.SetValue(map, 1 + i * 3.0 / 29);
                apply.Invoke(map, null);
                if (i == 0)
                {
                    render.Invoke(map, null);
                    Require(gpu.Pending, "Replay did not retain an unfinished frame");
                    zoom.SetValue(map, 1.025);
                    apply.Invoke(map, null);
                }
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (gpu.Pending || (bool)dirty.GetValue(map)!)
                {
                    render.Invoke(map, null);
                    Require(!gpu.Lost, "Map replay lost its GPU device");
                    if (timer.Elapsed.TotalSeconds > 5) throw new TimeoutException("Map did not present its latest view");
                    Thread.Yield();
                }
            }
            looping.SetValue(map, false);
            int submitted = gpu.SubmittedFrames;
            render.Invoke(map, null);
            Require(submitted == gpu.SubmittedFrames && gpu.SubmittedFrames == gpu.PresentedFrames, "Idle map redrew or lost a frame");
            log!.Dispose();
            Require(log.Completion.Wait(TimeSpan.FromSeconds(5)), "Frame log did not flush");
            string[] rows = System.IO.File.ReadAllLines(path);
            Require(rows.Count(row => row.StartsWith("submit,")) == submitted && rows.Count(row => row.StartsWith("present,")) == submitted, "Frame log missed a submission/presentation");
            string[] presented = rows.Where(row => row.StartsWith("present,")).ToArray();
            Require(presented[0].Split(',')[2] == "1.00000" && presented[1].Split(',')[2] == "1.02500", "Pending input lost a view or log recorded the wrong submitted zoom");
            Require(rows.Last().Split(',')[2] == "4.00000", "Final zoom was not presented");
            gpu.Dispose();
            typeof(MapSurface).GetField("gpu", Hidden)!.SetValue(map, null);
            Console.WriteLine($"PASS map frame replay and CSV diagnostics: {submitted} complete frames, latest zoom delivered, no idle redraw");
        }
        finally { log!.Dispose(); }
    }
}

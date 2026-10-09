using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void CheckPerformance(string output)
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "frames.jsonl"), "");
        File.WriteAllText(Path.Combine(output, "pan-pixels.jsonl"), "");
        var map = new MapSurface { Width = 1280, Height = 800, ProfileUnits = true };
        map.Measure(new W.Size(1280, 800)); map.Arrange(new W.Rect(0, 0, 1280, 800));
        var game = new TestGame().Force(1, 1, 1);
        for (int i = 1; i <= 180; i++) game.Officer(i, "장수" + i, 1, 1, 3).Leading(i);
        GameSnapshot Snapshot()
        {
            GameSnapshot made = game.Build();
            return new GameSnapshot { Cities = made.Cities, Counties = made.Counties, Officers = made.Officers,
                CastleByCity = made.CastleByCity, TileOwners = new byte[GameMemory.TileCount], TileCounties = new ushort[GameMemory.TileCount],
                Forces = made.Forces, ForceRecords = made.ForceRecords, CorpsForce = made.CorpsForce,
                OfficerById = made.OfficerById, Units = made.Units };
        }
        GameSnapshot data = Snapshot();
        for (int i = 0; i < data.Units.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.Units[i].Data.AsSpan(UnitFields.TileOffset),
                (ushort)(220 * (104 + i / 30 * 2) + 80 + (i % 30) * 3));
            UnitFields.Troops.Encode(3000 + i * 73).CopyTo(data.Units[i].Data, UnitFields.Troops.Offset);
        }
        map.SetSnapshot(data);
        var fields = typeof(MapSurface);
        void Set(string name, object value) => fields.GetField(name, Hidden)!.SetValue(map, value);
        object? Call(string name, params object?[] args) => fields.GetMethod(name, Hidden)!.Invoke(map, args);
        using GpuMap gpu = (GpuMap?)fields.GetField("gpu", Hidden)!.GetValue(map) ?? throw new InvalidOperationException("GPU unavailable");
        gpu.Resize(1280, 800, 96);
        var frame = new MapDrawList();
        var results = new List<object>();
        int uiThread = Environment.CurrentManagedThreadId;
        int workerSubmissions = 0, readyNotifications = 0, readyOnUi = 0;
        gpu.FrameReady += () => {
            if (Environment.CurrentManagedThreadId == uiThread) Interlocked.Increment(ref readyOnUi);
            Interlocked.Increment(ref readyNotifications);
        };
        byte[] Draw(string phase, bool forced = false)
        {
            if (forced) Set("layoutSteady", false);
            frame.Reset();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            Call("DrawGpuFrame", frame);
            double prepared = timer.Elapsed.TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(gpu.RenderAsync(frame), "Frame queue rejected a completed previous frame");
            workerSubmissions++;
            Finish(gpu);
            Require(gpu.RenderThreadId != 0 && gpu.RenderThreadId != uiThread, "GPU commands ran on the UI thread");
            results.Add(new { phase, prepared, allocated, workerMs = gpu.LastFrame.Draw,
                workerAllocated = gpu.LastAllocatedBytes, gpuWaitMs = gpu.LastGpuWait, gcPauseMs = gpu.LastGcPauseMs, map.UnitProfile,
                commands = frame.Count, gpu.Canvas.SpriteBuilds, gpu.Canvas.SpriteDraws });
            File.AppendAllText(Path.Combine(output, "frames.jsonl"), JsonSerializer.Serialize(results[^1]) + "\n");
            return ReadPixels(gpu, 1280, 800);
        }
        Set("zoom", 4.0); Call("ApplyView");
        for (int i = 0; i < 35; i++) Draw("warm");
        var center = (W.Point)fields.GetField("center", Hidden)!.GetValue(map)!;
        int panReused = 0, panMembershipChanges = 0;
        for (int i = 1; i <= 20; i++)
        {
            Set("center", new W.Point(center.X + i * .1, center.Y)); Call("ApplyView");
            byte[] cached = Draw("pan");
            if (map.UnitProfile.LayoutReused) panReused++;
            else
            {
                Require((int)fields.GetField("unitLayoutInvalidation", Hidden)!.GetValue(map)! == 64,
                    "Pan invalidated unchanged scale/order/motion inputs");
                panMembershipChanges++;
            }
            for (int wait = 0; wait < 20 && (gpu.Canvas.Deferred || (bool)fields.GetField("labelsPending", Hidden)!.GetValue(map)!); wait++)
                cached = Draw("pan-resources");
            var cachedCommands = new MapDrawList(); frame.CopyTo(cachedCommands);
            byte[] reference = Draw("pan-reference", true);
            string commandDifference = cachedCommands.CompareCommands(frame);
            Require(commandDifference == "identical", "Cached pan changed command inputs: " + commandDifference);
            int maximum = 0, changed = 0;
            for (int pixel = 0; pixel < cached.Length; pixel += 4)
            {
                int delta = 0;
                for (int channel = 0; channel < 4; channel++) delta = Math.Max(delta, Math.Abs(cached[pixel + channel] - reference[pixel + channel]));
                maximum = Math.Max(maximum, delta); if (delta != 0) changed++;
            }
            File.AppendAllText(Path.Combine(output, "pan-pixels.jsonl"), JsonSerializer.Serialize(new { i, maximum, changed, commandDifference }) + "\n");
            Require(maximum <= 1 && changed <= 10, "Identical command inputs produced a significant pixel difference");
        }
        Require(panReused >= 15 && panMembershipChanges > 0, "Pan did not cover both cache reuse and visibility invalidation");
        // A new read replaces every record even when its positions and priority order are unchanged.
        UnitRecord[] freshUnits = data.Units.Select(unit => unit with { Data = (byte[])unit.Data.Clone() }).ToArray();
        foreach (UnitRecord unit in freshUnits)
            UnitFields.Troops.Encode(unit[UnitFields.Troops] + 1).CopyTo(unit.Data, UnitFields.Troops.Offset);
        GameSnapshot WithUnits(UnitRecord[] units) => new() { Cities = data.Cities, Counties = data.Counties, Officers = data.Officers,
            CastleByCity = data.CastleByCity, TileOwners = data.TileOwners, TileCounties = data.TileCounties,
            Forces = data.Forces, ForceRecords = data.ForceRecords, CorpsForce = data.CorpsForce,
            OfficerById = data.OfficerById, Units = units };
        GameSnapshot fresh = WithUnits(freshUnits);
        map.SetSnapshot(fresh); Draw("snapshot-reuse");
        Require(map.UnitProfile.LayoutReused, "Identical snapshot positions/order unnecessarily rebuilt membership");
        for (int wait = 0; wait < 20 && (gpu.Canvas.Deferred || (bool)fields.GetField("labelsPending", Hidden)!.GetValue(map)!); wait++)
            Draw("snapshot-resources");
        var tokens = (List<(UnitRecord Unit, W.Point Point, double Radius, List<UnitRecord> Others)>)fields.GetField("unitTokens", Hidden)!.GetValue(map)!;
        foreach (var token in tokens)
        {
            Require(ReferenceEquals(token.Unit, freshUnits.Single(u => u.Slot == token.Unit.Slot)), "Cached leader retained the previous snapshot");
            foreach (UnitRecord other in token.Others)
                Require(ReferenceEquals(other, freshUnits.Single(u => u.Slot == other.Slot)), "Cached stack retained the previous snapshot");
        }
        var freshCommands = new MapDrawList(); frame.CopyTo(freshCommands);
        Draw("snapshot-reference", true);
        string snapshotDifference = freshCommands.CompareCommands(frame);
        Require(snapshotDifference == "identical", "Snapshot reuse changed drawing inputs: " + snapshotDifference);
        foreach (var change in new (string Name, Action Apply)[] {
            ("zoom", () => { Set("zoom", 4.1); Call("ApplyView"); }),
            ("selected", () => map.SelectedUnit = (data.Units[0].Slot, data.Units[0].Commander)),
            ("positions", () => {
                UnitRecord[] moved = (UnitRecord[])freshUnits.Clone();
                moved[0] = moved[0] with { Data = (byte[])moved[0].Data.Clone() };
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(moved[0].Data.AsSpan(UnitFields.TileOffset), (ushort)(moved[0].Tile + 1));
                map.SetSnapshot(WithUnits(moved));
            }),
            ("size", () => { map.Width = 1260; map.Measure(new W.Size(1260, 800)); map.Arrange(new W.Rect(0, 0, 1260, 800)); })
        })
        {
            change.Apply(); Draw("invalidate-" + change.Name);
            Require(!map.UnitProfile.LayoutReused, "Changed layout inputs retained stale token membership: " + change.Name);
        }
        map.SetSnapshot(null); Draw("snapshot-clear");
        map.SetSnapshot(fresh); Draw("snapshot-reconnect");
        Require(!map.UnitProfile.LayoutReused && map.UnitProfile.TokensShown > 0, "Cleared snapshot retained stale empty membership");
        var spriteResults = new List<object>();
        // Native command lists retain the exact vector result at fractional positions and both DPIs.
        foreach (float dpi in new[] { 96f, 144f })
        foreach (double radius in new[] { 10.3, 20.0, 27.0 })
        foreach (bool portrait in new[] { false, true })
        {
            gpu.Resize(160, 160, dpi);
            WM.Brush fill = WM.Brushes.SlateBlue;
            if (portrait)
            {
                var portraitPixels = new byte[128 * 128 * 4];
                for (int pixel = 0; pixel < portraitPixels.Length; pixel += 4)
                {
                    portraitPixels[pixel] = (byte)(80 + pixel / 4 % 128);
                    portraitPixels[pixel + 1] = (byte)(70 + pixel / 4 / 128);
                    portraitPixels[pixel + 2] = 200;
                    portraitPixels[pixel + 3] = 255;
                }
                var image = WMI.BitmapSource.Create(128, 128, 96, 96, WM.PixelFormats.Bgra32, null, portraitPixels, 128 * 4);
                image.Freeze(); var brush = new WM.ImageBrush(image); brush.Freeze(); fill = brush;
            }
            W.Point p = new(52.3, 55.7);
            var sprite = (MapSprite)Call("UnitPortraitSprite", fill, portrait, radius, dpi / 96.0)!;
            string surname = portrait ? "" : "장";
            if (portrait)
            {
                // Exhaust the upload budget deterministically, instead of depending on GPU timing.
                Require(gpu.Render(canvas => {
                    canvas.Clear(WM.Colors.Black);
                    typeof(D2DCanvas).GetField("uploadSpent", Hidden)!.SetValue(gpu.Canvas, 6.0);
                    canvas.DrawSprite(sprite, p);
                }), "Deferred portrait frame was not submitted");
                Finish(gpu);
                Require(gpu.Canvas.Deferred, "Cold portrait did not defer its upload after the budget was exhausted");
            }
            // The first sprite must build its portrait's mip levels without a direct draw warming them.
            bool coldSubmitted = gpu.Render(canvas => { canvas.Clear(WM.Colors.Black); canvas.DrawSprite(sprite, p); });
            Console.WriteLine(JsonSerializer.Serialize(new { scenario = "cold-portrait-sprite", dpi, radius, portrait, coldSubmitted, gpu.Lost, gpu.RenderError }));
            Require(coldSubmitted, "Cold sprite frame was not submitted");
            Finish(gpu);
            int resourceFrames = 0;
            while (gpu.Canvas.Deferred && resourceFrames++ < 16)
            {
                Require(gpu.Render(canvas => { canvas.Clear(WM.Colors.Black); canvas.DrawSprite(sprite, p); }), "Portrait retry frame was not submitted");
                Finish(gpu);
            }
            Require(!gpu.Canvas.Deferred, "Deferred portrait did not finish uploading");
            byte[] coldPixels = ReadPixels(gpu, 160, 160);
            gpu.Render(canvas => { canvas.Clear(WM.Colors.Black); Call("DrawPortraitBase", canvas, fill, portrait, surname, p, radius, dpi / 96.0); });
            Finish(gpu); byte[] expected = ReadPixels(gpu, 160, 160);
            gpu.Render(canvas => {
                canvas.Clear(WM.Colors.Black); canvas.DrawSprite(sprite, p);
                if (surname.Length > 0)
                {
                    var text = (WM.FormattedText)Call("Mark", surname, radius * 1.05, WM.Brushes.White, dpi / 96.0, null)!;
                    canvas.DrawText(text, new W.Point(p.X - text.Width / 2, p.Y - text.Height / 2));
                }
            });
            Finish(gpu); byte[] actual = ReadPixels(gpu, 160, 160);
            if (portrait)
                Require(coldPixels.SequenceEqual(actual), "Cold portrait was omitted or changed after warming the sprite cache");
            double error = expected.Zip(actual, (a, b) => Math.Abs(a - b)).Average();
            int maximum = 0, changed = 0;
            for (int pixel = 0; pixel < actual.Length; pixel += 4)
            {
                int delta = 0;
                for (int channel = 0; channel < 4; channel++) delta = Math.Max(delta, Math.Abs(actual[pixel + channel] - expected[pixel + channel]));
                maximum = Math.Max(maximum, delta); if (delta > 0) changed++;
            }
            spriteResults.Add(new { dpi, radius, portrait, error, maximum, changed });
            if (maximum > 1 || changed > 10)
            {
                void Save(byte[] pixels, string name)
                {
                    var image = WMI.BitmapSource.Create(160, 160, 96, 96, WM.PixelFormats.Bgra32, null, pixels, 640);
                    var png = new WMI.PngBitmapEncoder(); png.Frames.Add(WMI.BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(output, name)); png.Save(file);
                }
                Save(expected, "sprite-reference.png"); Save(actual, "sprite-cached.png");
                File.WriteAllText(Path.Combine(output, "sprite-differences.json"), JsonSerializer.Serialize(new { dpi, radius, portrait, error,
                    pixels = Enumerable.Range(0, actual.Length / 4).Where(pixel => !actual.AsSpan(pixel * 4, 4).SequenceEqual(expected.AsSpan(pixel * 4, 4))).Select(pixel => new { x = pixel % 160, y = pixel / 160, before = Convert.ToHexString(expected.AsSpan(pixel * 4, 4)), after = Convert.ToHexString(actual.AsSpan(pixel * 4, 4)) }) }));
            }
            Require(maximum <= 1 && changed <= 10, $"Sprite vectors changed pixels: dpi={dpi}, radius={radius}, portrait={portrait}, maximum={maximum}, changed={changed}, error={error}");
        }
        File.WriteAllText(Path.Combine(output, "sprites.json"), JsonSerializer.Serialize(spriteResults, new JsonSerializerOptions { WriteIndented = true }));
        frame.Reset(); frame.Clear(WM.Colors.DarkOrange);
        gpu.Resize(160, 160, 96);
        Require(gpu.RenderAsync(frame), "Worker resize frame was not queued");
        workerSubmissions++;
        Require(!gpu.RenderAsync(frame), "Worker accepted a second frame before completion");
        gpu.Resize(170, 180, 144); gpu.Resize(190, 200, 192);
        Finish(gpu);
        Require(gpu.Image.PixelWidth == 160 && gpu.Image.PixelHeight == 160, "Resize discarded the last complete surface");
        Require(gpu.RenderAsync(frame), "Worker did not resume after deferred resize"); Finish(gpu);
        workerSubmissions++;
        Require(gpu.Image.PixelWidth == 190 && gpu.Image.PixelHeight == 200, "Worker did not apply the latest size/DPI request");
        Require(ReadPixels(gpu, 190, 200).AsSpan(0, 4).SequenceEqual(new byte[] { 0, 140, 255, 255 }), "Worker resize delivered incomplete pixels");
        Require(SpinWait.SpinUntil(() => Volatile.Read(ref readyNotifications) == workerSubmissions, TimeSpan.FromSeconds(1)), "Worker lost a frame completion notification");
        Require(readyOnUi == 0, "GPU completion wait ran on the UI thread");
        File.WriteAllText(Path.Combine(output, "frames.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { frames = results.Count,
            uiThread, gpu.RenderThreadId, gpu.Lost, panPixelComparisons = 20, panReused, panMembershipChanges, snapshotReuse = true, workerResize = true, spritePixelComparisons = 12, deferredPortraitComparisons = 6 }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS render worker, pan layout pixels/invalidation and 12 sprite/DPI comparisons");
        ((GpuMap?)fields.GetField("gpu", Hidden)!.GetValue(map))?.Dispose();
    }
}

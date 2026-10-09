using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace San14Tenkazu;

// Optional frame diagnostics. File I/O runs on a worker, outside the input/render thread.
internal sealed class MapRenderLog : IDisposable
{
    private readonly Channel<string> lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    internal Task Completion { get; }

    private MapRenderLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var writer = new StreamWriter(path, append: false) { AutoFlush = true };
        writer.WriteLine("event,utc,zoom,moving,width,height,submitted,presented,world_ms,cities_ms,units_ms,draw_ms,end_ms,submit_ms,present_delay_ms,uploads,geometry_builds,culled,interop_bindings,skipped_locks,front_available,copy_wait_ms,unit_layout_ms,unit_tokens_ms,unit_shields_ms,unit_allocated_bytes,unit_count,unit_layout_reused,prepare_ms,commands,render_thread,render_allocated_bytes,gc_process_pause_ms,sprite_builds,sprite_draws,render_error,camera_x,camera_y,frame_age_ms");
        Completion = Task.Run(async () =>
        {
            using (writer)
            {
                try { await foreach (string line in lines.Reader.ReadAllAsync()) await writer.WriteLineAsync(line); }
                catch (IOException ex) { lines.Writer.TryComplete(ex); Debug.WriteLine("Render log stopped: " + ex.Message); }
            }
        });
    }

    internal static MapRenderLog? Open()
    {
        string? path = Environment.GetEnvironmentVariable("SAN14_RENDER_LOG");
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path == "1") path = Path.Combine(Program.DataFolder, "logs", $"render-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.csv");
        try { return new MapRenderLog(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Debug.WriteLine("Render log unavailable: " + ex.Message); return null; }
    }

    internal void Record(string kind, double zoom, bool moving, int width, int height, GpuMap gpu, (double World, double Cities, double Units) phases, MapUnitProfile units = default, double preparedMs = 0, int commands = 0, System.Windows.Point camera = default, double frameAgeMs = 0)
    {
        // Only a completed recording publishes coherent worker metrics. Present rows describe this frame.
        bool complete = !gpu.Recording;
        var frame = complete ? gpu.LastFrame : default;
        int uploads = complete ? gpu.Canvas.BitmapUploads : 0, geometries = complete ? gpu.Canvas.GeometryBuilds : 0,
            culled = complete ? gpu.Canvas.Culled : 0, spriteBuilds = complete ? gpu.Canvas.SpriteBuilds : 0,
            spriteDraws = complete ? gpu.Canvas.SpriteDraws : 0;
        string error = (gpu.RenderError ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"");
        lines.Writer.TryWrite(string.Create(CultureInfo.InvariantCulture,
            $"{kind},{DateTime.UtcNow:O},{zoom:F5},{moving},{width},{height},{gpu.SubmittedFrames},{gpu.PresentedFrames},{phases.World:F4},{phases.Cities:F4},{phases.Units:F4},{frame.Draw:F4},{frame.End:F4},{frame.Submit:F4},{gpu.LastPresentDelay:F4},{uploads},{geometries},{culled},{gpu.BackBufferBindings},{gpu.SkippedLocks},{gpu.Image.IsFrontBufferAvailable},{gpu.LastCopyWait:F4},{units.Layout:F4},{units.Tokens:F4},{units.Shields:F4},{units.Allocated},{units.TokensShown},{units.LayoutReused},{preparedMs:F4},{commands},{gpu.RenderThreadId},{(complete ? gpu.LastAllocatedBytes : 0)},{(complete ? gpu.LastGcPauseMs : 0):F4},{spriteBuilds},{spriteDraws},\"{error}\",{camera.X:F5},{camera.Y:F5},{frameAgeMs:F4}"));
    }
    public void Dispose() => lines.Writer.TryComplete();
}

using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using D9 = Vortice.Direct3D9;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using FillMode = Vortice.Direct2D1.FillMode;
using InterpolationMode = Vortice.Direct2D1.InterpolationMode;
using DW = Vortice.DirectWrite;
using W = System.Windows;
using WI = System.Windows.Interop;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace San14Tenkazu;

// The map drawn on the GPU, as a game draws its screen: Direct2D renders the whole map (ground, territory,
// models, labels, units) into a Direct3D 11 texture each frame, and WPF shows that texture through a
// D3DImage (a Direct3D 9Ex surface sharing it). WPF then composes one picture instead of thousands of
// shapes, and Direct2D batches the drawing on the GPU. The cards and chips over the map stay WPF.
internal sealed class GpuMap : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    private readonly ID3D11Device d3d;
    private readonly ID3D11DeviceContext immediate;
    private ID3D11Query finished;
    private readonly ID2D1Factory1 factory;
    private readonly ID2D1Device device;
    private readonly ID2D1DeviceContext context;
    private readonly D9.IDirect3D9Ex d3d9;
    private readonly D9.IDirect3DDevice9Ex device9;
    private FrameTarget? front, back;
    private bool frontReady;
    private (int Width, int Height, float Dpi)? pendingResize;
    private IntPtr boundSurface;
    internal int BackBufferBindings { get; private set; }
    internal int SkippedLocks { get; private set; }
    private int width, height;
    private float dpi = 96;

    internal readonly WI.D3DImage Image = new();
    internal readonly D2DCanvas Canvas;

    // The ground and the borders drawn by shaders (null where they cannot be made: the tiles draw them).
    internal readonly MapShaders? Shaders;
    private bool drawing;                           // between Direct2D's BeginDraw and EndDraw

    // CPU milliseconds: recording, EndDraw and submission. Async GPU completion is checked on the worker.
    internal (double Draw, double End, double Submit) LastFrame { get; private set; }
    private volatile bool pending, recordingReady = true;
    internal bool Pending => pending;
    internal bool Recording => pending && !recordingReady;
    private Thread? renderThread;
    private readonly AutoResetEvent renderWake = new(false);
    private MapDrawList? queuedFrame;
    private volatile bool stopping;
    private bool disposed;
    internal long LastAllocatedBytes { get; private set; }
    internal double LastGcPauseMs { get; private set; }
    internal int RenderThreadId { get; private set; }
    internal string? RenderError { get; private set; }
    internal event Action? FrameReady;
    internal double LastGpuWait { get; private set; }
    internal int SubmittedFrames { get; private set; }
    internal int PresentedFrames { get; private set; }
    internal double LastPresentDelay { get; private set; }
    internal double LastCopyWait { get; private set; }
    private long submittedAt;

    private sealed class FrameTarget : IDisposable
    {
        internal int Width, Height;
        internal float Dpi;
        internal ID3D11Texture2D? Texture;
        internal ID3D11RenderTargetView? View;
        internal ID2D1Bitmap1? Bitmap;
        internal D9.IDirect3DTexture9? Texture9;
        internal D9.IDirect3DSurface9? Surface9;
        public void Dispose()
        {
            Bitmap?.Dispose(); View?.Dispose(); Surface9?.Dispose(); Texture9?.Dispose(); Texture?.Dispose();
        }
    }

    // The device was lost (a driver update, the GPU reset): the map makes a new GpuMap.
    internal bool Lost { get; private set; }

    internal static GpuMap? TryCreate(Action<IDisposable>? resourceAcquired = null)
    {
        try { return new GpuMap(resourceAcquired); }
        catch (Exception ex)
        {
            Debug.WriteLine("GPU map unavailable: " + ex.Message);
            return null;
        }
    }

    private GpuMap(Action<IDisposable>? resourceAcquired)
    {
        using var resources = new ResourceScope(resourceAcquired);
        resources.Own(renderWake);
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        d3d = resources.Own(D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels));
        immediate = resources.Own(d3d.ImmediateContext);
        finished = resources.Own(d3d.CreateQuery(QueryType.Event));
        factory = resources.Own(D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded));
        using (IDXGIDevice dxgi = d3d.QueryInterface<IDXGIDevice>()) device = resources.Own(factory.CreateDevice(dxgi));
        context = resources.Own(device.CreateDeviceContext(DeviceContextOptions.None));
        d3d9 = resources.Own(D9.D3D9.Direct3DCreate9Ex());
        IntPtr window = GetDesktopWindow();
        var present = new D9.PresentParameters
        {
            Windowed = true, SwapEffect = D9.SwapEffect.Discard, DeviceWindowHandle = window, PresentationInterval = D9.PresentInterval.Immediate,
            BackBufferWidth = 1, BackBufferHeight = 1, BackBufferFormat = D9.Format.Unknown
        };
        device9 = resources.Own(d3d9.CreateDeviceEx(0, D9.DeviceType.Hardware, window,
            D9.CreateFlags.HardwareVertexProcessing | D9.CreateFlags.Multithreaded | D9.CreateFlags.FpuPreserve, present));
        Canvas = resources.Own(D2DCanvas.Create(factory, context, device, resourceAcquired));
        Shaders = MapShaders.TryCreate(d3d);
        if (Shaders is not null) resources.Own(Shaders);
        resources.Commit();
    }

    // The texture for a map of this many device pixels (made again when the size or the DPI changes).
    internal void Resize(int pixelWidth, int pixelHeight, float dotsPerInch)
    {
        pixelWidth = Math.Max(1, pixelWidth);
        pixelHeight = Math.Max(1, pixelHeight);
        if (Pending)
        {
            pendingResize = (pixelWidth, pixelHeight, dotsPerInch);
            return;
        }
        if (back is not null && pixelWidth == width && pixelHeight == height && dotsPerInch == dpi) return;
        // Keep WPF's completed surface bound until Present replaces it, so a resize never leaves
        // D3DImage empty while the GPU or the compositor is still completing the new frame.
        context.Target = null;
        if (back != front) back?.Dispose();
        back = null;
        (width, height, dpi) = (pixelWidth, pixelHeight, dotsPerInch);
        back = MakeTarget(shared: false);
        context.SetDpi(dpi, dpi);
    }

    private FrameTarget MakeTarget(bool shared)
    {
        var made = new FrameTarget { Width = width, Height = height, Dpi = dpi };
        try
        {
            made.Texture = d3d.CreateTexture2D(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, null,
                BindFlags.RenderTarget | BindFlags.ShaderResource, shared ? ResourceOptionFlags.Shared : ResourceOptionFlags.None);
            made.View = d3d.CreateRenderTargetView(made.Texture);
            if (shared)
            {
                IntPtr handle;
                using (IDXGIResource resource = made.Texture.QueryInterface<IDXGIResource>()) handle = resource.SharedHandle;
                made.Texture9 = device9.CreateTexture((uint)width, (uint)height, 1, D9.Usage.RenderTarget, D9.Format.A8R8G8B8, D9.Pool.Default, ref handle);
                made.Surface9 = made.Texture9.GetSurfaceLevel(0);
            }
            using (IDXGISurface surface = made.Texture.QueryInterface<IDXGISurface>())
                made.Bitmap = context.CreateBitmapFromDxgiSurface(surface,
                    new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), dpi, dpi, BitmapOptions.Target | BitmapOptions.CannotDraw));
            return made;
        }
        catch { made.Dispose(); throw; }
    }

    // Hands the surface to WPF (again after the front buffer came back).
    internal void Bind()
    {
        if (front?.Surface9 is not { } surface || !frontReady) return;
        bool locked = Image.TryLock(new W.Duration(TimeSpan.Zero));
        try
        {
            if (!locked) return;
            Image.SetBackBuffer(WI.D3DResourceType.IDirect3DSurface9, surface.NativePointer, true);
            Image.AddDirtyRect(new W.Int32Rect(0, 0, front.Width, front.Height));
        }
        finally { Image.Unlock(); }
    }

    // Render asynchronously into a private target. Never leave a D3DImage lock across dispatcher turns:
    // WPF can already have captured its present callback when the next rendering callback locks the image.
    // Its shared surface must therefore stay complete even while this private frame is in flight.
    internal bool Render(Action<D2DCanvas> draw)
    {
        if (back?.Bitmap is null || Pending || Lost) return false;
        pending = true; recordingReady = false;
        SubmittedFrames++;
        try { return Record(draw); }
        finally { recordingReady = true; }
    }

    internal bool RenderAsync(MapDrawList frame)
    {
        if (back?.Bitmap is null || Pending || Lost || stopping) return false;
        pending = true; recordingReady = false;
        SubmittedFrames++;
        queuedFrame = frame;
        if (renderThread is null)
        {
            renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "San14 map renderer" };
            renderThread.SetApartmentState(ApartmentState.STA);
            renderThread.Start();
        }
        renderWake.Set();
        return true;
    }

    private void RenderLoop()
    {
        RenderThreadId = Environment.CurrentManagedThreadId;
        while (true)
        {
            renderWake.WaitOne();
            if (stopping) return;
            MapDrawList? frame = queuedFrame;
            queuedFrame = null;
            try
            {
                if (frame is not null && Record(canvas => frame.Play(canvas, this)))
                {
                    long started = Stopwatch.GetTimestamp();
                    var wait = new SpinWait();
                    // Wait on the worker, with exclusive ownership of the immediate context. The UI
                    // receives a completed frame instead of polling it on an unrelated timer.
                    while (!stopping)
                    {
                        var status = immediate.GetData(finished, IntPtr.Zero, 0, AsyncGetDataFlags.DoNotFlush);
                        status.CheckError();
                        if (status.Code == 0) break;
                        wait.SpinOnce(sleep1Threshold: -1);
                    }
                    LastGpuWait = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
            }
            catch (Exception ex)
            {
                RenderError = ex.ToString(); Lost = true;
                Debug.WriteLine("Map renderer stopped: " + RenderError);
            }
            finally { recordingReady = true; }
            if (!stopping && frame is not null) FrameReady?.Invoke();
        }
    }

    private bool Record(Action<D2DCanvas> draw)
    {
        try
        {
            long allocatedAt = GC.GetAllocatedBytesForCurrentThread();
            TimeSpan pauseAt = GC.GetTotalPauseDuration();
            var watch = Stopwatch.StartNew();
            context.Target = back!.Bitmap;
            context.BeginDraw(); drawing = true;
            Canvas.Begin(dpi, width, height);
            double drawn;
            try { draw(Canvas); Shaders?.PrepareGround(Canvas); }
            finally
            {
                Canvas.End(); drawn = watch.Elapsed.TotalMilliseconds; drawing = false;
                if (context.EndDraw().Failure) Lost = true;
            }
            if (Lost) return false;
            double ended = watch.Elapsed.TotalMilliseconds;
            immediate.End(finished); immediate.Flush();
            LastFrame = (drawn, ended - drawn, watch.Elapsed.TotalMilliseconds - ended);
            LastAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedAt;
            LastGcPauseMs = (GC.GetTotalPauseDuration() - pauseAt).TotalMilliseconds;
            submittedAt = Stopwatch.GetTimestamp();
            return true;
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        { RenderError = ex.ToString(); Lost = true; return false; }
    }

    internal void DrawGround(MapShaders shaders, MapShaders.Camera camera, byte[]? terrain = null)
    {
        shaders.PrepareGround(Canvas);
        Direct3D((dc, target) => shaders.DrawGround(dc, target, camera, terrain));
    }
    internal void DrawBorders(MapShaders shaders, MapShaders.Camera camera, byte[]? owners, ushort[]? counties, System.Drawing.Color[]? palette = null, byte[]? terrain = null)
        => Direct3D((dc, target) => shaders.DrawBorders(dc, target, camera, owners, counties, palette, terrain));

    internal bool Present()
    {
        if (!Pending || !recordingReady || Lost || back?.Texture is null) return false;
        bool locked = false;
        try
        {
            var status = immediate.GetData(finished, IntPtr.Zero, 0, AsyncGetDataFlags.DoNotFlush);
            if (status.Failure) { Lost = true; return false; }
            if (status.Code != 0) return false;
            locked = Image.TryLock(new W.Duration(TimeSpan.Zero));
            if (!locked)
            {
                Image.Unlock(); // TryLock increments the count even on timeout.
                SkippedLocks++;
                return false;
            }
            FrameTarget? previous = front;
            if (front is null || front.Width != width || front.Height != height || front.Dpi != dpi)
                front = MakeTarget(shared: true);
            // Only the completed image is copied while locked, within this dispatcher call. Finish this
            // small GPU copy before Unlock so a queued WPF present never reads partially updated pixels.
            long copiedAt = Stopwatch.GetTimestamp();
            immediate.CopyResource(front.Texture!, back.Texture);
            immediate.End(finished);
            immediate.Flush();
            var wait = new SpinWait();
            while (true)
            {
                status = immediate.GetData(finished, IntPtr.Zero, 0, AsyncGetDataFlags.DoNotFlush);
                if (status.Failure) { Lost = true; return false; }
                if (status.Code == 0) break;
                // Yield while the short copy completes without a timer-resolution-sized Sleep(1).
                wait.SpinOnce(sleep1Threshold: -1);
            }
            LastCopyWait = Stopwatch.GetElapsedTime(copiedAt).TotalMilliseconds;
            if (boundSurface != front.Surface9!.NativePointer)
            {
                Image.SetBackBuffer(WI.D3DResourceType.IDirect3DSurface9, front.Surface9.NativePointer, true);
                boundSurface = front.Surface9.NativePointer;
                BackBufferBindings++;
            }
            Image.AddDirtyRect(new W.Int32Rect(0, 0, front.Width, front.Height));
            frontReady = true;
            pending = false;
            PresentedFrames++;
            LastPresentDelay = Stopwatch.GetElapsedTime(submittedAt).TotalMilliseconds;
            if (previous != front) previous?.Dispose();
        }
        catch (SharpGen.Runtime.SharpGenException) { Lost = true; return false; }
        finally { if (locked) Image.Unlock(); }
        if (pendingResize is { } size)
        {
            pendingResize = null;
            Resize(size.Width, size.Height, size.Dpi);
        }
        return true;
    }

    // Direct3D drawing into the frame between Direct2D's: what Direct2D has drawn goes to the GPU first, the pass
    // sets all the state it needs (and it is all cleared after it), and Direct2D carries on.
    internal void Direct3D(Action<ID3D11DeviceContext, MapShaders.Target> pass)
    {
        if (!drawing || back?.View is null || Lost) return;
        bool ended = context.EndDraw().Success;
        try
        {
            if (ended) pass(immediate, new MapShaders.Target(back.View, width, height));
            else Lost = true;
        }
        finally
        {
            immediate.ClearState();
            context.BeginDraw();
            Canvas.Resume();
        }
    }

    private void ReleaseTarget()
    {
        context.Target = null;
        if (front != back) front?.Dispose();
        back?.Dispose();
        front = back = null;
        frontReady = false;
        DiscardPending();
    }

    private void DiscardPending() => pending = false;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stopping = true; renderWake.Set();
        renderThread?.Join();
        renderWake.Dispose();
        Image.Lock();
        Image.SetBackBuffer(WI.D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
        Image.Unlock();
        ReleaseTarget();
        Shaders?.Dispose();
        Canvas.Dispose();
        context.Dispose();
        device.Dispose();
        factory.Dispose();
        finished.Dispose();
        immediate.Dispose();
        d3d.Dispose();
        device9.Dispose();
        d3d9.Dispose();
    }
}

// IMapCanvas on Direct2D. WPF's brushes, pens, geometries, bitmaps and text are turned into their Direct2D
// counterparts once and kept while they are in use (a resource not drawn for a while is let go).
internal sealed class D2DCanvas : IGpuMapCanvas, IDisposable
{
    private static readonly PixelFormat Premultiplied = new(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);

    private readonly ID2D1Factory1 factory;
    private ID2D1DeviceContext context;
    private readonly ID2D1DeviceContext mipContext;   // draws the smaller copies of big pictures
    private readonly ID2D1DeviceContext spriteContext; // records vectors while mipContext prepares their bitmaps
    private readonly DW.IDWriteFactory write;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double now, lastSweep;
    private float dpi = 96;
    private Matrix3x2 transform = Matrix3x2.Identity;
    private W.Rect viewport;
    internal int BitmapUploads { get; private set; }
    internal int GeometryBuilds { get; private set; }
    internal int Culled { get; private set; }
    private readonly Stack<Matrix3x2> transforms = new();

    internal D2DCanvas(ID2D1Factory1 factory, ID2D1DeviceContext context, ID2D1DeviceContext mipContext, ID2D1DeviceContext spriteContext, DW.IDWriteFactory write)
    {
        this.factory = factory;
        this.context = context;
        this.mipContext = mipContext;
        this.spriteContext = spriteContext;
        this.write = write;
    }

    internal static D2DCanvas Create(ID2D1Factory1 factory, ID2D1DeviceContext context, ID2D1Device device, Action<IDisposable>? acquired)
    {
        using var resources = new ResourceScope(acquired);
        var mips = resources.Own(device.CreateDeviceContext(DeviceContextOptions.None));
        var sprites = resources.Own(device.CreateDeviceContext(DeviceContextOptions.None));
        var text = resources.Own(DW.DWrite.DWriteCreateFactory<DW.IDWriteFactory>(DW.FactoryType.Shared));
        var canvas = new D2DCanvas(factory, context, mips, sprites, text);
        resources.Commit();
        return canvas;
    }

    // How bitmaps drawn through IMapCanvas.DrawImage are scaled (the labels' layers ask for high quality).
    internal WM.BitmapScalingMode ImageMode { get; set; } = WM.BitmapScalingMode.HighQuality;

    // Uploading a bitmap and making its smaller copies costs time on the first frame that shows it (the first
    // zoom out shows hundreds of models at once). A frame spends at most this on pictures that may wait (models,
    // icons, labels); the rest are drawn on the next frames, and Deferred asks for them. The ground is never
    // left out: a hole would show.
    private const double UploadBudget = 6;
    private double uploadSpent;
    private int deferredUploads;

    internal bool Deferred { get; private set; }

    internal void Begin(float dotsPerInch, int pixelWidth, int pixelHeight)
    {
        dpi = dotsPerInch;
        viewport = new W.Rect(0, 0, pixelWidth * 96.0 / dpi, pixelHeight * 96.0 / dpi);
        BitmapUploads = GeometryBuilds = Culled = SpriteBuilds = SpriteDraws = 0;
        now = clock.Elapsed.TotalSeconds;
        transforms.Clear();
        transform = Matrix3x2.Identity;
        context.Transform = transform;
        context.AntialiasMode = AntialiasMode.PerPrimitive;
        context.TextAntialiasMode = TextAntialiasMode.Grayscale;
        ImageMode = WM.BitmapScalingMode.HighQuality;
        uploadSpent = 0;
        Deferred = false;
    }

    // Whether a picture that may wait still fits in this frame's upload time.
    private bool MayUpload(bool mayDefer)
    {
        if (!mayDefer || uploadSpent < UploadBudget) return true;
        Deferred = true;
        deferredUploads++;
        return false;
    }

    internal void End()
    {
        if (now - lastSweep < 2) return;
        lastSweep = now;
        Sweep(bitmaps, 30, e => e.Dispose());
        Sweep(geometries, 3, g => g.Geometry.Dispose());
        Sweep(layouts, 30, l => l.Layout.Dispose());
        Sweep(gradients, 30, g => g.Dispose());
        Sweep(labels, 30, l => l.Bitmap.Dispose());
        Sweep(tints, 30, e => e.Dispose());
        Sweep(shadows, 30, e => e.Dispose());
        Sweep(sprites, 30, e => e.Commands.Dispose());
    }

    private void Sweep<TKey, TValue>(Dictionary<TKey, TValue> cache, double seconds, Action<TValue> release) where TKey : notnull where TValue : IUsed
    {
        foreach (var pair in cache.Where(p => now - p.Value.Used > seconds).ToList())
        {
            release(pair.Value);
            cache.Remove(pair.Key);
        }
    }

    private interface IUsed
    {
        double Used { get; }
    }

    // Direct2D's state again after a Direct3D pass (GpuMap.Direct3D).
    internal void Resume()
    {
        context.Transform = transform;
        context.AntialiasMode = AntialiasMode.PerPrimitive;
        context.TextAntialiasMode = TextAntialiasMode.Grayscale;
    }

    public void Clear(WM.Color colour) => context.Clear(new Color4(colour.R / 255f, colour.G / 255f, colour.B / 255f, colour.A / 255f));

    // ---- Transforms ----

    public void PushTransform(WM.Transform value) => PushMatrix(value.Value);

    public void PushMatrix(WM.Matrix m)
    {
        transforms.Push(transform);
        transform = new Matrix3x2((float)m.M11, (float)m.M12, (float)m.M21, (float)m.M22, (float)m.OffsetX, (float)m.OffsetY) * transform;
        context.Transform = transform;
    }

    public void Pop()
    {
        transform = transforms.Count > 0 ? transforms.Pop() : Matrix3x2.Identity;
        context.Transform = transform;
    }

    // Device pixels per unit of the current drawing space.
    private double DeviceScale => Math.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12) * dpi / 96;

    private bool Visible(W.Rect area, double stroke = 0)
    {
        if (area.IsEmpty) return false;
        area.Inflate(stroke / 2, stroke / 2);
        Vector2 a = Vector2.Transform(new((float)area.Left, (float)area.Top), transform);
        Vector2 b = Vector2.Transform(new((float)area.Right, (float)area.Top), transform);
        Vector2 c = Vector2.Transform(new((float)area.Left, (float)area.Bottom), transform);
        Vector2 d = Vector2.Transform(new((float)area.Right, (float)area.Bottom), transform);
        double left = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)), top = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
        double right = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)), bottom = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
        if (left <= viewport.Right + 1 && right >= -1 && top <= viewport.Bottom + 1 && bottom >= -1) return true;
        Culled++;
        return false;
    }

    // ---- Bitmaps ----

    private sealed class BitmapEntry(ID2D1Bitmap1 bitmap, int width, int height) : IUsed
    {
        internal readonly ID2D1Bitmap1 Bitmap = bitmap;
        internal readonly int Width = width, Height = height;
        internal bool Opaque;
        internal readonly List<ID2D1Bitmap1> Smaller = [];     // half, quarter, ... size
        internal readonly Dictionary<int, ID2D1BitmapBrush1> Brushes = [];
        public double Used { get; set; }

        internal void Dispose()
        {
            foreach (ID2D1BitmapBrush1 brush in Brushes.Values) brush.Dispose();
            foreach (ID2D1Bitmap1 small in Smaller) small.Dispose();
            Bitmap.Dispose();
        }
    }

    private readonly Dictionary<WMI.BitmapSource, BitmapEntry> bitmaps = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, WMI.BitmapSource> layerImages = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(WMI.BitmapSource Source, int Colour), BitmapEntry> tints = [];
    private readonly Dictionary<(WMI.BitmapSource Source, int X, int Y), BitmapEntry> shadows = [];

    // A map layer replaces its whole image. Once the next frame captures that replacement, none of
    // its old pixels are needed: the compositor retains a rendered front frame, not these inputs.
    public void SetLayerImage(object layer, WMI.BitmapSource? source)
    {
        if (layerImages.TryGetValue(layer, out var previous) && ReferenceEquals(previous, source)) return;
        layerImages.Remove(layer);
        if (previous is not null && !layerImages.Values.Any(image => ReferenceEquals(image, previous)))
        {
            if (bitmaps.Remove(previous, out var entry)) entry.Dispose();
        }
        if (source is not null) layerImages[layer] = source;
    }

    public void DrawShadowed(GameModels.Sprite sprite, W.Point origin, double opacity = 1)
    {
        var image = sprite.Image;
        int w = image.PixelWidth, h = image.PixelHeight;
        double pixelsX = w / sprite.World.Width, pixelsY = h / sprite.World.Height;
        int dx = (int)Math.Round(MapSurface.ShadowOffset.X * pixelsX), dy = (int)Math.Round(MapSurface.ShadowOffset.Y * pixelsY);
        var area = new W.Rect(origin.X + sprite.World.X, origin.Y + sprite.World.Y,
            (w + dx) / pixelsX, (h + dy) / pixelsY);
        if (opacity <= 0 || !Visible(area)) return;
        var key = (image, dx, dy);
        if (!shadows.TryGetValue(key, out BitmapEntry? entry))
        {
            if (!MayUpload(true) || Entry(image, true) is not { } input) return;
            var watch = Stopwatch.StartNew();
            ID2D1Bitmap1 bitmap = mipContext.CreateBitmap(new SizeI(w + dx, h + dy), IntPtr.Zero, 0,
                new BitmapProperties1(Premultiplied, 96, 96, BitmapOptions.Target));
            try
            {
                using var effect = new Vortice.Direct2D1.Effects.ColorMatrix(mipContext)
                {
                    Matrix = new Matrix5x4(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                        0, 0, 0, (float)MapSurface.ShadowStrength, MapSurface.ShadowColor.R / 255f, MapSurface.ShadowColor.G / 255f, MapSurface.ShadowColor.B / 255f, 0),
                    AlphaMode = ColorMatrixAlphaMode.Premultiplied
                };
                effect.SetInput(0, input.Bitmap, true);
                mipContext.Target = bitmap;
                mipContext.Transform = Matrix3x2.Identity;
                mipContext.BeginDraw();
                mipContext.Clear(new Color4(0, 0, 0, 0));
                using ID2D1Image output = effect.Output;
                mipContext.DrawImage(output, new Vector2(dx, dy));
                mipContext.DrawBitmap(input.Bitmap, new RawRectF(0, 0, w, h), 1, InterpolationMode.NearestNeighbor, null, null);
                mipContext.EndDraw().CheckError();
            }
            catch { bitmap.Dispose(); throw; }
            finally { mipContext.Target = null; }
            shadows[key] = entry = new BitmapEntry(bitmap, w + dx, h + dy);
            uploadSpent += watch.Elapsed.TotalMilliseconds;
            BitmapUploads++;
        }
        entry.Used = now;
        ID2D1Bitmap1 shown = Sized(entry, area.Width * DeviceScale / entry.Width, out _, true);
        context.DrawBitmap(shown, Rect(area), (float)opacity, InterpolationMode.Linear, null, null);
    }

    // Force-coloured art stays on the GPU: one source upload, then a cached colour-matrix pass.
    public void DrawTinted(WMI.BitmapSource source, System.Drawing.Color colour, W.Rect area)
    {
        if (colour.R == 255 && colour.G == 255 && colour.B == 255) { DrawImage(source, area); return; }
        if (!Visible(area) || area.Width <= 0 || area.Height <= 0) return;
        var key = (source, colour.ToArgb());
        if (!tints.TryGetValue(key, out BitmapEntry? entry))
        {
            if (!MayUpload(true) || Entry(source, true) is not { } input) return;
            var watch = Stopwatch.StartNew();
            ID2D1Bitmap1 bitmap = mipContext.CreateBitmap(new SizeI(input.Width, input.Height), IntPtr.Zero, 0,
                new BitmapProperties1(Premultiplied, 96, 96, BitmapOptions.Target));
            try
            {
                using var effect = new Vortice.Direct2D1.Effects.ColorMatrix(mipContext)
                {
                    Matrix = new Matrix5x4(colour.R / 255f, 0, 0, 0, 0, colour.G / 255f, 0, 0,
                        0, 0, colour.B / 255f, 0, 0, 0, 0, 1, 0, 0, 0, 0),
                    AlphaMode = ColorMatrixAlphaMode.Premultiplied
                };
                effect.SetInput(0, input.Bitmap, true);
                mipContext.Target = bitmap;
                mipContext.Transform = Matrix3x2.Identity;
                mipContext.BeginDraw();
                mipContext.Clear(new Color4(0, 0, 0, 0));
                using ID2D1Image output = effect.Output;
                mipContext.DrawImage(output);
                mipContext.EndDraw().CheckError();
            }
            catch { bitmap.Dispose(); throw; }
            finally { mipContext.Target = null; }
            tints[key] = entry = new BitmapEntry(bitmap, input.Width, input.Height);
            uploadSpent += watch.Elapsed.TotalMilliseconds;
            BitmapUploads++;
        }
        entry.Used = now;
        ID2D1Bitmap1 shown = ImageMode is WM.BitmapScalingMode.HighQuality or WM.BitmapScalingMode.Fant
            ? Sized(entry, area.Width * DeviceScale / entry.Width, out _, true) : entry.Bitmap;
        context.DrawBitmap(shown, Rect(area), 1, ImageMode == WM.BitmapScalingMode.NearestNeighbor ? InterpolationMode.NearestNeighbor : InterpolationMode.Linear, null, null);
    }

    private sealed class LabelEntry(ID2D1Bitmap1 bitmap, W.Rect area) : IUsed
    {
        internal readonly ID2D1Bitmap1 Bitmap = bitmap;
        internal readonly W.Rect Area = area;
        public double Used { get; set; }
    }
    private readonly Dictionary<WM.Geometry, LabelEntry> labels = new(ReferenceEqualityComparer.Instance);

    // GPU labels are rasterized and kept on the same D2D device, avoiding WPF RenderTargetBitmap,
    // the CPU pixel copy and a second texture upload for every new text/size.
    public void DrawOutlined(WM.Geometry shape, W.Rect bounds, WM.Brush ink, WM.Pen halo, double x, double y, double scale, double pixelsPerDip)
    {
        if (!labels.TryGetValue(shape, out LabelEntry? label))
        {
            bounds.Inflate(halo.Thickness / 2 + 1, halo.Thickness / 2 + 1);
            double left = Math.Floor(bounds.X * pixelsPerDip) / pixelsPerDip, top = Math.Floor(bounds.Y * pixelsPerDip) / pixelsPerDip;
            int width = Math.Max(1, (int)Math.Ceiling((bounds.Right - left) * pixelsPerDip));
            int height = Math.Max(1, (int)Math.Ceiling((bounds.Bottom - top) * pixelsPerDip));
            if (!Visible(new W.Rect(x + left * scale, y + top * scale, width / pixelsPerDip * scale, height / pixelsPerDip * scale)) || !MayUpload(true)) return;
            var watch = Stopwatch.StartNew();
            ID2D1Bitmap1 bitmap = mipContext.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0, new BitmapProperties1(Premultiplied, 96, 96, BitmapOptions.Target));
            try
            {
                GeometryEntry path = Path(shape);
                mipContext.Target = bitmap;
                mipContext.Transform = Matrix3x2.CreateTranslation((float)-left, (float)-top) * Matrix3x2.CreateScale((float)pixelsPerDip);
                mipContext.BeginDraw();
                mipContext.Clear(new Color4(0, 0, 0, 0));
                if (Brush(halo.Brush, path.Bounds) is { } edge) mipContext.DrawGeometry(path.Geometry, edge, (float)halo.Thickness, Stroke(halo));
                if (Brush(ink, path.Bounds) is { } fill) mipContext.FillGeometry(path.Geometry, fill);
                mipContext.EndDraw().CheckError();
            }
            catch { bitmap.Dispose(); throw; }
            finally { mipContext.Target = null; mipContext.Transform = Matrix3x2.Identity; }
            labels[shape] = label = new LabelEntry(bitmap, new W.Rect(left, top, width / pixelsPerDip, height / pixelsPerDip));
            uploadSpent += watch.Elapsed.TotalMilliseconds;
            BitmapUploads++;
        }
        label.Used = now;
        W.Rect area = label.Area;
        area = new W.Rect(x + area.X * scale, y + area.Y * scale, area.Width * scale, area.Height * scale);
        if (Visible(area)) context.DrawBitmap(label.Bitmap, Rect(area), 1, InterpolationMode.Linear, null, null);
    }

    private BitmapEntry? Entry(WMI.BitmapSource source, bool mayDefer)
    {
        if (!bitmaps.TryGetValue(source, out BitmapEntry? entry))
        {
            if (!MayUpload(mayDefer)) return null;
            var watch = Stopwatch.StartNew();
            WMI.BitmapSource pixels = source.Format == WM.PixelFormats.Pbgra32 ? source : new WMI.FormatConvertedBitmap(source, WM.PixelFormats.Pbgra32, null, 0);
            int w = pixels.PixelWidth, h = pixels.PixelHeight, stride = w * 4;
            using var data = NativePixelBuffer.Copy(pixels, stride);
            entry = new BitmapEntry(context.CreateBitmap(new SizeI(w, h), data.Pointer, (uint)stride, new BitmapProperties1(Premultiplied, 96, 96)), w, h)
                { Opaque = data.IsOpaque32() };
            bitmaps[source] = entry;
            BitmapUploads++;
            uploadSpent += watch.Elapsed.TotalMilliseconds;
        }
        entry.Used = now;
        return entry;
    }

    // A caller owns the returned COM reference. Its shader view keeps the texture alive even if
    // the bitmap cache later retires it. Transparent images need the shader's separate BGRA copy.
    internal ID3D11Texture2D? OpaqueTexture(WMI.BitmapSource source)
    {
        if (Entry(source, false) is not { Opaque: true } entry) return null;
        using IDXGISurface surface = entry.Bitmap.Surface;
        ID3D11Texture2D texture = surface.QueryInterface<ID3D11Texture2D>();
        var description = texture.Description;
        if (description.Width == entry.Width && description.Height == entry.Height && description.MipLevels == 1 &&
            description.ArraySize == 1 && description.Format == Format.B8G8R8A8_UNorm &&
            (description.BindFlags & BindFlags.ShaderResource) != 0)
            return texture;
        texture.Dispose();
        return null;
    }

    // The copy of a picture to draw at this many device pixels per bitmap pixel: shrunk by halves (each
    // pixel the mean of four), so a far-off picture is smooth, as WPF's high-quality scaling makes it.
    private ID2D1Bitmap1 Sized(BitmapEntry entry, double scale, out int level, bool mayDefer = true)
    {
        level = scale >= 0.75 ? 0 : Math.Min(8, (int)Math.Floor(Math.Log2(1 / scale)));
        var watch = Stopwatch.StartNew();
        while (entry.Smaller.Count < level && MayUpload(mayDefer))
        {
            ID2D1Bitmap1 previous = entry.Smaller.Count == 0 ? entry.Bitmap : entry.Smaller[^1];
            SizeI size = previous.PixelSize;
            if (size.Width < 4 || size.Height < 4) break;
            int w = (size.Width + 1) / 2, h = (size.Height + 1) / 2;
            ID2D1Bitmap1 next = mipContext.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, new BitmapProperties1(Premultiplied, 96, 96, BitmapOptions.Target));
            mipContext.Target = next;
            mipContext.BeginDraw();
            mipContext.Clear(new Color4(0, 0, 0, 0));
            mipContext.DrawBitmap(previous, new RawRectF(0, 0, w, h), 1, InterpolationMode.Linear, null, null);
            mipContext.EndDraw();
            mipContext.Target = null;
            entry.Smaller.Add(next);
        }
        uploadSpent += watch.Elapsed.TotalMilliseconds;
        level = Math.Min(level, entry.Smaller.Count);
        return level == 0 ? entry.Bitmap : entry.Smaller[level - 1];
    }

    public void DrawImage(WMI.BitmapSource image, W.Rect area) => DrawBitmap(image, area, 1, ImageMode, mayDefer: true);

    // A bitmap into an area of the current space; snap puts its edges on device pixels (tiles meet without seams).
    // mayDefer lets a picture not uploaded yet wait for a later frame (UploadBudget).
    public void DrawBitmap(WMI.BitmapSource image, W.Rect area, double opacity, WM.BitmapScalingMode mode, bool snap = false, bool mayDefer = false)
    {
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0 || opacity <= 0) return;
        if (!Visible(area)) return;
        if (Entry(image, mayDefer) is not { } entry) return;
        if (snap) area = Snapped(area);
        double scale = area.Width * DeviceScale / entry.Width;
        ID2D1Bitmap1 bitmap = entry.Bitmap;
        InterpolationMode interpolation = InterpolationMode.Linear;
        if (mode == WM.BitmapScalingMode.NearestNeighbor) interpolation = InterpolationMode.NearestNeighbor;
        else if (mode is WM.BitmapScalingMode.HighQuality or WM.BitmapScalingMode.Fant) bitmap = Sized(entry, scale, out _, mayDefer);
        context.DrawBitmap(bitmap, Rect(area), (float)opacity, interpolation, null, null);
    }

    private W.Rect Snapped(W.Rect area)
    {
        float device = dpi / 96;
        Vector2 a = Vector2.Transform(new Vector2((float)area.Left, (float)area.Top), transform) * device;
        Vector2 b = Vector2.Transform(new Vector2((float)area.Right, (float)area.Bottom), transform) * device;
        if (!Matrix3x2.Invert(transform, out Matrix3x2 inverse)) return area;
        Vector2 p = Vector2.Transform(new Vector2(MathF.Round(a.X), MathF.Round(a.Y)) / device, inverse);
        Vector2 q = Vector2.Transform(new Vector2(MathF.Round(b.X), MathF.Round(b.Y)) / device, inverse);
        return new W.Rect(new W.Point(p.X, p.Y), new W.Point(q.X, q.Y));
    }

    // A drawing (the models' layer): its groups, images and shapes.
    internal void DrawDrawing(WM.Drawing? drawing, WM.BitmapScalingMode mode, double opacity)
    {
        switch (drawing)
        {
            case WM.DrawingGroup group:
                WM.BitmapScalingMode own = WM.RenderOptions.GetBitmapScalingMode(group);
                if (own != WM.BitmapScalingMode.Unspecified) mode = own;
                if (group.Transform is { } t) PushTransform(t);
                foreach (WM.Drawing child in group.Children)
                    if (Visible(child.Bounds)) DrawDrawing(child, mode, opacity * group.Opacity);
                if (group.Transform is not null) Pop();
                break;
            case WM.ImageDrawing { ImageSource: WMI.BitmapSource source } image:
                DrawBitmap(source, image.Rect, opacity, mode, mayDefer: true);
                break;
            case WM.GeometryDrawing shape:
                DrawGeometry(shape.Brush, shape.Pen, shape.Geometry);
                break;
        }
    }

    // ---- Brushes and pens ----

    private readonly Dictionary<uint, ID2D1SolidColorBrush> solids = [];

    private sealed class GradientEntry(ID2D1LinearGradientBrush brush) : IUsed
    {
        internal readonly ID2D1LinearGradientBrush Brush = brush;
        public double Used { get; set; }
        internal void Dispose() => Brush.Dispose();
    }

    private readonly Dictionary<WM.LinearGradientBrush, GradientEntry> gradients = new(ReferenceEqualityComparer.Instance);

    private ID2D1SolidColorBrush Solid(WM.Color colour, double opacity)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(colour.A * opacity), 0, 255);
        uint key = (uint)alpha << 24 | (uint)colour.R << 16 | (uint)colour.G << 8 | colour.B;
        if (!solids.TryGetValue(key, out ID2D1SolidColorBrush? brush))
        {
            if (solids.Count > 2000)
            {
                foreach (ID2D1SolidColorBrush old in solids.Values) old.Dispose();
                solids.Clear();
            }
            solids[key] = brush = context.CreateSolidColorBrush(new Color4(colour.R / 255f, colour.G / 255f, colour.B / 255f, alpha / 255f));
        }
        return brush;
    }

    // The Direct2D brush for a WPF brush filling an area (gradients and pictures are stretched over it).
    private ID2D1Brush? Brush(WM.Brush? brush, W.Rect bounds, double opacity = 1)
    {
        switch (brush)
        {
            case null:
                return null;
            case WM.SolidColorBrush solid:
                return Solid(solid.Color, solid.Opacity * opacity);
            case WM.LinearGradientBrush linear:
            {
                if (!gradients.TryGetValue(linear, out GradientEntry? entry))
                {
                    GradientStop[] stops = linear.GradientStops.Select(s => new GradientStop((float)s.Offset, new Color4(s.Color.R / 255f, s.Color.G / 255f, s.Color.B / 255f, s.Color.A / 255f))).ToArray();
                    using ID2D1GradientStopCollection collection = context.CreateGradientStopCollection(stops);
                    gradients[linear] = entry = new GradientEntry(context.CreateLinearGradientBrush(new LinearGradientBrushProperties(), collection));
                }
                entry.Used = now;
                W.Point start = linear.StartPoint, end = linear.EndPoint;
                if (linear.MappingMode == WM.BrushMappingMode.RelativeToBoundingBox)
                {
                    start = new W.Point(bounds.X + start.X * bounds.Width, bounds.Y + start.Y * bounds.Height);
                    end = new W.Point(bounds.X + end.X * bounds.Width, bounds.Y + end.Y * bounds.Height);
                }
                entry.Brush.StartPoint = new Vector2((float)start.X, (float)start.Y);
                entry.Brush.EndPoint = new Vector2((float)end.X, (float)end.Y);
                entry.Brush.Opacity = (float)(linear.Opacity * opacity);
                return entry.Brush;
            }
            case WM.ImageBrush { ImageSource: WMI.BitmapSource source } picture:
            {
                if (Entry(source, mayDefer: true) is not { } entry) return null;
                ID2D1Bitmap1 bitmap = Sized(entry, bounds.Width * DeviceScale / entry.Width, out int level);
                if (!entry.Brushes.TryGetValue(level, out ID2D1BitmapBrush1? fill))
                    entry.Brushes[level] = fill = context.CreateBitmapBrush(bitmap, new BitmapBrushProperties1(ExtendMode.Clamp, ExtendMode.Clamp, InterpolationMode.Linear), null);
                SizeI size = bitmap.PixelSize;
                fill.Transform = Matrix3x2.CreateScale((float)(bounds.Width / size.Width), (float)(bounds.Height / size.Height)) *
                    Matrix3x2.CreateTranslation((float)bounds.X, (float)bounds.Y);
                fill.Opacity = (float)(picture.Opacity * opacity);
                return fill;
            }
            default:
                return null;
        }
    }

    private readonly Dictionary<(WM.PenLineCap, WM.PenLineCap, WM.PenLineCap, WM.PenLineJoin, double, string), ID2D1StrokeStyle1> strokes = [];

    private ID2D1StrokeStyle1? Stroke(WM.Pen pen)
    {
        WM.DashStyle? dash = pen.DashStyle;
        bool dashed = dash is not null && dash.Dashes.Count > 0;
        if (!dashed && pen.StartLineCap == WM.PenLineCap.Flat && pen.EndLineCap == WM.PenLineCap.Flat && pen.LineJoin == WM.PenLineJoin.Miter) return null;
        var key = (pen.StartLineCap, pen.EndLineCap, pen.DashCap, pen.LineJoin, pen.MiterLimit, dashed ? string.Join(",", dash!.Dashes) + "@" + dash.Offset : "");
        if (!strokes.TryGetValue(key, out ID2D1StrokeStyle1? style))
        {
            static CapStyle Cap(WM.PenLineCap cap) => cap switch
            {
                WM.PenLineCap.Round => CapStyle.Round, WM.PenLineCap.Square => CapStyle.Square, WM.PenLineCap.Triangle => CapStyle.Triangle, _ => CapStyle.Flat
            };
            var properties = new StrokeStyleProperties1
            {
                StartCap = Cap(pen.StartLineCap), EndCap = Cap(pen.EndLineCap), DashCap = Cap(pen.DashCap),
                LineJoin = pen.LineJoin switch { WM.PenLineJoin.Round => LineJoin.Round, WM.PenLineJoin.Bevel => LineJoin.Bevel, _ => LineJoin.MiterOrBevel },
                MiterLimit = (float)pen.MiterLimit, DashStyle = dashed ? DashStyle.Custom : DashStyle.Solid, DashOffset = dashed ? (float)dash!.Offset : 0
            };
            strokes[key] = style = dashed ? factory.CreateStrokeStyle(properties, dash!.Dashes.Select(d => (float)d).ToArray()) : factory.CreateStrokeStyle(properties);
        }
        return style;
    }

    private static RawRectF Rect(W.Rect r) => new((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom);

    // ---- Shapes ----

    public void DrawRectangle(WM.Brush? brush, WM.Pen? pen, W.Rect area) => FillRectangle(brush, pen, area, 1);

    public void FillRectangle(WM.Brush? brush, WM.Pen? pen, W.Rect area, double opacity)
    {
        if (!Visible(area, pen?.Thickness ?? 0)) return;
        if (Brush(brush, area, opacity) is { } fill) context.FillRectangle(Rect(area), fill);
        if (pen is { Thickness: > 0 } && Brush(pen.Brush, area, opacity) is { } edge) context.DrawRectangle(Rect(area), edge, (float)pen.Thickness, Stroke(pen));
    }

    public void DrawRoundedRectangle(WM.Brush? brush, WM.Pen? pen, W.Rect area, double radiusX, double radiusY)
    {
        if (!Visible(area, pen?.Thickness ?? 0)) return;
        var shape = new RoundedRectangle { Rect = Rect(area), RadiusX = (float)radiusX, RadiusY = (float)radiusY };
        if (Brush(brush, area) is { } fill) context.FillRoundedRectangle(shape, fill);
        if (pen is not { Thickness: > 0 } || Brush(pen.Brush, area) is not { } edge) return;
        if (Stroke(pen) is { } style) context.DrawRoundedRectangle(shape, edge, (float)pen.Thickness, style);
        else context.DrawRoundedRectangle(shape, edge, (float)pen.Thickness);
    }

    public void DrawEllipse(WM.Brush? brush, WM.Pen? pen, W.Point centre, double radiusX, double radiusY)
    {
        var bounds = new W.Rect(centre.X - radiusX, centre.Y - radiusY, radiusX * 2, radiusY * 2);
        if (!Visible(bounds, pen?.Thickness ?? 0)) return;
        var point = new Vector2((float)centre.X, (float)centre.Y);
        var shape = new Ellipse(point, (float)radiusX, (float)radiusY);
        if (Brush(brush, bounds) is { } fill) context.FillEllipse(shape, fill);
        if (pen is { Thickness: > 0 } && Brush(pen.Brush, bounds) is { } edge) context.DrawEllipse(shape, edge, (float)pen.Thickness, Stroke(pen));
    }

    public void DrawLine(WM.Pen pen, W.Point from, W.Point to)
    {
        if (pen.Thickness <= 0 || !Visible(new W.Rect(from, to), pen.Thickness) || Brush(pen.Brush, new W.Rect(from, to)) is not { } ink) return;
        context.DrawLine(new Vector2((float)from.X, (float)from.Y), new Vector2((float)to.X, (float)to.Y), ink, (float)pen.Thickness, Stroke(pen));
    }

    private sealed class GeometryEntry(ID2D1PathGeometry geometry, W.Rect bounds) : IUsed
    {
        internal readonly ID2D1PathGeometry Geometry = geometry;
        internal readonly W.Rect Bounds = bounds;
        public double Used { get; set; }
    }

    private readonly Dictionary<WM.Geometry, GeometryEntry> geometries = new(ReferenceEqualityComparer.Instance);

    // A WPF geometry as a Direct2D path, figure by figure and segment by segment (lines and Béziers stay
    // what they are; an arc, which the map does not use, is flattened). Open, unfilled figures (a unit's
    // route) are kept: flattening the whole geometry would drop them.
    private GeometryEntry Path(WM.Geometry geometry)
    {
        if (!geometries.TryGetValue(geometry, out GeometryEntry? entry))
        {
            WM.PathGeometry shape = WM.PathGeometry.CreateFromGeometry(geometry);
            WM.Matrix m = shape.Transform?.Value ?? WM.Matrix.Identity;
            Vector2 V(W.Point p)
            {
                p = m.Transform(p);
                return new Vector2((float)p.X, (float)p.Y);
            }
            ID2D1PathGeometry path = factory.CreatePathGeometry();
            GeometryBuilds++;
            using (ID2D1GeometrySink sink = path.Open())
            {
                sink.SetFillMode(shape.FillRule == WM.FillRule.Nonzero ? FillMode.Winding : FillMode.Alternate);
                foreach (WM.PathFigure figure in shape.Figures)
                {
                    sink.BeginFigure(V(figure.StartPoint), figure.IsFilled ? FigureBegin.Filled : FigureBegin.Hollow);
                    W.Point last = figure.StartPoint;
                    foreach (WM.PathSegment segment in figure.Segments)
                    {
                        switch (segment)
                        {
                            case WM.LineSegment line:
                                sink.AddLine(V(line.Point));
                                last = line.Point;
                                break;
                            case WM.PolyLineSegment poly:
                                foreach (W.Point point in poly.Points) sink.AddLine(V(point));
                                if (poly.Points.Count > 0) last = poly.Points[^1];
                                break;
                            case WM.QuadraticBezierSegment quadratic:
                                sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = V(quadratic.Point1), Point2 = V(quadratic.Point2) });
                                last = quadratic.Point2;
                                break;
                            case WM.PolyQuadraticBezierSegment quadratics:
                                for (int i = 0; i + 1 < quadratics.Points.Count; i += 2)
                                    sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = V(quadratics.Points[i]), Point2 = V(quadratics.Points[i + 1]) });
                                if (quadratics.Points.Count > 0) last = quadratics.Points[^1];
                                break;
                            case WM.BezierSegment cubic:
                                Vector2 c1 = V(cubic.Point1), c2 = V(cubic.Point2), c3 = V(cubic.Point3);
                                sink.AddBezier(new BezierSegment(in c1, in c2, in c3));
                                last = cubic.Point3;
                                break;
                            case WM.PolyBezierSegment cubics:
                                for (int i = 0; i + 2 < cubics.Points.Count; i += 3)
                                {
                                    Vector2 a = V(cubics.Points[i]), b = V(cubics.Points[i + 1]), c = V(cubics.Points[i + 2]);
                                    sink.AddBezier(new BezierSegment(in a, in b, in c));
                                }
                                if (cubics.Points.Count > 0) last = cubics.Points[^1];
                                break;
                            default:
                                // An arc: its flattened points, from a one-segment figure of its own.
                                var single = new WM.PathGeometry([new WM.PathFigure(last, [segment.Clone()], false)]);
                                foreach (WM.PathFigure flat in single.GetFlattenedPathGeometry(0.05, WM.ToleranceType.Absolute).Figures)
                                    foreach (WM.PathSegment piece in flat.Segments)
                                        if (piece is WM.PolyLineSegment pieces)
                                            foreach (W.Point point in pieces.Points)
                                            {
                                                sink.AddLine(V(point));
                                                last = point;
                                            }
                                break;
                        }
                    }
                    sink.EndFigure(figure.IsClosed ? FigureEnd.Closed : FigureEnd.Open);
                }
                sink.Close();
            }
            W.Rect bounds = shape.Bounds;
            if (bounds.IsEmpty) bounds = new W.Rect(0, 0, 0, 0);
            geometries[geometry] = entry = new GeometryEntry(path, bounds);
        }
        entry.Used = now;
        return entry;
    }

    public void DrawGeometry(WM.Brush? brush, WM.Pen? pen, WM.Geometry geometry)
    {
        W.Rect bounds = geometries.TryGetValue(geometry, out GeometryEntry? cached) ? cached.Bounds : geometry.Bounds;
        if (!Visible(bounds, StrokeExtent(pen))) return;
        GeometryEntry path = Path(geometry);
        if (Brush(brush, path.Bounds) is { } fill) context.FillGeometry(path.Geometry, fill);
        if (pen is { Thickness: > 0 } && Brush(pen.Brush, path.Bounds) is { } edge) context.DrawGeometry(path.Geometry, edge, (float)pen.Thickness, Stroke(pen));
    }

    public void DrawGeometryAt(WM.Brush? brush, WM.Pen? pen, WM.Geometry geometry, W.Point origin, double scale = 1)
    {
        PushMatrix(new WM.Matrix(scale, 0, 0, scale, origin.X, origin.Y));
        try
        {
            W.Rect bounds = geometries.TryGetValue(geometry, out GeometryEntry? cached) ? cached.Bounds : geometry.Bounds;
            if (!Visible(bounds, StrokeExtent(pen) / scale)) return;
            GeometryEntry path = Path(geometry);
            if (Brush(brush, path.Bounds) is { } fill) context.FillGeometry(path.Geometry, fill);
            if (pen is { Thickness: > 0 } && Brush(pen.Brush, path.Bounds) is { } edge)
                context.DrawGeometry(path.Geometry, edge, (float)(pen.Thickness / scale), Stroke(pen));
        }
        finally { Pop(); }
    }

    private static double StrokeExtent(WM.Pen? pen) => pen is null ? 0 : pen.Thickness * Math.Max(1, pen.MiterLimit);

    // ---- Text ----

    private sealed class LayoutEntry(DW.IDWriteTextLayout layout, float baseline) : IUsed
    {
        internal readonly DW.IDWriteTextLayout Layout = layout;
        internal readonly float Baseline = baseline;
        public double Used { get; set; }
    }

    private readonly Dictionary<MapTextRun, LayoutEntry> layouts = [];
    private readonly Dictionary<(string, int, int, int, double), DW.IDWriteTextFormat> formats = [];

    // The text set by DirectWrite in the same font and size, its baseline where WPF puts it.
    public void DrawText(WM.FormattedText text, W.Point origin)
    {
        if (!MapText.Specs.TryGetValue(text, out _))
        {
            DrawGeometry(text.Foreground(), null, text.BuildGeometry(origin));
            return;
        }
        DrawText(MapDrawList.TextRun(text), origin);
    }

    internal void DrawText(MapTextRun text, W.Point origin)
    {
        if (!layouts.TryGetValue(text, out LayoutEntry? entry))
        {
            if (!formats.TryGetValue((text.Family, text.Weight, text.Style, text.Stretch, text.Size), out DW.IDWriteTextFormat? format))
            {
                format = write.CreateTextFormat(text.Family, (DW.FontWeight)text.Weight, (DW.FontStyle)text.Style,
                    (DW.FontStretch)text.Stretch, (float)text.Size);
                format.WordWrapping = DW.WordWrapping.NoWrap;
                formats[(text.Family, text.Weight, text.Style, text.Stretch, text.Size)] = format;
            }
            DW.IDWriteTextLayout layout = write.CreateTextLayout(text.Text, format, 10000, 10000);
            DW.LineMetrics[] lines = layout.LineMetrics;
            layouts[text] = entry = new LayoutEntry(layout, lines.Length > 0 ? lines[0].Baseline : 0);
        }
        entry.Used = now;
        if (Brush(text.Brush, new W.Rect(origin, new W.Size(text.Width, text.Height))) is not { } ink) return;
        context.DrawTextLayout(new Vector2((float)origin.X, (float)(origin.Y + text.Baseline - entry.Baseline)), entry.Layout, ink);
    }

    private sealed class SpriteEntry(ID2D1CommandList commands, bool complete) : IUsed
    {
        internal readonly ID2D1CommandList Commands = commands;
        internal readonly bool Complete = complete;
        public double Used { get; set; }
    }
    private readonly Dictionary<MapSprite, SpriteEntry> sprites = new(ReferenceEqualityComparer.Instance);
    internal int SpriteBuilds { get; private set; }
    internal int SpriteDraws { get; private set; }

    public void DrawSprite(MapSprite sprite, W.Point origin)
    {
        W.Rect area = sprite.Bounds; area.Offset(origin.X, origin.Y);
        if (!Visible(area)) return;
        if (!sprites.TryGetValue(sprite, out var entry) || !entry.Complete)
        {
            // Record vectors once. A command list keeps native antialiasing at the actual position/DPI.
            var list = spriteContext.CreateCommandList();
            int deferredAt = deferredUploads;
            ID2D1DeviceContext previous = context;
            Matrix3x2 previousTransform = transform;
            W.Rect previousViewport = viewport;
            try
            {
                // A portrait brush can generate mipmaps while these commands are being recorded.
                // Each BeginDraw/EndDraw pair must own a separate context and target.
                context = spriteContext; transform = Matrix3x2.Identity;
                viewport = sprite.Bounds;
                spriteContext.Target = list; spriteContext.Transform = Matrix3x2.Identity;
                spriteContext.SetDpi(dpi, dpi);
                spriteContext.AntialiasMode = AntialiasMode.PerPrimitive;
                spriteContext.TextAntialiasMode = TextAntialiasMode.Grayscale;
                spriteContext.BeginDraw();
                try { sprite.Drawing.Play(this); }
                finally { spriteContext.EndDraw().CheckError(); }
                list.Close().CheckError();
            }
            catch { list.Dispose(); throw; }
            finally
            {
                spriteContext.Target = null;
                context = previous; transform = previousTransform; viewport = previousViewport;
            }
            entry?.Commands.Dispose();
            if (entry is null && sprites.Count >= 1024)
            {
                MapSprite? oldest = null; double used = double.PositiveInfinity;
                foreach (var pair in sprites) if (pair.Value.Used < used) { used = pair.Value.Used; oldest = pair.Key; }
                if (oldest is not null) { sprites[oldest].Commands.Dispose(); sprites.Remove(oldest); }
            }
            // An upload postponed by this frame's budget must be retried, rather than cached as
            // a permanently missing portrait. Deferred requests another frame from MapSurface.
            sprites[sprite] = entry = new(list, deferredUploads == deferredAt);
            SpriteBuilds++;
        }
        entry.Used = now;
        context.DrawImage(entry.Commands, new Vector2((float)origin.X, (float)origin.Y));
        SpriteDraws++;
    }

    public void Dispose()
    {
        layerImages.Clear();
        foreach (BitmapEntry entry in bitmaps.Values) entry.Dispose();
        foreach (BitmapEntry entry in tints.Values) entry.Dispose();
        foreach (BitmapEntry entry in shadows.Values) entry.Dispose();
        foreach (SpriteEntry entry in sprites.Values) entry.Commands.Dispose();
        foreach (LabelEntry entry in labels.Values) entry.Bitmap.Dispose();
        foreach (GeometryEntry entry in geometries.Values) entry.Geometry.Dispose();
        foreach (LayoutEntry entry in layouts.Values) entry.Layout.Dispose();
        foreach (GradientEntry entry in gradients.Values) entry.Dispose();
        foreach (DW.IDWriteTextFormat format in formats.Values) format.Dispose();
        foreach (ID2D1SolidColorBrush brush in solids.Values) brush.Dispose();
        foreach (ID2D1StrokeStyle1 style in strokes.Values) style.Dispose();
        mipContext.Dispose();
        spriteContext.Dispose();
        write.Dispose();
    }
}

internal static class FormattedTextExtensions
{
    // The brush a FormattedText was made with is not public; drawn as a shape it is the map's label ink.
    internal static WM.Brush Foreground(this WM.FormattedText _) => WM.Brushes.White;
}

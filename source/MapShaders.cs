using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;
using W = System.Windows;

namespace San14Tenkazu;

// The map's close-up ground and its borders drawn by Direct3D 11 shaders straight into the frame, between
// Direct2D's layers (GpuMap.Direct3D). Every frame is drawn at the view's own scale, so nothing waits to be
// drawn and nothing changes as the zoom passes a tile level (the tiles drew these on the CPU, MapTiles):
//   - the ground: GroundDetail's recipe per pixel, the lit base map times each material layer's fine grain
//     and relief, with the trees standing on it (MapTrees), shadows first;
//   - the borders: MapView.RenderDetail's lines found per pixel from each hex's owner and edges: the faint
//     hex grid, county boundaries, the marks of seats, gates and ports, and each force's border.
// The tiles stay for a machine without the shaders, and for the painted map (its roads and symbols).
internal sealed class MapShaders : IDisposable
{
    // Where a pass draws: the frame's target and its size in device pixels.
    internal readonly record struct Target(ID3D11RenderTargetView View, int Width, int Height);

    // The view: device pixels per world unit, and where world point (0, 0) is, in device pixels.
    internal readonly record struct Camera(float Scale, float X, float Y);

    private readonly ID3D11Device device;
    private readonly ID3D11VertexShader areaShader, treeShader;
    private readonly ID3D11PixelShader groundShader, treePixels, borderShader;
    private readonly ID3D11Buffer frameConstants, treeConstants, lineConstants;
    private readonly ID3D11BlendState blend;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11SamplerState repeat, clear;
    private readonly ID3D11Texture2D hexTexture;
    private readonly ID3D11ShaderResourceView hexView;
    private readonly object sync = new();
    private bool disposed;

    // Something made on a worker is ready (raised on that worker): the map draws a frame.
    internal event Action? Ready;

    internal static MapShaders? TryCreate(ID3D11Device device, Action<IDisposable>? resourceAcquired = null)
    {
        try { return new MapShaders(device, resourceAcquired); }
        catch (Exception ex)
        {
            // Without the shaders the map's tiles draw the ground and the borders, as before.
            Debug.WriteLine("Map shaders unavailable: " + ex.Message);
            return null;
        }
    }

    private MapShaders(ID3D11Device d3d, Action<IDisposable>? resourceAcquired)
    {
        using var resources = new ResourceScope(resourceAcquired);
        device = resources.Own(d3d.QueryInterface<ID3D11Device>());
        var watch = Stopwatch.StartNew();
        areaShader = resources.Own(Shader("AreaVS", "vs_4_0", code => device.CreateVertexShader(code)));
        treeShader = resources.Own(Shader("TreeVS", "vs_4_0", code => device.CreateVertexShader(code)));
        groundShader = resources.Own(Shader("GroundPS", "ps_4_0", code => device.CreatePixelShader(code)));
        treePixels = resources.Own(Shader("TreePS", "ps_4_0", code => device.CreatePixelShader(code)));
        borderShader = resources.Own(Shader("BordersPS", "ps_4_0", code => device.CreatePixelShader(code)));
        Debug.WriteLine($"Map shaders made in {watch.ElapsedMilliseconds} ms");
        frameConstants = resources.Own(Constants(Marshal.SizeOf<FrameData>()));
        treeConstants = resources.Own(Constants(16 * 3));
        lineConstants = resources.Own(Constants(16 * (2 + 256)));
        blend = resources.Own(device.CreateBlendState(BlendDescription.AlphaBlend));      // premultiplied, as Direct2D draws
        rasterizer = resources.Own(device.CreateRasterizerState(RasterizerDescription.CullNone));
        repeat = resources.Own(device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 1, ComparisonFunc = ComparisonFunction.Never, MinLOD = 0, MaxLOD = MaxLayerMip
        }));
        clear = resources.Own(device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Border, AddressV = TextureAddressMode.Border, AddressW = TextureAddressMode.Border,
            BorderColor = new Color4(0, 0, 0, 0), MaxAnisotropy = 1, ComparisonFunc = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue
        }));
        hexTexture = resources.Own(device.CreateTexture2D(new Texture2DDescription(Format.R32_UInt, MapTerrain.Columns, MapTerrain.Rows, 1, 1)));
        hexView = resources.Own(device.CreateShaderResourceView(hexTexture));
        resources.Commit();
    }

    private ID3D11Buffer Constants(int bytes) => device.CreateBuffer((uint)bytes, BindFlags.ConstantBuffer);

    // ---- Frames ----------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameData
    {
        internal Vector4 ToClip;    // clip position = world * ToClip.xy + ToClip.zw
        internal Vector4 Area;      // the world rectangle drawn: left, top, right, bottom
        internal Vector4 View;      // device pixels per world unit, opacity
    }

    // Starts a pass: the target, the state every pass shares and the view (the pass sets its shaders).
    private void Begin(ID3D11DeviceContext dc, Target target, Camera camera, Vector4 area, float opacity)
    {
        dc.OMSetRenderTargets(target.View);
        dc.OMSetBlendState(blend);
        dc.RSSetViewport(0, 0, target.Width, target.Height, 0, 1);
        dc.RSSetState(rasterizer);
        dc.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        var frame = new FrameData
        {
            ToClip = new Vector4(camera.Scale * 2 / target.Width, -camera.Scale * 2 / target.Height, camera.X * 2 / target.Width - 1, 1 - camera.Y * 2 / target.Height),
            Area = area,
            View = new Vector4(camera.Scale, opacity, 0, 0)
        };
        dc.UpdateSubresource(in frame, frameConstants);
        dc.VSSetConstantBuffer(0, frameConstants);
        dc.PSSetConstantBuffer(0, frameConstants);
    }

    private static float Ramp(float value, float from, float to) => Math.Clamp((value - from) / (to - from), 0, 1);

    // ---- The ground --------------------------------------------------------------------------------

    // The close-up ground takes over from the base map where the tiles' did (from their level 2, 11.3 device
    // pixels per unit), fading in; the trees from where the tiles drew them (their level 3).
    private const float GroundFrom = 10f, GroundFull = 11.31f, TreesFrom = 20f, TreesFull = 22.63f;
    private const float MaxLayerMip = 8;            // the coarsest layer level GroundDetail.RenderTile takes

    private sealed class Ground(WMI.BitmapSource source) : IDisposable
    {
        internal readonly WMI.BitmapSource Source = source;
        internal ID3D11ShaderResourceView? BaseMap, MaterialMap, Albedo, Normals;
        internal ID3D11Buffer? Constants;
        internal Vector4 Area;                       // the base map's world rectangle (the shader reads its pixels from its corner)
        internal bool Usable => Constants is not null;

        public void Dispose()
        {
            foreach (IDisposable? part in new IDisposable?[] { BaseMap, MaterialMap, Albedo, Normals, Constants }) part?.Dispose();
        }
    }

    private Ground? ground, groundMade;
    private WMI.BitmapSource? groundAsked;

    // How much of the close-up ground shows at this scale (0 while the base map alone shows).
    internal static float GroundOpacity(float scale) => Ramp(scale, GroundFrom, GroundFull);

    // Whether the ground over this base map, its top-left corner at (left, top) in the world, can be drawn; starts
    // making it on a worker when it cannot yet (the game's ground data loads in a second or two, and its textures
    // are about 100 MB).
    internal bool GroundReady(WMI.BitmapSource baseImage, float pixelsPerUnit, float left = 0, float top = 0)
    {
        lock (sync)
        {
            if (groundMade is { } made && ReferenceEquals(made.Source, baseImage))
            {
                ground?.Dispose();
                ground = made;
                groundMade = null;
            }
        }
        if (ground is { } current && ReferenceEquals(current.Source, baseImage)) return current.Usable;
        if (ReferenceEquals(groundAsked, baseImage)) return false;
        groundAsked = baseImage;
        ID3D11Device worker = device.QueryInterface<ID3D11Device>();   // kept alive while the worker uses it
        Task.Run(() =>
        {
            Ground made = new(baseImage);
            try { MakeGround(worker, made, baseImage, pixelsPerUnit, new Vector2(left, top)); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debug.WriteLine("Ground textures not made: " + ex.Message);
                made.Dispose();
                made = new Ground(baseImage);
            }
            finally { worker.Dispose(); }
            lock (sync)
            {
                if (disposed)
                {
                    made.Dispose();
                    return;
                }
                groundMade?.Dispose();
                groundMade = made;
            }
            Ready?.Invoke();
        });
        return false;
    }

    private static void MakeGround(ID3D11Device device, Ground ground, WMI.BitmapSource baseImage, float pixelsPerUnit, Vector2 corner)
    {
        using var data = GroundGpuData.Load();
        if (data is null) return;   // the game's files are missing: no close-up ground
        int width = baseImage.PixelWidth, height = baseImage.PixelHeight;
        int cells = GroundDetail.MaterialMapSize;
        ground.MaterialMap = View(device, Format.R8G8B8A8_UInt, cells, cells, 1, 1,
            [new SubresourceData(data.MaterialMap.Pointer, (uint)cells * 4, (uint)data.MaterialMap.Length)]);

        // The layers as the game stores them: each mip level of every layer in turn (GroundDetail.Decode), while
        // a texture array's subresources go layer by layer.
        int size = data.LayerSize, layers = data.LayerCount, mips = data.MipCount;
        var parts = new (int Offset, int Pitch, int Bytes)[layers * mips];
        long offset = 0;
        for (int mip = 0, side = size; mip < mips; mip++, side = Math.Max(1, side / 2))
        {
            long bytes = GroundDetail.LevelBytes(side);
            for (int layer = 0; layer < layers; layer++)
                parts[layer * mips + mip] = ((int)(offset + bytes * layer), Math.Max(1, (side + 3) / 4) * 16, (int)bytes);
            offset += bytes * layers;
        }
        ground.Albedo = View(device, Format.BC3_UNorm, size, size, layers, mips,
            parts.Select(p => new SubresourceData(data.Albedo.Pointer + p.Offset, (uint)p.Pitch, (uint)p.Bytes)).ToArray());
        if (data.Normals is { } normals)
            ground.Normals = View(device, Format.BC3_UNorm, size, size, layers, mips,
                parts.Select(p => new SubresourceData(normals.Pointer + p.Offset, (uint)p.Pitch, (uint)p.Bytes)).ToArray());

        var constants = new Vector4[GroundDetail.Materials.Length + 2];
        for (int m = 0; m < GroundDetail.Materials.Length; m++)
        {
            (int layer, float repeat) = GroundDetail.Materials[m];
            // The level of detail at which a texel spans GrainUnits: what a layer's grain is measured against.
            constants[m] = new Vector4(layer, 1 / repeat, MathF.Log2(GroundDetail.GrainUnits * size / repeat), 0);
        }
        constants[^2] = new Vector4(pixelsPerUnit, width, height, data.Normals is null ? 0 : 1);
        constants[^1] = new Vector4(GameTerrain.OriginX, GameTerrain.OriginZ, GameTerrain.UnitsPerHex, size);
        ground.Constants = device.CreateBuffer(constants, BindFlags.ConstantBuffer);
        ground.Area = new Vector4(corner.X, corner.Y, corner.X + width / pixelsPerUnit, corner.Y + height / pixelsPerUnit);
    }

    // Only the render thread touches Direct2D's cache. Prepare as soon as the worker's ground is
    // adopted, including at distant zooms, so the first close-up still has its resources ready.
    internal void PrepareGround(D2DCanvas canvas)
    {
        if (ground is not { Usable: true, BaseMap: null } made) return;
        using (var shared = canvas.OpaqueTexture(made.Source))
        {
            if (shared is not null)
            {
                made.BaseMap = device.CreateShaderResourceView(shared);
                return;
            }
        }
        int width = made.Source.PixelWidth, height = made.Source.PixelHeight;
        using var pixels = NativePixelBuffer.Copy(new WMI.FormatConvertedBitmap(made.Source, WM.PixelFormats.Bgra32, null, 0), width * 4);
        using var texture = device.CreateTexture2D(
            new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Immutable),
            [new SubresourceData(pixels.Pointer, (uint)width * 4, (uint)pixels.Length)]);
        made.BaseMap = device.CreateShaderResourceView(texture);
    }

    // A texture of these parts (each subresource: its array, offset, row pitch and size) and its view.
    private static ID3D11ShaderResourceView View(ID3D11Device device, Format format, int width, int height, int arraySize, int mips, SubresourceData[] initial)
    {
        using ID3D11Texture2D texture = device.CreateTexture2D(
            new Texture2DDescription(format, (uint)width, (uint)height, (uint)arraySize, (uint)mips, BindFlags.ShaderResource, ResourceUsage.Immutable), initial);
        return device.CreateShaderResourceView(texture);
    }

    private static ID3D11ShaderResourceView View(ID3D11Device device, Format format, int width, int height, int arraySize, int mips, (byte[] Data, int Offset, int Pitch, int Bytes)[] parts)
    {
        var pins = parts.Select(p => p.Data).Distinct().Select(d => GCHandle.Alloc(d, GCHandleType.Pinned)).ToList();
        try
        {
            var initial = parts.Select(p => new SubresourceData(Marshal.UnsafeAddrOfPinnedArrayElement(p.Data, p.Offset), (uint)p.Pitch, (uint)p.Bytes)).ToArray();
            using ID3D11Texture2D texture = device.CreateTexture2D(
                new Texture2DDescription(format, (uint)width, (uint)height, (uint)arraySize, (uint)mips, BindFlags.ShaderResource, ResourceUsage.Immutable), initial);
            return device.CreateShaderResourceView(texture);
        }
        finally
        {
            foreach (GCHandle pin in pins) pin.Free();
        }
    }

    // The ground over the base map, then the trees on it (as far as the zoom shows them).
    internal void DrawGround(ID3D11DeviceContext dc, Target target, Camera camera)
    {
        if (ground is not { Usable: true } made) return;
        Begin(dc, target, camera, made.Area, GroundOpacity(camera.Scale));
        dc.VSSetShader(areaShader);
        dc.PSSetShader(groundShader);
        dc.PSSetConstantBuffer(1, made.Constants);
        dc.PSSetShaderResources(0, [made.BaseMap!, made.MaterialMap!, made.Albedo!, made.Normals ?? made.Albedo!]);
        dc.PSSetSampler(0, repeat);
        dc.Draw(4, 0);
        DrawTrees(dc, target, camera, Ramp(camera.Scale, TreesFrom, TreesFull));
    }

    // ---- Trees -------------------------------------------------------------------------------------

    private sealed class Woods(MapTrees.Sprite[] sprites) : IDisposable
    {
        internal readonly MapTrees.Sprite[] Sprites = sprites;
        internal ID3D11Buffer? Trees;
        internal ID3D11ShaderResourceView? TreeView;
        internal ID3D11ShaderResourceView?[] Pictures = new ID3D11ShaderResourceView?[2];
        internal int Count;
        internal (W.Rect Bounds, int Start, int Count)[] Batches = [];

        public void Dispose()
        {
            TreeView?.Dispose();
            Trees?.Dispose();
            foreach (ID3D11ShaderResourceView? picture in Pictures) picture?.Dispose();
        }
    }

    private Woods? woods, woodsMade;
    private MapTrees.Sprite[]? woodsAsked;
    private readonly List<(int Start, int Count)> visibleTrees = [];

    private void DrawTrees(ID3D11DeviceContext dc, Target target, Camera camera, float opacity)
    {
        lock (sync)
        {
            if (woodsMade is not null)
            {
                woods?.Dispose();
                woods = woodsMade;
                woodsMade = null;
            }
        }
        if (MapTrees.Sprites is not { } sprites) return;
        if (!ReferenceEquals(woodsAsked, sprites))
        {
            woodsAsked = sprites;
            ID3D11Device worker = device.QueryInterface<ID3D11Device>();
            Task.Run(() =>
            {
                Woods? made = null;
                try { made = MakeWoods(worker, sprites); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine("Trees not made: " + ex.Message); }
                finally { worker.Dispose(); }
                if (made is null) return;
                lock (sync)
                {
                    if (disposed)
                    {
                        made.Dispose();
                        return;
                    }
                    woodsMade?.Dispose();
                    woodsMade = made;
                }
                Ready?.Invoke();
            });
        }
        if (opacity <= 0 || woods is not { Count: > 0 } made || !ReferenceEquals(made.Sprites, sprites)) return;
        var constants = new Vector4[3];
        for (int kind = 0; kind < 2; kind++)
        {
            MapTrees.Sprite sprite = sprites[Math.Min(kind, sprites.Length - 1)];
            constants[kind] = new Vector4(sprite.X, sprite.Y, sprite.Width, sprite.Height);
        }
        dc.VSSetShader(treeShader);
        dc.VSSetShaderResource(0, made.TreeView);
        dc.VSSetConstantBuffer(1, treeConstants);
        dc.PSSetShader(treePixels);
        dc.PSSetConstantBuffer(1, treeConstants);
        dc.PSSetShaderResources(1, [made.Pictures[0]!, made.Pictures[1]!]);
        dc.PSSetSampler(1, clear);
        var visible = new W.Rect(-camera.X / camera.Scale, -camera.Y / camera.Scale, target.Width / camera.Scale, target.Height / camera.Scale);
        visible.Inflate(1 / camera.Scale, 1 / camera.Scale);
        visibleTrees.Clear();
        foreach (var batch in made.Batches)
            if (visible.IntersectsWith(batch.Bounds))
            {
                if (visibleTrees.Count > 0 && visibleTrees[^1].Start + visibleTrees[^1].Count == batch.Start)
                    visibleTrees[^1] = (visibleTrees[^1].Start, visibleTrees[^1].Count + batch.Count);
                else visibleTrees.Add((batch.Start, batch.Count));
            }
        // All the shadows, then all the trees, northern ones first (MapTrees.Draw).
        for (int pass = 0; pass < 2; pass++)
        {
            // Adjacent visible batches share one draw, including the full map when zoomed out.
            foreach (var span in visibleTrees)
            {
                // SV_InstanceID starts at zero for each draw; pass the buffer offset explicitly.
                constants[2] = new Vector4(pass == 0 ? 1 : 0, opacity, span.Start, 0);
                dc.UpdateSubresource(constants, treeConstants);
                dc.DrawInstanced(4, (uint)span.Count, 0, 0);
            }
        }
    }

    private static Woods MakeWoods(ID3D11Device device, MapTrees.Sprite[] sprites)
    {
        var woods = new Woods(sprites);
        try
        {
            List<MapTrees.Tree> trees = MapTrees.Trees(0, MapTerrain.Columns - 1, 0, MapTerrain.Rows - 1);
            // Each tree as x, y, scale and tone, the tone negative for the second kind.
            var data = new Vector4[Math.Max(1, trees.Count)];
            for (int i = 0; i < trees.Count; i++)
                data[i] = new Vector4(trees[i].X, trees[i].Y, trees[i].Scale, trees[i].Kind == 0 ? trees[i].Tone : -trees[i].Tone);
            var batches = new List<(W.Rect Bounds, int Start, int Count)>();
            // Contiguous batches preserve the north-to-south blend order. Bounds include both crowns and shadows.
            for (int start = 0; start < trees.Count; start += 256)
            {
                int count = Math.Min(256, trees.Count - start);
                W.Rect bounds = W.Rect.Empty;
                for (int i = start; i < start + count; i++)
                {
                    MapTrees.Tree tree = trees[i];
                    MapTrees.Sprite sprite = sprites[Math.Min(tree.Kind, sprites.Length - 1)];
                    var area = new W.Rect(tree.X + sprite.X * tree.Scale, tree.Y + sprite.Y * tree.Scale, sprite.Width * tree.Scale, sprite.Height * tree.Scale);
                    bounds.Union(area);
                    area.Offset(MapTrees.ShadowX * tree.Scale, MapTrees.ShadowY * tree.Scale);
                    bounds.Union(area);
                }
                batches.Add((bounds, start, count));
            }
            woods.Batches = batches.ToArray();
            woods.Trees = device.CreateBuffer(data, BindFlags.ShaderResource, ResourceUsage.Immutable);
            woods.TreeView = device.CreateShaderResourceView(woods.Trees,
                new ShaderResourceViewDescription(woods.Trees, Format.R32G32B32A32_Float, 0, (uint)data.Length));
            for (int kind = 0; kind < 2; kind++)
            {
                MapTrees.Sprite sprite = sprites[Math.Min(kind, sprites.Length - 1)];
                MapTrees.Level top = sprite.Levels[0];
                var parts = sprite.Levels.Select(level =>
                {
                    var bytes = new byte[level.Pixels.Length * 4];
                    Buffer.BlockCopy(level.Pixels, 0, bytes, 0, bytes.Length);
                    return (bytes, 0, level.Width * 4, bytes.Length);
                }).ToArray();
                woods.Pictures[kind] = View(device, Format.B8G8R8A8_UNorm, top.Width, top.Height, 1, parts.Length, parts);
            }
            woods.Count = trees.Count;
            return woods;
        }
        catch
        {
            woods.Dispose();
            throw;
        }
    }

    // ---- Borders -------------------------------------------------------------------------------------

    // Each hex as the border shader reads it: its owner (0 for none and for the sea), the edges a force's
    // border runs along, the edges between counties, its mark and whether it is land.
    private const int OwnerBits = 0, BorderBits = 8, CountyBits = 14, MarkBits = 20, LandBit = 22;

    private byte[]? hexOwners;
    private ushort[]? hexCounties;
    private bool hexesMade;
    private readonly Vector4[] lines = new Vector4[2 + 256];      // the shader's Lines: widths, strengths, colours
    private bool linesMade;

    private void UpdateHexes(ID3D11DeviceContext dc, byte[]? owners, ushort[]? counties)
    {
        if (hexesMade && ReferenceEquals(owners, hexOwners) && (ReferenceEquals(counties, hexCounties) ||
            counties is not null && hexCounties is not null && counties.AsSpan().SequenceEqual(hexCounties))) return;
        hexesMade = true;
        hexOwners = owners;
        hexCounties = counties?.ToArray();
        int count = MapTerrain.Columns * MapTerrain.Rows;
        byte[] terrain = MapTerrain.Terrain;
        int Owner(int tile) => owners is null || tile >= owners.Length || MapTerrain.IsSea(terrain[tile]) ? 0 : owners[tile];
        bool InCounty(int tile) => counties is not null && tile < counties.Length && counties[tile] != 0 && !MapTerrain.IsWater(terrain[tile]);
        var info = new uint[count];
        for (int tile = 0; tile < count; tile++)
        {
            int column = tile % MapTerrain.Columns, row = tile / MapTerrain.Columns, owner = Owner(tile);
            uint borders = 0, edges = 0;
            for (int direction = 0; direction < 6; direction++)
            {
                int neighbour = MapTerrain.Neighbour(column, row, direction);
                if (owner != 0 && (neighbour < 0 || Owner(neighbour) != owner)) borders |= 1u << direction;
                if (InCounty(tile) && neighbour >= 0 && InCounty(neighbour) && counties![neighbour] != counties[tile]) edges |= 1u << direction;
            }
            uint mark = terrain[tile] switch { MapTerrain.Seat => 1u, MapTerrain.Gate => 2u, MapTerrain.Port => 3u, _ => 0u };
            info[tile] = (uint)owner << OwnerBits | borders << BorderBits | edges << CountyBits | mark << MarkBits |
                (MapTerrain.IsWater(terrain[tile]) ? 0u : 1u << LandBit);
        }
        dc.UpdateSubresource(info, hexTexture, 0, (uint)(MapTerrain.Columns * 4), 0);
    }

    // The borders, county boundaries, hex grid and marks (MapView.RenderDetail) at this scale.
    internal void DrawBorders(ID3D11DeviceContext dc, Target target, Camera camera, byte[]? owners, ushort[]? counties, System.Drawing.Color[]? palette = null)
    {
        UpdateHexes(dc, owners, counties);
        float s = camera.Scale;
        // Widths in device pixels, as the tiles drew them (MapView.DrawBorders, DrawCountyBorders, DrawSymbols)
        // but following the zoom smoothly instead of a tile level's scale.
        float border = MathF.Min(s * 0.4f, 3.6f), county = Math.Clamp(s * 0.09f, 1f, 3f), grid = Math.Clamp(s * 0.03f, 1f, 1.5f);
        float mark = MathF.Min(s * 0.55f, 9f), markLine = MathF.Max(1f, s * 0.1f);
        float reach = 1 + (MathF.Max(border * 3.4f, county * 2.2f) / 2 + 1) / s;
        bool changed = !linesMade;
        for (int force = 1; force < 256; force++)
        {
            System.Drawing.Color c = palette is null ? MapView.ForceColor(force) : palette[force];
            var colour = new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1);
            if (lines[2 + force] == colour) continue;
            lines[2 + force] = colour;
            changed = true;
        }
        // County boundaries show from where the tiles drew them (their level 1), the grid with the ground (level 2).
        var widths = new Vector4(border, county, grid, mark);
        var shows = new Vector4(Ramp(s, 5f, 5.66f), 28f / 255 * Ramp(s, GroundFrom, GroundFull), markLine, reach);
        if (changed || lines[0] != widths || lines[1] != shows)
        {
            lines[0] = widths;
            lines[1] = shows;
            dc.UpdateSubresource(lines, lineConstants);
            linesMade = true;
        }
        Begin(dc, target, camera, new Vector4(-2, -2, MapTerrain.WorldWidth + 2, MapTerrain.WorldHeight + 2), 1);
        dc.VSSetShader(areaShader);
        dc.PSSetShader(borderShader);
        dc.PSSetConstantBuffer(1, lineConstants);
        dc.PSSetShaderResource(0, hexView);
        dc.Draw(4, 0);
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            groundMade?.Dispose();
            woodsMade?.Dispose();
        }
        ground?.Dispose();
        woods?.Dispose();
        foreach (IDisposable part in new IDisposable[] { areaShader, treeShader, groundShader, treePixels, borderShader, frameConstants, treeConstants,
                     lineConstants, blend, rasterizer, repeat, clear, hexView, hexTexture, device })
            part.Dispose();
    }

    // ---- Shaders -----------------------------------------------------------------------------------------

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte[] source, nint sourceSize, string? sourceName, IntPtr defines, IntPtr include,
        string entryPoint, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    private const uint OptimizationLevel3 = 1 << 15;

    // Compiling takes most of a second, so the compiled shaders are kept with the editor's other caches, by the
    // source they come from: a new source compiles anew (and the files of the old one go).
    private static T Shader<T>(string entryPoint, string target, Func<byte[], T> create)
        => CachedShader(Source, entryPoint, target, create, Path.Combine(Program.DataFolder, "cache"));

    internal static T CachedShader<T>(string source, string entryPoint, string target, Func<byte[], T> create, string folder)
    {
        string stamp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
        string path = Path.Combine(folder, $"shader-{entryPoint}-{stamp}.bin");
        try
        {
            if (File.Exists(path)) return create(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SharpGen.Runtime.SharpGenException) { /* compiled again below */ }
        byte[] code = CompileSource(source, entryPoint, target);
        T shader = create(code);
        try
        {
            Directory.CreateDirectory(folder);
            foreach (string old in Directory.EnumerateFiles(folder, $"shader-{entryPoint}-*.bin")) File.Delete(old);
            File.WriteAllBytes(path + ".part", code);
            File.Move(path + ".part", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* only an optimisation */ }
        return shader;
    }

    // The shaders are compiled from the source below by the compiler Windows ships (d3dcompiler_47.dll).
    internal static byte[] CompileSource(string source, string entryPoint, string target)
    {
        byte[] text = Encoding.ASCII.GetBytes(source);
        int result = D3DCompile(text, text.Length, "MapShaders", IntPtr.Zero, IntPtr.Zero, entryPoint, target, OptimizationLevel3, 0, out IntPtr code, out IntPtr errors);
        using Blob? program = code == IntPtr.Zero ? null : new Blob(code);
        using Blob? messages = errors == IntPtr.Zero ? null : new Blob(errors);
        if (result < 0 || program is null)
            throw new InvalidOperationException($"{entryPoint}: " + (messages is null ? $"0x{result:X8}" : Encoding.ASCII.GetString(messages.AsBytes()).TrimEnd('\0')));
        return program.AsBytes();
    }

    private static string F(float value) => value.ToString("0.0######", CultureInfo.InvariantCulture);

    private static readonly string Source = $$"""
        cbuffer Frame : register(b0)
        {
            float4 ToClip;      // clip position = world * ToClip.xy + ToClip.zw
            float4 Area;        // the world rectangle drawn: left, top, right, bottom
            float4 View;        // x: device pixels per world unit, y: opacity
        };

        struct Pixel
        {
            float4 Position : SV_Position;
            float2 World : WORLD;
        };

        // The rectangle Area, as a strip of two triangles.
        Pixel AreaVS(uint id : SV_VertexID)
        {
            float2 corner = float2(id & 1, id >> 1);
            Pixel o;
            o.World = lerp(Area.xy, Area.zw, corner);
            o.Position = float4(o.World * ToClip.xy + ToClip.zw, 0, 1);
            return o;
        }

        // ---- The ground (GroundDetail.RenderTile) ----

        cbuffer Ground : register(b1)
        {
            float4 Materials[{{GroundDetail.Materials.Length}}];   // texture layer, 1 / game units per repeat, level of detail of the grain's surroundings
            float4 BaseInfo;            // base map: pixels per world unit, width, height; w: 1 when the layers have normal maps
            float4 Game;                // game units = Game.xy + world * Game.z; w: a layer's size in texels
        };
        Texture2D<float4> BaseMap : register(t0);
        Texture2D<uint4> MaterialMap : register(t1);
        Texture2DArray<float4> Albedo : register(t2);
        Texture2DArray<float4> Normals : register(t3);
        SamplerState Repeat : register(s0);

        static const float3 Light = float3({{F(GroundDetail.LightX)}}, {{F(GroundDetail.LightY)}}, {{F(GroundDetail.LightZ)}});

        float4 CatmullRom(float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5 * float4(-t3 + 2 * t2 - t, 3 * t3 - 5 * t2 + 2, -3 * t3 + 4 * t2 + t, t3 - t2);
        }

        float4 GroundPS(Pixel i) : SV_Target
        {
            // The lit base map, Catmull-Rom: magnified many times, bilinear sampling shows its texels as soft diamonds.
            int2 size = (int2)BaseInfo.yz;
            float2 t = clamp((i.World - Area.xy) * BaseInfo.x - 0.5, 0, BaseInfo.yz - 1.001);
            int2 t0 = (int2)t;
            float2 f = t - t0;
            float4 wx = CatmullRom(f.x), wy = CatmullRom(f.y);
            float3 colour = 0;
            [unroll] for (int j = 0; j < 4; j++)
                [unroll] for (int k = 0; k < 4; k++)
                    colour += BaseMap.Load(int3(clamp(t0 + int2(k - 1, j - 1), 0, size - 1), 0)).rgb * (wx[k] * wy[j]);

            // Material weights from the four surrounding cells of the material map.
            float2 game = Game.xy + i.World * Game.z;
            float2 cell = game / {{F(GroundDetail.UnitsPerCell)}};
            int2 c = (int2)floor(cell);
            float2 tc = cell - c;
            float weights[{{GroundDetail.Materials.Length}}];
            [unroll] for (int m = 0; m < {{GroundDetail.Materials.Length}}; m++) weights[m] = 0;
            [unroll] for (int cj = 0; cj < 2; cj++)
                [unroll] for (int ci = 0; ci < 2; ci++)
                {
                    uint4 data = MaterialMap.Load(int3(clamp(c + int2(ci, cj), 0, {{GroundDetail.MaterialMapSize - 1}}), 0));
                    float corner = (ci == 0 ? 1 - tc.x : tc.x) * (cj == 0 ? 1 - tc.y : tc.y);
                    uint main = data.z, second = data.y;
                    float blend = second < {{GroundDetail.Materials.Length}} ? data.x / 255.0 : 0;
                    if (main < {{GroundDetail.Materials.Length}}) weights[main] += corner * (1 - blend);
                    if (blend > 0) weights[second] += corner * blend;
                }

            // Each layer's grain against its own surroundings, and its relief lit from the north-west. The level of
            // detail leans half a level finer, as the tiles took the finer level of the two.
            float2 gx = ddx(game) * 0.7071, gy = ddy(game) * 0.7071;
            float3 grain = 0;
            float total = 0, light = 0, lit = 0;
            [loop] for (int n = 0; n < {{GroundDetail.Materials.Length}}; n++)
            {
                float weight = weights[n];
                if (weight < 0.02) continue;
                float4 material = Materials[n];
                float3 at = float3(game * material.y, material.x);
                float2 dx = gx * material.y, dy = gy * material.y;
                float3 texel = Albedo.SampleGrad(Repeat, at, dx, dy).rgb;
                float shown = log2(max(max(length(dx), length(dy)) * Game.w, 1));
                float3 around = Albedo.SampleLevel(Repeat, at, max(material.z, shown + 1.585)).rgb;
                grain += weight * min(texel / max(around, 4.0 / 255), 255.0 / 128);
                total += weight;
                if (BaseInfo.w > 0)
                {
                    float3 normal = Normals.SampleGrad(Repeat, at, dx, dy).rgb * 2 - 1;
                    float facing = dot(normal, Light) / max(length(normal), 0.0001) / Light.z;
                    light += weight * clamp(facing, 0, 2.55);
                    lit += weight;
                }
            }
            if (total > 0)
            {
                float relief = lit > 0 ? clamp(1 + {{F(GroundDetail.ReliefStrength)}} * (light / lit - 1), 0.45, 1.6) : 1;
                colour *= clamp(grain / total, 0.3, 2.2) * relief;
            }
            return float4(saturate(colour), 1) * View.y;
        }

        // ---- Trees (MapTrees.Draw) ----

        cbuffer Woods : register(b1)
        {
            float4 Sprites[2];      // each kind's picture around the point a tree stands on, at scale 1: left, top, width, height
            float4 Shading;         // x: 1 for the shadows, y: opacity, z: first tree in this batch
        };
        Buffer<float4> Trees : register(t0);       // x, y, scale, tone (negative for the second kind)
        Texture2D<float4> Broadleaf : register(t1);
        Texture2D<float4> Palm : register(t2);
        SamplerState Clear : register(s1);

        struct TreePixel
        {
            float4 Position : SV_Position;
            float2 Uv : UV;
            nointerpolation float2 Look : LOOK;     // kind, tone
        };

        TreePixel TreeVS(uint id : SV_VertexID, uint instance : SV_InstanceID)
        {
            float4 tree = Trees.Load(instance + (uint)Shading.z);
            uint kind = tree.w < 0 ? 1 : 0;
            float4 sprite = Sprites[kind];
            float2 corner = float2(id & 1, id >> 1);
            float2 at = tree.xy + (Shading.x > 0 ? float2({{F(MapTrees.ShadowX)}}, {{F(MapTrees.ShadowY)}}) * tree.z : 0);
            float2 world = at + (sprite.xy + corner * sprite.zw) * tree.z;
            TreePixel o;
            o.Position = float4(world * ToClip.xy + ToClip.zw, 0, 1);
            o.Uv = corner;
            o.Look = float2(kind, abs(tree.w));
            return o;
        }

        float4 TreePS(TreePixel i) : SV_Target
        {
            float4 broadleaf = Broadleaf.Sample(Clear, i.Uv), palm = Palm.Sample(Clear, i.Uv);
            float4 c = i.Look.x > 0.5 ? palm : broadleaf;
            if (Shading.x > 0)
            {
                float dark = c.a * {{F(MapTrees.ShadowStrength)}};
                return float4(float3({{MapTrees.ShadowColour.R}}, {{MapTrees.ShadowColour.G}}, {{MapTrees.ShadowColour.B}}) / 255 * dark, dark) * Shading.y;
            }
            // The renderer's foliage is greyish: a little more colour, and the crown's own tone.
            float grey = dot(c.rgb, float3(0.3, 0.59, 0.11));
            return float4((grey + (c.rgb - grey) * {{F(MapTrees.Saturation)}}) * i.Look.y, c.a) * Shading.y;
        }

        // ---- Borders (MapView.RenderDetail) ----

        cbuffer Lines : register(b1)
        {
            float4 Widths;          // device pixels: a force's border line, a county boundary, the grid, a mark
            float4 Shows;           // x: county boundaries' strength, y: the grid's, z: a mark's outline width, w: how far from a hex's centre its lines reach (world units)
            float4 Palette[256];    // each force's colour
        };
        Texture2D<uint> Hexes : register(t0);       // MapShaders.UpdateHexes

        static const float Root3 = 1.7320508;
        static const float2 Corners[6] = { float2(1, 0), float2(0.5, Root3 / 2), float2(-0.5, Root3 / 2), float2(-1, 0), float2(-0.5, -Root3 / 2), float2(0.5, -Root3 / 2) };
        // Neighbours N, NE, SE, S, SW, NW (MapTerrain.Neighbour): even columns sit half a hex lower.
        static const int2 EvenSteps[6] = { int2(0, -1), int2(1, 0), int2(1, 1), int2(0, 1), int2(-1, 1), int2(-1, 0) };
        static const int2 OddSteps[6] = { int2(0, -1), int2(1, -1), int2(1, 0), int2(0, 1), int2(-1, 0), int2(-1, -1) };

        float2 Centre(int2 hex) { return float2(1.5 * hex.x + 1, Root3 * (hex.y + ((hex.x & 1) == 0 ? 1 : 0.5))); }
        int2 Step(int2 hex, int direction) { return hex + ((hex.x & 1) == 0 ? EvenSteps[direction] : OddSteps[direction]); }
        uint Info(int2 hex) { return all(hex >= 0) && all(hex < int2({{MapTerrain.Columns}}, {{MapTerrain.Rows}})) ? Hexes.Load(int3(hex, 0)) : 0; }

        // The hex a point is in: the nearest centre, in one of the two columns around it.
        int2 HexAt(float2 p)
        {
            int column = (int)floor((p.x - 1) / 1.5);
            int2 best = int2(column, 0);
            float nearest = 1e9;
            [unroll] for (int k = 0; k < 2; k++)
            {
                int c = column + k;
                int2 hex = int2(c, (int)round(p.y / Root3 - ((c & 1) == 0 ? 1 : 0.5)));
                float2 d = p - Centre(hex);
                if (dot(d, d) < nearest)
                {
                    nearest = dot(d, d);
                    best = hex;
                }
            }
            return best;
        }

        float Segment(float2 p, float2 a, float2 b)
        {
            float2 ab = b - a, ap = p - a;
            return length(ap - ab * saturate(dot(ap, ab) / dot(ab, ab)));
        }

        // How much of a pixel a line this many pixels wide covers, d pixels from its middle (round ends, as GDI+'s pens).
        float Line(float d, float width) { return clamp(width * 0.5 + 0.5 - d, 0, min(width, 1)); }

        float4 Over(float4 under, float3 colour, float alpha) { return float4(colour * alpha, alpha) + under * (1 - alpha); }

        float4 BordersPS(Pixel i) : SV_Target
        {
            float2 p = i.World;
            float s = View.x;
            int2 home = HexAt(p);
            uint homeInfo = Info(home);
            float2 q = p - Centre(home);
            float4 colour = 0;

            // The faint hex grid: every land hex draws its outline, so an edge between two of them is drawn twice.
            if (Shows.y > 0)
            {
                float3 across = float3(dot(q, float2(Root3 / 2, 0.5)), q.y, dot(q, float2(-Root3 / 2, 0.5)));
                float3 apart = abs(across);
                float most;
                int direction;
                if (apart.x >= apart.y && apart.x >= apart.z) { most = apart.x; direction = across.x > 0 ? 2 : 5; }
                else if (apart.y >= apart.z) { most = apart.y; direction = across.y > 0 ? 3 : 0; }
                else { most = apart.z; direction = across.z > 0 ? 4 : 1; }
                uint outlines = ((homeInfo >> {{LandBit}}) & 1) + ((Info(Step(home, direction)) >> {{LandBit}}) & 1);
                float stroke = Line((Root3 / 2 - most) * s, Widths.z) * Shows.y;
                colour = Over(colour, 1, outlines > 1 ? stroke * (2 - stroke) : stroke * outlines);
            }

            // The lines of this hex and its neighbours that reach this point: county boundaries on the hex edges, and
            // each force's border just inside its hexes (the nearest per force, for up to four forces).
            float county = 1e6;
            uint4 forces = 0;
            float4 closest = 1e6;
            [loop] for (int k = 0; k < 7; k++)
            {
                int2 hex = home;
                uint info = homeInfo;
                if (k > 0)
                {
                    hex = Step(home, k - 1);
                    info = Info(hex);
                }
                uint borders = (info >> {{BorderBits}}) & 63, counties = (info >> {{CountyBits}}) & 63;
                if ((borders | counties) == 0) continue;
                float2 r = p - Centre(hex);
                if (dot(r, r) > Shows.w * Shows.w) continue;
                float border = 1e6;
                [unroll] for (int d = 0; d < 6; d++)
                {
                    float2 a = Corners[(d + 4) % 6], b = Corners[(d + 5) % 6];
                    if ((counties >> d) & 1) county = min(county, Segment(r, a, b));
                    if ((borders >> d) & 1) border = min(border, Segment(r, a * 0.92, b * 0.92));
                }
                uint force = (info >> {{OwnerBits}}) & 255;
                bool placed = border >= 1e5;
                [unroll] for (int slot = 0; slot < 4; slot++)
                    if (!placed && (forces[slot] == force || forces[slot] == 0))
                    {
                        forces[slot] = force;
                        closest[slot] = min(closest[slot], border);
                        placed = true;
                    }
            }

            // County boundaries: a soft shade under a bright line.
            if (county < 1e5)
            {
                float d = county * s;
                colour = Over(colour, float3(40, 30, 20) / 255, Line(d, Widths.y * 2.2) * (42.0 / 255) * Shows.x);
                colour = Over(colour, float3(250, 248, 240) / 255, Line(d, Widths.y) * (140.0 / 255) * Shows.x);
            }

            // Seats, gates and ports: a small mark on the hex, round or square, with a dark outline.
            uint mark = (homeInfo >> {{MarkBits}}) & 3;
            if (mark != 0)
            {
                float2 m = q * s;
                float radius = Widths.w * 0.5;
                float edge = mark == 2 ? max(abs(m.x), abs(m.y)) - radius : length(m) - radius;
                float4 fill = mark == 1 ? float4(250, 244, 228, 235) : mark == 2 ? float4(118, 98, 76, 215) : float4(70, 120, 170, 235);
                colour = Over(colour, fill.rgb / 255, saturate(0.5 - edge) * fill.a / 255);
                colour = Over(colour, float3(70, 52, 36) / 255, Line(abs(edge), Shows.z) * (220.0 / 255));
            }

            // Each force's border, forces in the order of their numbers: a soft wide glow in the force's colour, a dark
            // edge, and the line (translucent, as the game's: the territory colour and the ground show through).
            [unroll] for (int sweep = 0; sweep < 3; sweep++)
                [unroll] for (int e = 0; e < 3 - sweep; e++)
                    if (forces[e + 1] != 0 && (forces[e] == 0 || forces[e + 1] < forces[e]))
                    {
                        uint swapForce = forces[e]; forces[e] = forces[e + 1]; forces[e + 1] = swapForce;
                        float swapDistance = closest[e]; closest[e] = closest[e + 1]; closest[e + 1] = swapDistance;
                    }
            [unroll] for (int g = 0; g < 4; g++)
                if (forces[g] != 0)
                {
                    float3 tint = Palette[forces[g]].rgb;
                    float d = closest[g] * s;
                    colour = Over(colour, tint, Line(d, Widths.x * 3.4) * (28.0 / 255));
                    colour = Over(colour, tint * 0.45, Line(d, Widths.x * 1.7) * (84.0 / 255));
                    colour = Over(colour, tint, Line(d, Widths.x) * (120.0 / 255));
                }
            return colour * View.y;
        }
        """;
}

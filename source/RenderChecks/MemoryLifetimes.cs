using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using San14Tenkazu;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckMemoryLifetimes(string output)
    {
        Directory.CreateDirectory(output);
        string? previousGpu = Environment.GetEnvironmentVariable("SAN14_NO_GPU"), previousLog = Environment.GetEnvironmentVariable("SAN14_RENDER_LOG");
        Environment.SetEnvironmentVariable("SAN14_NO_GPU", "1");
        Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", null);
        var results = new List<object>();
        try
        {
            // Same object factories/counts as the original .cache/wpf-memory-audit diagnostic.
            CheckCollected("TextBlock control", () => new WC.TextBlock { Text = "control" }, 24, results);
            CheckCollected("PlateButton", () => new PlateButton("audit"), 24, results);
            CheckCollected("CornerMark", () => CardSkin.CornerMark("missing-audit-icon", "x", "audit", () => { }), 12, results);
            CheckCollected("GameTable text column control", () => new GameTable([new("text", GameTable.Kind.Text)], 28), 24, results);
            CheckCollected("GameTable shield column", () => new GameTable([new("shield", GameTable.Kind.Shield)], 28), 24, results);
            CheckCollected("GameTable numeric header", () =>
            {
                var table = new GameTable([new("loyalty", GameTable.Kind.Number, Field: OfficerFields.Loyalty)], 28);
                table.SetRows([new(1, [100L])]); return table;
            }, 24, results);
            CheckCollected("Closed ColumnEditDialog", () =>
            {
                var dialog = new ColumnEditDialog(OfficerFields.Loyalty, 1, 0, true); dialog.Close(); return dialog;
            }, 12, results);
            CheckCollected("Unloaded MapSurface", () =>
            {
                var map = new MapSurface(); map.Measure(new(0, 0)); map.Arrange(new(0, 0, 0, 0)); map.UpdateLayout();
                map.RaiseEvent(new W.RoutedEventArgs(W.FrameworkElement.UnloadedEvent)); return map;
            }, 4, results);
            CheckReplacedOfficerTables(results);
            CheckVisualSubscriptions(results);
            CheckShieldWaitingList(results);
            CheckBoundedUiLog(results);
            CheckBoundedRenderLog(results);
            CheckGpuConstructionCleanup(results);
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, Cases = results.Count, Results = results }));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SAN14_NO_GPU", previousGpu);
            Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", previousLog);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] LifetimeObjects(Func<object> create, int count)
        => Enumerable.Range(0, count).Select(_ => new WeakReference(create())).ToArray();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int LifetimeAlive(WeakReference[] references) => references.Count(r => r.IsAlive);

    private static void CollectLifetimes()
    {
        for (int i = 0; i < 3; i++)
        {
            DrainLifetimeDispatcher();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private static void DrainLifetimeDispatcher()
    {
        var frame = new WT.DispatcherFrame();
        WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        WT.Dispatcher.PushFrame(frame);
    }

    private static void CheckCollected(string name, Func<object> create, int count, List<object> results)
    {
        var references = LifetimeObjects(create, count);
        CollectLifetimes();
        int alive = LifetimeAlive(references);
        results.Add(new { Case = name, Created = count, AliveAfterGC = alive });
        Require(alive == 0, name + " retained discarded visuals");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ReplaceOfficerTables(OfficerTables owner)
    {
        var references = new List<WeakReference>();
        for (int i = 0; i <= 16; i++)
        {
            owner.Show([(OfficerFields.StatusName(2), new GameTable.Row(i + 1, [1, "audit", 100L, 50L, 50L, 50L, 50L, 50L]))]);
            if (i < 16) references.Add(new WeakReference(owner.Children.OfType<GameTable>().Single()));
        }
        return references.ToArray();
    }

    private static void CheckReplacedOfficerTables(List<object> results)
    {
        var owner = new OfficerTables("empty") { Drafts = new TableDrafts() };
        var references = ReplaceOfficerTables(owner);
        CollectLifetimes();
        int alive = LifetimeAlive(references);
        Require(alive == 0, "Replaced officer tables retained by their header/fill buttons");
        GC.KeepAlive(owner);
        results.Add(new { Case = "OfficerTables rebuilt for different officer", ReplacedTables = 16, AliveAfterGC = alive });
    }

    private static void CheckVisualSubscriptions(List<object> results)
    {
        var owner = new WC.TextBlock();
        Action? changed = null;
        int refreshed = 0;
        VisualEventSubscription.Attach(owner, handler => changed += handler, handler => changed -= handler, () => refreshed++);
        void Load() => owner.RaiseEvent(new W.RoutedEventArgs(W.FrameworkElement.LoadedEvent));
        void Unload() => owner.RaiseEvent(new W.RoutedEventArgs(W.FrameworkElement.UnloadedEvent));
        Require(changed is null, "An unshown visual subscribed to a static event");
        Load(); Load();
        Require(refreshed == 1 && changed!.GetInvocationList().Length == 1, "Repeated Loaded duplicated subscriptions");
        Task.Run(() => { for (int i = 0; i < 100; i++) changed?.Invoke(); }).GetAwaiter().GetResult();
        DrainLifetimeDispatcher();
        Require(refreshed == 2, "Worker resource notifications did not coalesce onto the UI thread");
        changed?.Invoke(); Unload(); DrainLifetimeDispatcher();
        Require(changed is null && refreshed == 2, "Unloaded visual retained its subscription or queued refresh");
        Load();
        Require(refreshed == 3 && changed!.GetInvocationList().Length == 1, "Reload did not refresh current resources");
        Unload();
        results.Add(new { Case = "Loaded/unloaded/reloaded subscription", Refreshes = refreshed, RemainingHandlers = changed?.GetInvocationList().Length ?? 0 });
    }

    private static void CheckShieldWaitingList(List<object> results)
    {
        var shield = new ForceShield();
        var waiting = (System.Collections.IList)typeof(ForceShield).GetField("waiting", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        int before = waiting.Count;
        var visual = new WM.DrawingVisual();
        var render = typeof(ForceShield).GetMethod("OnRender", Hidden)!;
        using (var dc = visual.RenderOpen())
            for (int i = 0; i < 1000; i++) render.Invoke(shield, [dc]);
        int added = waiting.Count - before;
        Require(added == 1, "Fallback repaints duplicated weak references");
        GC.KeepAlive(shield);
        results.Add(new { Case = "ForceShield fallback repaint registration", LiveShields = 1, Repaints = 1000, AddedWeakReferences = added });
    }

    private static void CheckBoundedUiLog(List<object> results)
    {
        var panel = new LogPanel();
        for (int i = 0; i < 2000; i++) panel.Append(new string('x', 100) + "\n");
        var text = (WC.TextBox)typeof(LogPanel).GetField("text", Hidden)!.GetValue(panel)!;
        Require(text.Text.Length <= LogPanel.MaximumCharacters && !text.IsUndoEnabled, "UI log retained unbounded text/undo history");
        panel.Append(new string('y', LogPanel.MaximumCharacters * 3) + "tail");
        Require(text.Text.Length <= LogPanel.MaximumCharacters && text.Text.EndsWith("tail"), "Oversized log entry exceeded the bound or lost its tail");
        results.Add(new { Case = "Bounded UI log", Characters = text.Text.Length, Maximum = LogPanel.MaximumCharacters, text.IsUndoEnabled });
    }

    private sealed class PausedLogWriter : StringWriter
    {
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closed;
        public override async Task WriteLineAsync(string? value) { await Release.Task; base.WriteLine(value); }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }

    private static void CheckBoundedRenderLog(List<object> results)
    {
        var writer = new PausedLogWriter();
        using var log = new MapRenderLog(writer);
        int accepted = 0;
        for (int i = 0; i < MapRenderLog.QueueCapacity + 2; i++)
            if (log.TryWrite("sample," + i)) accepted++;
        Require(accepted <= MapRenderLog.QueueCapacity + 1 && log.StopReason is not null, "Render log exceeded its queue bound without reporting overflow");
        writer.Release.SetResult();
        Require(log.Completion.Wait(TimeSpan.FromSeconds(10)), "Render log did not drain and close");
        string contents = writer.ToString();
        Require(writer.Closed && contents.Contains("# Render log stopped:") && contents.Split('\n').Count(l => l.StartsWith("sample,")) == accepted,
            "Render log lost accepted samples, omitted its stop reason, or retained its writer");
        results.Add(new { Case = "Bounded render log with blocked writer", Accepted = accepted, Capacity = MapRenderLog.QueueCapacity, writer.Closed, log.StopReason });
    }

    private static void CheckGpuConstructionCleanup(List<object> results)
    {
        var acquired = new List<IDisposable>();
        using (var complete = GpuMap.TryCreate(acquired.Add)) Require(complete is not null, "GPU unavailable for construction cleanup checks");
        int mapStages = acquired.Count;
        RequireReleased(acquired);
        for (int stage = 1; stage <= mapStages; stage++)
        {
            acquired.Clear();
            using var failed = GpuMap.TryCreate(resource =>
            {
                acquired.Add(resource);
                if (acquired.Count == stage) throw new InvalidOperationException("Offline construction fault");
            });
            Require(failed is null && acquired.Count == stage, "GPU constructor did not reach the requested failure point");
            RequireReleased(acquired);
        }
        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0]);
        acquired.Clear();
        using (var complete = MapShaders.TryCreate(device, acquired.Add)) Require(complete is not null, "Shaders unavailable for cleanup checks");
        int shaderStages = acquired.Count;
        RequireReleased(acquired);
        for (int stage = 1; stage <= shaderStages; stage++)
        {
            acquired.Clear();
            using var failed = MapShaders.TryCreate(device, resource =>
            {
                acquired.Add(resource);
                if (acquired.Count == stage) throw new InvalidOperationException("Offline shader construction fault");
            });
            Require(failed is null && acquired.Count == stage, "Shader constructor did not reach the requested failure point");
            RequireReleased(acquired);
            Require(device.NativePointer != IntPtr.Zero, "Shader failure disposed the caller's device");
        }
        results.Add(new { Case = "GPU construction failure cleanup", MapFailurePoints = mapStages, ShaderFailurePoints = shaderStages });
    }

    private static void RequireReleased(IEnumerable<IDisposable> resources)
    {
        foreach (var resource in resources)
        {
            if (resource is SharpGen.Runtime.ComObject com) Require(com.NativePointer == IntPtr.Zero, "Constructor failure retained " + resource.GetType().Name);
            if (resource is WaitHandle wait) Require(wait.SafeWaitHandle.IsClosed, "Constructor failure retained a wait handle");
        }
    }
}

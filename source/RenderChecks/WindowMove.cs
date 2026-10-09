using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WT = System.Windows.Threading;
using WInterop = System.Windows.Interop;

internal static partial class RenderChecks
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr MoveSendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool MovePostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    private static extern bool MoveSetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    // Run USER32's real caption move loop and replay native position updates on our own HWND.
    // This measures WPF handling in that nested loop without synthetic global pointer input.
    private static void CheckWindowMove(string output, bool expectResponsive)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SAN14_RENDER_LOG", Path.GetFullPath(Path.Combine(output, "frames.csv")));
        static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Hidden)!.GetValue(obj)!;
        static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Hidden)!.SetValue(obj, value);
        static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Hidden)!.Invoke(obj, args);
        var watch = Stopwatch.StartNew();
        string phase = "load";
        var events = new List<object>();
        var summaries = new List<object>();
        var refreshTimes = new List<double>();
        var dispatchAt = new Dictionary<WT.DispatcherOperation, double>();
        var slowDispatches = new List<object>();
        WT.DispatcherHookEventHandler started = (_, e) => dispatchAt[e.Operation] = watch.Elapsed.TotalMilliseconds;
        WT.DispatcherHookEventHandler completed = (_, e) =>
        {
            if (!dispatchAt.Remove(e.Operation, out double at)) return;
            double duration = watch.Elapsed.TotalMilliseconds - at;
            if (duration < 10) return;
            var method = typeof(WT.DispatcherOperation).GetField("_method", Hidden)?.GetValue(e.Operation) as Delegate;
            slowDispatches.Add(new { phase, at, duration, priority = e.Operation.Priority.ToString(),
                method = method?.Method.ToString(), owner = method?.Target?.GetType().FullName });
        };
        void Pump(double seconds)
        {
            double until = watch.Elapsed.TotalSeconds + seconds;
            do
            {
                var frame = new WT.DispatcherFrame();
                WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                WT.Dispatcher.PushFrame(frame);
                Thread.Sleep(2);
            } while (watch.Elapsed.TotalSeconds < until);
        }
        void Move(W.Window host, string name, GpuMap? gpu)
        {
            phase = name;
            host.Activate();
            Pump(.3);
            var source = (WInterop.HwndSource)W.PresentationSource.FromVisual(host);
            int enters = 0, exits = 0, moves = 0, sizes = 0;
            var gaps = new List<double>();
            double lastMove = -1;
            WInterop.HwndSourceHook hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                double at = watch.Elapsed.TotalMilliseconds;
                if (message == 0x231) enters++;
                if (message == 0x232) exits++;
                if (message == 3)
                {
                    if (lastMove >= 0) gaps.Add(at - lastMove);
                    lastMove = at;
                    moves++;
                }
                if (message == 5) sizes++;
                if (message is 3 or 5 or 0x231 or 0x232)
                    events.Add(new { phase, at, message, host.Left, host.Top, gpuPending = gpu?.Pending, copyWaitMs = gpu?.LastCopyWait });
                return IntPtr.Zero;
            };
            source.AddHook(hook);
            host.Dispatcher.Hooks.OperationStarted += started;
            host.Dispatcher.Hooks.OperationCompleted += completed;
            int frames = gpu?.PresentedFrames ?? 0;
            IntPtr handle = source.Handle;
            var calls = new List<double>();
            double atStart = watch.Elapsed.TotalMilliseconds;
            Task input = Task.Run(() =>
            {
                Thread.Sleep(150);
                try
                {
                    for (int i = 0; i < 180; i++)
                    {
                        long at = Stopwatch.GetTimestamp();
                        if (!MoveSetWindowPos(handle, IntPtr.Zero, 20 + (i < 90 ? i + 1 : 180 - i), 20, 0, 0, 0x15))
                            throw new System.ComponentModel.Win32Exception();
                        calls.Add(Stopwatch.GetElapsedTime(at).TotalMilliseconds);
                        Thread.Sleep(16);
                    }
                }
                finally { MovePostMessage(handle, 0x100, (IntPtr)0x0D, (IntPtr)1); }
            });
            try { MoveSendMessage(source.Handle, 0x112, (IntPtr)0xF010, IntPtr.Zero); input.GetAwaiter().GetResult(); }
            finally
            {
                source.RemoveHook(hook);
                host.Dispatcher.Hooks.OperationStarted -= started;
                host.Dispatcher.Hooks.OperationCompleted -= completed;
            }
            gaps.Sort();
            calls.Sort();
            var summary = new { phase, enters, exits, moves, sizes,
                elapsedMs = watch.Elapsed.TotalMilliseconds - atStart,
                meanGapMs = gaps.Count == 0 ? 0 : gaps.Average(),
                p95GapMs = gaps.Count == 0 ? 0 : gaps[(int)(gaps.Count * .95)],
                maxGapMs = gaps.Count == 0 ? 0 : gaps[^1],
                positionMeanMs = calls.Average(), positionP95Ms = calls[(int)(calls.Count * .95)], positionMaxMs = calls[^1],
                presented = (gpu?.PresentedFrames ?? 0) - frames };
            summaries.Add(summary);
            Console.WriteLine(JsonSerializer.Serialize(summary));
            Require(enters == 1 && exits == 1 && moves >= 150 && sizes == 0, "Native caption movement was not exercised without resizing");
        }
        var empty = new W.Window { Content = new WC.TextBlock { Text = "Native move control" },
            Width = 1280, Height = 800, Left = 20, Top = 20, ShowInTaskbar = false, Title = "천하도 창 이동 기준 검사" };
        empty.Show();
        try { Pump(.5); Move(empty, "empty-window", null); }
        finally { empty.Close(); }

        var editor = new EditorWindow();
        var root = (W.FrameworkElement)editor.Content;
        var map = Field<MapView>(editor, "map");
        var surface = Field<MapSurface>(map, "surface");
        var gpu = Field<GpuMap?>(surface, "gpu") ?? throw new InvalidOperationException("GPU renderer unavailable");
        editor.Content = null;
        var host = new W.Window { Content = root, Width = 1280, Height = 800, Left = 20, Top = 20,
            ShowInTaskbar = false, Title = "천하도 창 이동 렌더링 검사", FontFamily = editor.FontFamily, FontSize = editor.FontSize };
        var refresh = new WT.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        var availability = new WT.DispatcherTimer(WT.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        availability.Tick += (_, _) => Call(editor, "RefreshRegisteredAvailability");
        try
        {
            host.Show();
            Pump(10);
            Require(Field<WC.Image>(surface, "terrain").Source is not null, "Terrain did not load");
            Move(host, "terrain-idle", gpu);
            var fixture = new TestGame().Force(1, 1, 1);
            for (int i = 1; i <= 180; i++) fixture.Officer(i, "장수" + i, 1, 1, 4).Leading(i);
            GameSnapshot made = fixture.Build();
            int tick = 0;
            GameSnapshot Snapshot()
            {
                UnitRecord[] units = made.Units.Select((unit, i) =>
                {
                    var next = unit with { Data = (byte[])unit.Data.Clone() };
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(next.Data.AsSpan(UnitFields.TileOffset),
                        (ushort)(220 * (104 + i / 30 * 2) + 80 + (i % 30) * 3 + tick % 2));
                    return next;
                }).ToArray();
                tick++;
                return new GameSnapshot { Cities = made.Cities, Counties = made.Counties, Officers = made.Officers,
                    CastleByCity = made.CastleByCity, TileOwners = new byte[GameMemory.TileCount], TileCounties = new ushort[GameMemory.TileCount],
                    Forces = made.Forces, ForceRecords = made.ForceRecords, CorpsForce = made.CorpsForce,
                    OfficerById = made.OfficerById, Units = units };
            }
            refresh.Tick += (_, _) => map.SetSnapshot(Snapshot());
            map.SetSnapshot(Snapshot());
            Set(surface, "zoom", 4.0); Call(surface, "ApplyView");
            Pump(3);
            refresh.Start(); availability.Start();
            Move(host, "units-refresh", gpu);
            Pump(.5);
            refresh.Stop(); availability.Stop();
            var rules = new PlainRuleFixture(DoctrineRules.Spec);
            Set(editor, "ruleSnapshot", rules.Offices.Policies.Tactics.Formations.Snapshot());
            Set(editor, "tacticRuleSnapshot", rules.Offices.Policies.Tactics.Snapshot());
            Set(editor, "policyRuleSnapshot", rules.Offices.Policies.Snapshot());
            Set(editor, "officeRuleSnapshot", rules.Offices.Snapshot());
            Set(editor, "rankRuleSnapshot", rules.Ranks!.Snapshot());
            Set(editor, "doctrineRuleSnapshot", rules.Snapshot());
            RuleProfile profile = rules.Profile();
            Set(editor, "ruleKept", new RuleProfileRead(profile, "fixture"));
            void FillLists()
            {
                long at = Stopwatch.GetTimestamp();
                Call(editor, "FillLists");
                refreshTimes.Add(Stopwatch.GetElapsedTime(at).TotalMilliseconds);
            }
            for (int i = 0; i < 5; i++) { Set(editor, "snapshot", Snapshot()); FillLists(); }
            refresh.Tick += (_, _) => { Set(editor, "snapshot", Snapshot()); FillLists(); };
            refresh.Start(); availability.Start();
            Move(host, "rules-refresh", gpu);
            refresh.Stop(); availability.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new { rules = true, defaults = profile.Defaults.Length,
                refreshes = refreshTimes.Count, meanMs = refreshTimes.Average(), maxMs = refreshTimes.Max() }));
            if (expectResponsive)
                Require(refreshTimes.Skip(1).All(ms => ms < 50), "Warm rule-list refresh blocked the UI for 50ms or longer");
        }
        finally
        {
            refresh.Stop(); availability.Stop();
            var log = Field<MapRenderLog?>(surface, "renderLog");
            host.Close();
            log?.Dispose(); log?.Completion.GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(output, "events.json"), JsonSerializer.Serialize(events));
            File.WriteAllText(Path.Combine(output, "dispatch.json"), JsonSerializer.Serialize(slowDispatches));
            File.WriteAllText(Path.Combine(output, "refresh.json"), JsonSerializer.Serialize(refreshTimes));
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

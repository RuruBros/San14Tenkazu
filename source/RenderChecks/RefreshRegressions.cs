using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    private static void CheckRefreshRegressions(string output)
    {
        Directory.CreateDirectory(output);
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        var cases = new List<string>();
        void Check(bool condition, string name) { Require(condition, name); cases.Add(name); }
        static void Set(object target, string name, object? value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
        static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
        static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
        static void Pump(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (!done())
            {
                if (watch.Elapsed.TotalSeconds > 10) throw new TimeoutException("Refresh fixture did not complete");
                var frame = new WT.DispatcherFrame();
                WT.Dispatcher.CurrentDispatcher.BeginInvoke(WT.DispatcherPriority.Background, new Action(() => frame.Continue = false));
                WT.Dispatcher.PushFrame(frame); Thread.Yield();
            }
        }
        GameSnapshot Data(int value) => OriginalMapCastles(new TestGame().Force(1, 1, 1).City(1, 1)
            .Officer(1, "군주", 1, 1, 3).Officer(11, "장수", 1, 1, 3).With(11, OfficerFields.Leadership, value)).Build();
        var before = Data(65); var stale = Data(70); var fresh = Data(75);
        var layout = new GameLayout(new(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0),0,0,0,0,0,0,0,0,[],[],0,0,0);
        var identity = new GameIdentity(123, DateTime.UtcNow, 0x140000000, "OFFLINE-REFRESH", false, TestGame.Classes);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int reads = 0, blockedRead = 1;
        bool blockLayout = false;
        Exception? readFailure = null;
        void WaitForReplacement()
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }
        var access = new LiveGameReads(_ =>
        {
            if (!blockLayout) throw new InvalidOperationException("Unexpected layout access");
            WaitForReplacement();
            return layout;
        }, (_, _, _) =>
        {
            if (Interlocked.Increment(ref reads) == blockedRead)
            {
                WaitForReplacement();
                if (readFailure is not null) throw readFailure;
                return stale;
            }
            return fresh;
        });
        var editor = OfflineEditor(() => false, output, output, output, liveReads: access);
        var map = Get<MapView>(editor, "map"); var surface = Get<MapSurface>(map, "surface");
        try
        {
            Set(editor, "game", identity); Set(editor, "layout", layout); Set(editor, "snapshot", before);
            var task = (Task)Call(editor, "LiveRefreshAsync")!;
            Pump(() => entered.IsSet);
            Set(surface, "dragged", true); release.Set(); Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
            Check(ReferenceEquals(Get<GameSnapshot>(editor, "snapshot"), before), "read-finishing-during-drag-keeps-displayed-snapshot");
            Check(Get<bool>(editor, "liveRefreshDeferred"), "drag-defers-fresh-read");
            Set(surface, "dragged", false); Call(surface, "Settle");
            Pump(() => reads == 2 && !Get<bool>(editor, "refreshing"));
            Check(ReferenceEquals(Get<GameSnapshot>(editor, "snapshot"), fresh), "settling-rereads-instead-of-applying-stale-sample");
            blockedRead = 3; entered.Reset(); release.Reset();
            task = (Task)Call(editor, "LiveRefreshAsync")!; Pump(() => entered.IsSet);
            var newer = Data(90); Set(editor, "snapshot", newer);
            release.Set(); Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
            Check(ReferenceEquals(Get<GameSnapshot>(editor, "snapshot"), newer), "later-snapshot-is-not-overwritten-by-old-worker-result");
            foreach (Exception failure in new Exception[] { new GameSessionUnavailableException(new(GameSessionPhase.Title, 0, 0, [])), new IOException("stale sample") })
            {
                readFailure = failure; blockedRead = reads + 1; entered.Reset(); release.Reset();
                Set(editor, "snapshot", before);
                task = (Task)Call(editor, "LiveRefreshAsync")!; Pump(() => entered.IsSet);
                Set(editor, "snapshot", newer);
                release.Set(); Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
                Check(Get<GameIdentity?>(editor, "game") == identity && ReferenceEquals(Get<GameSnapshot>(editor, "snapshot"), newer),
                    "obsolete-" + failure.GetType().Name + "-keeps-current-connection");
            }
            readFailure = null; blockLayout = true; entered.Reset(); release.Reset();
            int initialReads = reads;
            Set(editor, "game", identity with { Verified = true });
            task = (Task)Call(editor, "LiveRefreshAsync")!; Pump(() => entered.IsSet);
            var replacement = layout with { CityArray = 456 };
            Set(editor, "layout", replacement);
            release.Set(); Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
            Check(reads == initialReads && ReferenceEquals(Get<GameLayout>(editor, "layout"), replacement), "replaced-layout-skips-obsolete-snapshot-read");
            Set(editor, "game", identity);
            var timer = Get<WT.DispatcherTimer>(editor, "liveTimer");
            void StopTimer(object? sender, EventArgs e) => timer.Stop();
            timer.Tick += StopTimer; timer.Interval = TimeSpan.FromMilliseconds(20);
            Set(surface, "dragged", true); timer.Start(); Pump(() => !timer.IsEnabled);
            timer.Tick -= StopTimer; timer.Interval = TimeSpan.FromSeconds(2);
            Check(reads == initialReads && Get<bool>(editor, "liveRefreshDeferred"), "timer-tick-during-drag-defers-read");
            Set(surface, "dragged", false); Call(surface, "Settle");
            Pump(() => reads == initialReads + 1 && !Get<bool>(editor, "refreshing"));
            Set(editor, "snapshot", fresh); Call(editor, "FillLists"); map.ShowList(ListKind.Officers);
            var rows = (List<GameTable.Row>)Call(editor, "OfficerRows")!;
            Check(ReferenceEquals(rows, Call(editor, "OfficerRows")), "unchanged-row-inputs-reuse-sorted-rows");
            var officer = fresh.OfficerById[11];
            OfficerFields.Leadership.Encode(80).CopyTo(officer.Data, OfficerFields.Leadership.Offset);
            rows = (List<GameTable.Row>)Call(editor, "OfficerRows")!;
            Check((long)rows.Single(r => (int)r.Tag == 11).Cells[5]! == 80, "in-place-record-mutation-invalidates-row");
            var ruler = fresh.OfficerById[1]; ruler.Data[OfficerFields.SurnameOffset] ^= 1;
            rows = (List<GameTable.Row>)Call(editor, "OfficerRows")!;
            Check((string)rows.Single(r => (int)r.Tag == 11).Cells[2]! == fresh.ForceName(1) + "\n" + fresh.BaseName(1), "ruler-name-change-invalidates-other-officers-force-labels");
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(officer.Data.AsSpan(OfficerFields.IdOffset), 1);
            Call(editor, "FillLists");
            var table = Get<GameTable>(editor, "officerTable");
            Check(table.Rows.Select(r => r.Tag).Distinct().Count() == 2 && table.Rows.Count(r => r.ReadOnly) == 1 &&
                table.Rows.Single(r => r.ReadOnly).Cells[1] as string == officer.Name + "\n" + officer.CourtesyName,
                "duplicate-id-displays-separate-slot-without-aliasing-writes");
            Call(table, "SortBy", 5); Call(editor, "FillLists");
            Check(table.Rows.Count == 2 && table.Contains(1), "sorted-duplicate-id-refresh-keeps-both-slots");
            Set(editor, "snapshot", null); Call(editor, "FillLists");
            Check(Get<System.Collections.IDictionary>(editor, "officerRows").Count == 0, "disconnect-clears-retained-officer-data");
            void CheckPolling(bool toolbar)
            {
                using var pollEntered = new ManualResetEventSlim(); using var pollRelease = new ManualResetEventSlim();
                bool block = false; int calls = 0;
                bool Running()
                {
                    Interlocked.Increment(ref calls);
                    if (!block) return false;
                    pollEntered.Set();
                    if (!pollRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                    return true;
                }
                W.Window window = toolbar ? OfflineEditor(Running, output, output, output) : new RegisteredOfficerWindow(GameCatalog.Empty, _ => { }, Running);
                string method = toolbar ? "PollRegisteredAvailabilityAsync" : "PollGameStateAsync";
                string state = toolbar ? "registeredGameRunning" : "gameRunning";
                string prefix = toolbar ? "toolbar" : "registered-window";
                block = true;
                try
                {
                    var poll = (Task)Call(window, method)!; Pump(() => pollEntered.IsSet);
                    bool dispatched = false;
                    window.Dispatcher.BeginInvoke(new Action(() => dispatched = true)); Pump(() => dispatched);
                    Check(!poll.IsCompleted, prefix + "-slow-process-enumeration-does-not-block-dispatcher");
                    int observedCalls = calls;
                    Check(((Task)Call(window, method)!).IsCompleted && calls == observedCalls, prefix + "-periodic-polls-do-not-overlap");
                    window.Close(); pollRelease.Set(); Pump(() => poll.IsCompleted); poll.GetAwaiter().GetResult();
                    Check(!Get<bool>(window, state), prefix + "-closed-window-rejects-inflight-result");
                }
                finally { pollRelease.Set(); window.Close(); }
            }
            CheckPolling(true); CheckPolling(false);
            var imageHost = new GpuImageHost { Width = 128, Height = 128 };
            imageHost.Measure(new(128, 128)); imageHost.Arrange(new W.Rect(0, 0, 128, 128));
            int measures = imageHost.MeasurePasses;
            for (int i = 0; i < 8; i++)
            {
                imageHost.Source = new System.Windows.Interop.D3DImage();
                Check(imageHost.IsArrangeValid, "completed-image-retains-arrangement-" + i);
                imageHost.Measure(new(128, 128)); imageHost.Arrange(new W.Rect(0, 0, 128, 128));
            }
            Check(imageHost.MeasurePasses == measures, "completed-image-switch-does-not-invalidate-measure");
            blockedRead = reads + 1; entered.Reset(); release.Reset();
            // The disconnected snapshot above was deliberately cleared; restore a live sample
            // to exercise a real read already in flight when the owner window closes.
            var closingSnapshot = fresh; Set(editor, "snapshot", closingSnapshot);
            task = (Task)Call(editor, "LiveRefreshAsync")!; Pump(() => entered.IsSet);
            editor.Close(); release.Set(); Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
            Check(ReferenceEquals(Get<GameSnapshot>(editor, "snapshot"), closingSnapshot), "closed-editor-rejects-inflight-memory-result");
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { gameAccess = false, reads, cases }));
            Console.WriteLine($"PASS refresh regressions: {cases.Count} checks; gameAccess=false");
        }
        finally
        {
            release.Set(); Set(editor, "game", null); map.HideEditors(); editor.Close(); app.Shutdown();
        }
    }
}

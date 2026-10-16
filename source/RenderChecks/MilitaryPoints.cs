using System.Collections;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void ReadMilitaryGame(string output)
    {
        if (File.Exists(output)) throw new IOException("Military evidence already exists");
        GameIdentity game = GameMemory.FindGame();
        var layout = GameMemory.ReadCurrentLayout(game);
        var first = GameMemory.ReadSnapshot(game, layout);
        var second = GameMemory.ReadSnapshot(game, layout, before: first);
        if (second.MilitaryCaps is not { Valid: true } caps) throw new InvalidDataException("Military caps unavailable");
        var records = second.CorpsRecords.Values.Where(c => c.Force > 0 && c.Number > 0)
            .Select(c => new { c.Id, c.Force, c.Number, c.Leader, c.MilitaryPoints,
                cap = caps.ForRank((int)second.ForceRecords[c.Force][ForceFields.Rank]), address = $"0x{c.Address:X}" }).ToArray();
        var result = new { utc = DateTime.UtcNow, machine = Environment.MachineName, game.ProcessId, game.Verified,
            pace = second.Pace.ToString(), date = first.Date is { } date ? new { date.Year, date.Month, date.Day } : null, second.Hour, records,
            stable = first.CorpsRecords.Count == second.CorpsRecords.Count && first.CorpsRecords.All(c => second.CorpsRecords.TryGetValue(c.Key, out var next)
                && c.Value.Address == next.Address && c.Value.Data.AsSpan().SequenceEqual(next.Data)),
            writes = 0, profileSaved = false };
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { game.ProcessId, game.Verified, records = records.Length, result.stable, result.pace, writes = 0 }));
    }
    private static void CheckMilitaryPoints(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "military-points.jsonl"));
        void Check(string scenario, bool passed)
        {
            string line = JsonSerializer.Serialize(new { scenario, passed });
            log.WriteLine(line); log.Flush(); Console.WriteLine(line); Require(passed, scenario);
        }
        static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
        static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args);
        var data = new TestGame().Force(4, 35, 4, 7).Force(9, 521, 9)
            .Officer(35, "원소", 4, 6, 1).Officer(199, "장수", 7, 6, 1).Officer(521, "마등", 9, 22, 1)
            .MilitaryPoints(4, 4, 1, 35, 1907, 4).MilitaryPoints(7, 4, 2, 199, 504, 4)
            .MilitaryPoints(9, 9, 1, 521, 2179).Build();
        var window = OfflineEditor();
        window.GetType().GetField("snapshot", Hidden)!.SetValue(window, data);
        var map = Field<MapView>(window, "map"); var card = map.ForceEditor;
        card.SetNames([], GameCatalog.Empty with { Ranks = Enumerable.Range(1, 12).Select(i => new GameName(i, $"작위{i}", "")).ToArray() });
        try
        {
            Check("force-view-opens", Call(window, "Show", new ForceTarget(4)) is true);
            var boxes = Field<IEnumerable>(card, "military").Cast<object>()
                .Select(r => (NumberField)r.GetType().GetProperty("Number")!.GetValue(r)!).ToArray();
            Check("only-selected-force-corps", boxes.Length == 2 && boxes[0].Current == 1907 && boxes[1].Current == 504);
            typeof(EditorCard).GetMethod("ShowPage", Hidden)!.Invoke(card, [5]);
            foreach (double width in new[] { 500.0, 600.0 })
            {
                card.Measure(new W.Size(width, 800)); card.Arrange(new W.Rect(0, 0, width, 800)); card.UpdateLayout();
                Check($"input-layout-{width}", boxes.All(b => b.Box.ActualWidth > 90 && b.Box.TranslatePoint(new W.Point(), card).X + b.Box.ActualWidth <= width));
                var bitmap = new WMI.RenderTargetBitmap((int)width, 800, 96, 96, WM.PixelFormats.Pbgra32);
                bitmap.Render(card); var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"military-{width}.png")); encoder.Save(file);
            }
            boxes[0].Set(1000);
            Check("one-draft-counted", card.Pending == 1 && card.MilitaryChanges(out _) is { Count: 1 } c && c[0].Corps == 4 && c[0].Value == 1000);
            var changed = data.With(ForceEdits.Plan(data, 4, [], [], [], [new(4, 1, 35, 2000), new(7, 2, 199, 600)]).Edits);
            window.GetType().GetField("snapshot", Hidden)!.SetValue(window, changed); Call(window, "RefreshEditor");
            Check("refresh-keeps-draft-and-follows-untouched", boxes[0].Value == 1000 && boxes[0].Current == 2000 && boxes[1].Value == 600 && card.Pending == 1);
            boxes[0].Set(150001);
            Check("above-native-cap-refused", card.MilitaryChanges(out string range) is null && range.Contains("150,000"));
            boxes[0].Box.Text = "-1";
            Check("negative-refused", card.MilitaryChanges(out _) is null);
            boxes[0].Box.Text = "1.5";
            Check("fraction-refused", card.MilitaryChanges(out _) is null);
            boxes[0].Set(0);
            Check("zero-accepted", card.MilitaryChanges(out _) is { Count: 1 } zero && zero[0].Value == 0);
            boxes[0].Set(150000);
            Check("native-cap-accepted", card.MilitaryChanges(out _) is { Count: 1 } cap && cap[0].Value == 150000);
            var requests = card.MilitaryChanges(out _)!; var plan = ForceEdits.Plan(changed, 4, [], [], [], requests);
            var memory = new FakeMemory(changed); GameMemory.Apply(memory, TestGame.Classes, plan.Edits);
            Check("ui-request-writes-core-balance", CorpsFields.MilitaryPoints.Read(memory[changed.CorpsRecords[4].Address]!) == 150000);
            GameMemory.Apply(memory, TestGame.Classes, Undo.Of(plan.Edits));
            Check("ui-request-undo", CorpsFields.MilitaryPoints.Read(memory[changed.CorpsRecords[4].Address]!) == 2000);
        }
        finally { Call(window, "Show", new ForceTarget(4)); window.Close(); }
    }
}

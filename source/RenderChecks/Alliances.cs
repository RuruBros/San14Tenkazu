using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    // Actual WPF input, validation and layout; synthetic records only, with no game writes or browser.
    private static void CheckAlliances(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl"));
        void Record(string scenario, bool passed)
        {
            string line = JsonSerializer.Serialize(new { scenario, passed });
            log.WriteLine(line); log.Flush(); Console.WriteLine(line);
            Require(passed, scenario);
        }
        var card = new ForceEditorCard { Visibility = W.Visibility.Visible };
        var fixture = new TestGame().Force(45, 1).Force(3, 2).Force(9, 3)
            .Officer(1, "설지", 45, 1, 1).Officer(2, "손책", 3, 2, 1).Officer(3, "공손찬", 9, 3, 1);
        foreach ((int id, string name) in new[] { (46, "이민족 A"), (47, "선비"), (48, "강"), (49, "산월"), (50, "남만") })
            fixture.Force(id, 100 + id).Officer(100 + id, name, id, id, 1);
        GameSnapshot snapshot = fixture.Build();
        snapshot.ForceRecords[45].Data[ForceFields.Agreement(3).Offset] = 0x8C;
        var others = (List<ForceEditorCard.Relation>)typeof(EditorWindow).GetMethod("Relations",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [snapshot, 45])!;
        var view = new ForceEditorCard.View(45, "설지군", "군주 설지 · 도시 1", System.Drawing.Color.Red, [],
            0, 0, 0, 9, others, Enumerable.Range(0, 5).Select(_ => (IReadOnlyList<(int Officer, int Days)>)new (int, int)[ForceFields.PolicySlots]).ToArray(),
            [], "", [], [], [], []);
        card.Open(view);
        var rows = (WC.Grid)typeof(ForceEditorCard).GetField("relationRows", Hidden)!.GetValue(card)!;
        WC.TextBox Box(int row, int column) => rows.Children.OfType<WC.TextBox>().Single(b => WC.Grid.GetRow(b) == row && WC.Grid.GetColumn(b) == column);
        Record("masked-period-shows-as-twelve", Box(0, 3).Text == "12" && card.Pending == 0);
        Record("tribal-period-inputs-hidden", Enumerable.Range(2, 5).All(r => Box(r, 3).Visibility == W.Visibility.Collapsed));
        Box(0, 3).Text = "48";
        Record("typed-period-tracked", card.Pending == 1 && card.AllianceChanges(out string error).SequenceEqual(new[] { (3, 48) }) && error.Length == 0);
        foreach (string bad in new[] { "-1", "49", "1.5", "", "abc" })
        {
            Box(0, 3).Text = bad;
            Record("invalid-period-" + bad, card.AllianceChanges(out string validationError).Count == 0 && validationError.Length > 0);
        }
        Box(0, 3).Text = "0";
        Record("zero-ends-alliance", card.AllianceChanges(out _).SequenceEqual(new[] { (3, 0) }));
        Box(0, 2).Text = "100";
        Record("friendship-and-alliance-tracked-independently", card.Pending == 2
            && card.FriendshipChanges(out _).SequenceEqual(new[] { (3, 100) }));
        card.Open(view);
        Record("reopening-discards-pending-values", card.Pending == 0 && card.AllianceChanges(out _).Count == 0);
        foreach (int width in new[] { 306, 360, 480 })
        {
            card.Measure(new W.Size(width, 700)); card.Arrange(new W.Rect(0, 0, width, 700)); card.UpdateLayout();
            Record("inputs-fit-" + width, rows.Children.OfType<WC.TextBox>().Where(b => b.Visibility == W.Visibility.Visible)
                .All(b => b.ActualWidth > 0 && b.TranslatePoint(new W.Point(b.ActualWidth, 0), card).X <= width));
            var bitmap = new WMI.RenderTargetBitmap(width, 700, 96, 96, WM.PixelFormats.Pbgra32); bitmap.Render(card);
            var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"card-{width}.png")); encoder.Save(file);
        }
        const ulong code = 0x2_0000_0000, data = code + 0x1000, setter = 0x7FF6_002E_BCC0,
            begin = 0x7FF6_0027_8270, end = 0x7FF6_0029_2760, orders = 0x7FF6_0027_54A0, map = 0x7FF6_0033_CED0;
        object Stub(string name, ulong transition, ulong third, byte firstByte, byte secondByte)
        {
            ulong months = transition == end ? 0UL : 12UL;
            GameMemory.FrameInvocation[] after = [new(transition, 0x3000, 0x4000, [third]), new(orders, 0, 0), new(map, 0, 0)];
            GameMemory.FrameStore[] stores = [new(0x3100, firstByte), new(0x4100, secondByte)];
            byte[] stub = GameMemory.FrameStub(code, data, setter, 0x3000, 0x4000, 0, [months], after, stores);
            return new { name, code, data, hex = Convert.ToHexString(stub), calls = new[]
            {
                new { function = setter, args = new ulong[] { 0x3000, 0x4000, months } },
                new { function = transition, args = new ulong[] { 0x3000, 0x4000, third } },
                new { function = orders, args = new ulong[] { 0, 0 } }, new { function = map, args = new ulong[] { 0, 0 } }
            }, stores };
        }
        File.WriteAllText(Path.Combine(output, "stubs.json"), JsonSerializer.Serialize(new[]
        {
            Stub("begin", begin, data + GameMemory.StubArgs, 12, 0x8C), Stub("end-undo", end, 0, 0x80, 0)
        }, new JsonSerializerOptions { IncludeFields = true }));
    }
}

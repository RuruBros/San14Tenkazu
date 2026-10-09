using System.IO;
using System.Text.Json;
using San14Tenkazu;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void CheckAiExtensions(string output, bool native)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, native ? "native-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + ".jsonl" : "checks.jsonl"));
        void Record(string scenario, object details, bool passed)
        {
            string row = JsonSerializer.Serialize(new { scenario, details, passed });
            log.WriteLine(row); log.Flush(); Console.WriteLine(row); Require(passed, scenario);
        }
        const ulong exe = 0x7FF600000000, code = 0x200000000, data = code + AiExtensionStub.Page;
        File.WriteAllText(Path.Combine(output, "stub.json"), JsonSerializer.Serialize(new { exe, code, data,
            hex = Convert.ToHexString(AiExtensionStub.Build(exe, data)), installer = Convert.ToHexString(AiExtensionStub.Install(data)) }));
        GameIdentity? identity = native ? GameMemory.FindGame() : null;
        string folder = FaceLibrary.FindGameDirectory(identity is { } id ? GameMemory.ExecutablePath(id) : null)
            ?? throw new DirectoryNotFoundException(AiTreeFile.RelativePath);
        AiTree[] trees = AiTreeFile.Read(folder);
        if (native)
        {
            GameIdentity game = identity!.Value;
            AiExtensionStatus before = GameMemory.ReadAiExtensions(game, trees);
            if (before.Settings.SupplyProtection) throw new InvalidOperationException("Native bypass probe requires the extension to be off");
            try
            {
                AiExtensionStatus installed = GameMemory.ApplyAiExtensions(game, trees, before.Settings);
                Record("native-frame-install-disabled", installed, !installed.Settings.SupplyProtection);
                Thread.Sleep(5000);
                AiExtensionStatus after = GameMemory.ReadAiExtensions(game, trees);
                Record(after.Entries > installed.Entries ? "native-selector-bypass-executed" : "native-no-selector-calls-in-observation",
                    new { before = installed, after, observedCalls = after.Entries - installed.Entries }, !after.Settings.SupplyProtection
                    && after.Checks == installed.Checks && after.Returns == installed.Returns && after.Holds == installed.Holds);
            }
            finally
            {
                GameMemory.RemoveDisabledAiExtension(game, trees);
                AiExtensionStatus restored = GameMemory.ReadAiExtensions(game, trees);
                Record("native-probe-restored-original-selector", restored, restored.Entries == 0);
            }
            return;
        }
        var view = new AiView(trees, () => null, (_, _) => Task.FromResult(false));
        T Field<T>(string name) => (T)typeof(AiView).GetField(name, Hidden)!.GetValue(view)!;
        void Tab(bool extended) => Field<PaneTab>(extended ? "extendedTab" : "basicTab").RaiseEvent(new W.RoutedEventArgs(WC.Button.ClickEvent));
        Tab(true);
        Record("extension-default-off-and-delegated", new { pending = view.Pending }, !Field<WC.CheckBox>("supplyProtection").IsChecked!.Value
            && Field<WC.ComboBox>("extensionScope").SelectedIndex == 0 && !view.Pending);
        Field<WC.CheckBox>("supplyProtection").IsChecked = true;
        Record("extension-draft-tracked", new { pending = view.Pending }, view.Pending && Field<WC.ComboBox>("supplyFailure").IsEnabled);
        Tab(false); Tab(true);
        Record("draft-survives-tab-switch", new { pending = view.Pending }, view.Pending && Field<WC.CheckBox>("supplyProtection").IsChecked == true);
        view.ShowTreeView(); view.ShowSettings();
        Record("tree-return-keeps-extension-tab", new { view.Expanded }, !view.Expanded && Field<PaneTab>("extendedTab").Selected);
        Record("offline-apply-disabled", new { enabled = Field<PlateButton>("apply").IsEnabled }, !Field<PlateButton>("apply").IsEnabled);
        foreach (int width in new[] { 306, 360, 480 })
        {
            view.Measure(new W.Size(width, 720)); view.Arrange(new W.Rect(0, 0, width, 720)); view.UpdateLayout();
            WC.ComboBox choice = Field<WC.ComboBox>("supplyFailure");
            Record("extension-layout-" + width, new { width, choice.ActualWidth }, choice.ActualWidth > 0
                && choice.TranslatePoint(new W.Point(choice.ActualWidth, 0), view).X <= width);
            var bitmap = new WMI.RenderTargetBitmap(width, 720, 96, 96, WM.PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, "extensions-" + width + ".png")); encoder.Save(file);
        }
    }
}

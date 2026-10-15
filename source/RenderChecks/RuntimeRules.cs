using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using San14Tenkazu;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    private static void ExportRuntimeTraitList(string[] args)
    {
        string output = args.First(a => !a.StartsWith("--")); Directory.CreateDirectory(output);
        ulong module = Convert.ToUInt64(args.Single(a => a.StartsWith("--module="))[9..], 16);
        ulong allocation = 0x20000000000;
        File.WriteAllBytes(Path.Combine(output, "payload.bin"), RuntimeTraitList.Build(module, allocation));
        File.WriteAllText(Path.Combine(output, "payload.json"), JsonSerializer.Serialize(new { module, allocation, site = RuntimeTraitList.Site, size = RuntimeTraitList.Size,
            writablePages = RuntimeTraitList.WritablePages, mutableColumns = RuntimeTraitList.MutableColumns }));
        Console.WriteLine("Exported product-generated Native payload; gameAccess=false");
    }
    // An owned fixture process only: never discovers, reads or writes a game process.
    private static void RunRuntimeRuleWorker()
    {
        IntPtr page = Marshal.AllocHGlobal(4096);
        try
        {
            Marshal.WriteInt64(page, 1);
            Console.WriteLine(page.ToInt64()); Console.Out.Flush();
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(45))
            {
                Marshal.WriteInt64(page, Marshal.ReadInt64(page) + 1);
                Thread.Sleep(5);
            }
        }
        finally { Marshal.FreeHGlobal(page); }
    }

    private static void CheckRuntimeRulesOffline(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        RuntimeTraitList.ValidateDefinition(RuntimeTraitList.Definition);
        using (var saved = typeof(RenderChecks).Assembly.GetManifestResourceStream("RuntimeRules.real-column-diagnostic.json")!)
        using (var diagnostic = JsonDocument.Parse(saved))
        {
            var root = diagnostic.RootElement;
            ulong module = Convert.ToUInt64(root.GetProperty("module").GetString(), 16);
            byte[] actual = Convert.FromHexString(root.GetProperty("columns").GetString()!);
            Require(RuntimeTraitList.Rebase(RuntimeTraitList.Definition.Columns, module).SequenceEqual(actual), "real-diagnostic-all-213-columns-match");
        }
        foreach (ulong module in new[] { 0x140000000UL, 0x7ff76e7f0000UL, 0x7ff737fe0000UL })
        {
            byte[] payload = RuntimeTraitList.Build(module, 0x20000000000);
            ulong original = RuntimeTraitList.Definition.OriginalBase;
            bool stale = Enumerable.Range(0, payload.Length - 7).Any(i =>
            { ulong value = BitConverter.ToUInt64(payload, i); return value >= original && value - original < RuntimeTraitList.Definition.ImageSize; });
            Require(!stale, $"no-captured-absolute-address-at-module-{module:x}");
        }
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(RenderChecks).Assembly.Location); start.ArgumentList.Add("--runtime-rule-worker");
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("Fixture did not start");
        try
        {
            string line = worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult()!;
            IntPtr address = (IntPtr)long.Parse(line);
            long Tick()
            {
                byte[] bytes = new byte[8];
                if (!RuntimeFixtureNative.ReadProcessMemory(worker.Handle, address, bytes, (UIntPtr)8, out var read) || read.ToUInt64() != 8)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return BitConverter.ToInt64(bytes);
            }
            long tick = Tick(); Thread.Sleep(40); Require(Tick() > tick, "owned-worker-running");
            using (RuntimeProcessFreeze.Acquire(worker.Handle, worker.Id, []))
            {
                tick = Tick(); Thread.Sleep(40); Require(Tick() == tick, "all-worker-threads-frozen");
                using var memory = new GameMemory.RuntimeProcessMemory(worker.Handle);
                ulong list = GameMemory.AllocateRuntimeTraitList(worker.Handle, 0x140000000);
                RuntimeTraitList.VerifyAllocation(memory.Read, 0x140000000, list);
                Require(true, "list-allocation-written-and-verified-in-owned-worker");
                Require(RuntimeFixtureNative.VirtualProtectEx(worker.Handle, (IntPtr)unchecked((long)(list + 4096)), (UIntPtr)(RuntimeTraitList.Size - 4096), 2, out _), "old-readonly-layout-simulated");
                GameMemory.ProtectRuntimeTraitList(worker.Handle, list);
                RuntimeTraitList.VerifyAllocation(memory.Read, 0x140000000, list);
                bool foreignRejected = false;
                try { GameMemory.ProtectRuntimeTraitList(worker.Handle, list + 4096); }
                catch (InvalidDataException) { foreignRejected = true; }
                Require(foreignRejected, "interior-address-cannot-claim-allocation-ownership");
                foreach (int offset in Enumerable.Range(0, RuntimeTraitList.Size / 4096).Select(i => i * 4096))
                {
                    ulong at = list + (uint)offset;
                    uint want = offset == 0 ? 0x20U : RuntimeTraitList.WritablePages.Contains(offset) ? 4U : 2U;
                    byte[] info = new byte[48];
                    Require(RuntimeFixtureNative.VirtualQueryEx(worker.Handle, (IntPtr)unchecked((long)at), info, (UIntPtr)48) != UIntPtr.Zero
                        && BitConverter.ToUInt32(info, 36) == want, $"list-page-protection-{want}");
                }
                var write = new RuntimeRuleWrite(unchecked((ulong)address.ToInt64()), BitConverter.GetBytes(tick), BitConverter.GetBytes(tick + 100), true);
                byte[] protection = new byte[48];
                Require(RuntimeFixtureNative.VirtualQueryEx(worker.Handle, address, protection, (UIntPtr)48) != UIntPtr.Zero, "fixture-protection-read");
                uint originalProtection = BitConverter.ToUInt32(protection, 36);
                RuntimeRuleTransaction.Apply(memory, [write], () => { }, () => { });
                Require(Tick() == tick + 100, "native-write-readback-cache-flush");
                Require(RuntimeFixtureNative.VirtualQueryEx(worker.Handle, address, protection, (UIntPtr)48) != UIntPtr.Zero
                    && BitConverter.ToUInt32(protection, 36) == originalProtection, "native-page-protection-restored");
                bool rollback = false;
                var reverse = new RuntimeRuleWrite(write.Address, write.After, write.Before, true);
                try { RuntimeRuleTransaction.Apply(memory, [reverse], () => { }, () => throw new IOException("fixture journal failure")); }
                catch (IOException) { rollback = true; }
                Require(rollback && Tick() == tick + 100, "native-write-rollback-after-journal-failure");
                tick = Tick();
            }
            Thread.Sleep(40); Require(Tick() > tick, "worker-resumed-after-success");
            bool refused = false;
            try { using var lease = RuntimeProcessFreeze.Acquire(worker.Handle, worker.Id, [(0UL, ulong.MaxValue)]); }
            catch (InvalidOperationException) { refused = true; }
            Require(refused, "active-instruction-range-refused");
            tick = Tick(); Thread.Sleep(40); Require(Tick() > tick, "worker-resumed-after-refusal");
        }
        finally { if (!worker.HasExited) worker.Kill(); worker.WaitForExit(5000); }

        var officerCard = new OfficerEditorCard();
        var choices = (NameChoice[])typeof(OfficerEditorCard).GetField("personalities", Hidden)!.GetValue(officerCard)!;
        var chart = (W.FrameworkElement)typeof(OfficerEditorCard).GetField("chart", Hidden)!.GetValue(officerCard)!;
        var maximum = chart.GetType().GetProperty("Maximum", Hidden)!;
        officerCard.FollowRuntimeRules(new(true, AbilityRuleMode.Growth200));
        Require(choices.All(c => c.Box.Visibility == W.Visibility.Visible), "officer-editor-nine-empty-slots-visible");
        Require((int)maximum.GetValue(chart)! == 200, "officer-chart-200-scale");
        choices[8].SetNames([new GameName(77, "해독", "")]); choices[8].Pick(77);
        officerCard.FollowRuntimeRules(RuntimeRuleSettings.Original);
        Require(choices[8].Picked == 77 && choices[8].Box.Visibility == W.Visibility.Visible, "off-retains-pending-ninth-personality");
        Require((int)maximum.GetValue(chart)! == 100, "officer-chart-original-scale");

        foreach (bool nine in new[] { false, true })
            foreach (var mode in Enum.GetValues<AbilityRuleMode>())
            {
                var settings = new RuntimeRuleSettings(nine, mode);
                var window = new RuntimeRulesWindow(settings);
                try
                {
                    Require(window.Selection == settings, $"selection-{nine}-{mode}");
                    window.TraitList.IsChecked = true;
                    Require(window.Selection.TraitList9 && window.Selection.Traits9, $"list-enables-effects-{nine}-{mode}");
                    window.Traits.IsChecked = false;
                    Require(!window.Selection.TraitList9, $"effects-off-disables-list-{nine}-{mode}");
                    window.ShowSettings(settings);
                    window.SetBusy(true); Require(!window.Apply.IsEnabled && !window.Restore.IsEnabled, $"busy-{nine}-{mode}");
                    window.SetBusy(false); Require(window.Apply.IsEnabled, $"ready-{nine}-{mode}");
                    var content = (W.FrameworkElement)window.Content;
                    content.Measure(new W.Size(466, double.PositiveInfinity));
                    content.Arrange(new W.Rect(0, 0, 466, content.DesiredSize.Height)); content.UpdateLayout();
                    Require(content.DesiredSize.Height < 760 && content.DesiredSize.Height > 300, $"layout-{nine}-{mode}");
                    var bitmap = new WMI.RenderTargetBitmap(466, (int)Math.Ceiling(content.DesiredSize.Height), 96, 96, WM.PixelFormats.Pbgra32);
                    var background = new WM.DrawingVisual();
                    using (var drawing = background.RenderOpen()) drawing.DrawRectangle(CardSkin.Parchment, null, new W.Rect(0, 0, 466, content.DesiredSize.Height));
                    bitmap.Render(background); bitmap.Render(content);
                    var encoder = new WMI.PngBitmapEncoder(); encoder.Frames.Add(WMI.BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, $"rules-{nine}-{mode}.png")); encoder.Save(file);
                    window.ShowSettings(null); window.SetBusy(false); Require(!window.Apply.IsEnabled, $"unknown-blocked-{nine}-{mode}");
                }
                finally { window.Close(); }
            }
        File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { gameAccess = false, checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} isolated process/WPF checks; gameAccess=false");
    }

    private static class RuntimeFixtureNative
    {
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern UIntPtr VirtualQueryEx(IntPtr process, IntPtr address, byte[] info, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool VirtualProtectEx(IntPtr process, IntPtr address, UIntPtr size, uint protection, out uint original);
    }
}

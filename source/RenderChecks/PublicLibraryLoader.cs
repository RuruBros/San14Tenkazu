using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using San14Tenkazu.Seasons;

internal static partial class RenderChecks
{
    private static void CheckPublicLibraryLoader(string output)
    {
        Directory.CreateDirectory(output);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(typeof(RenderChecks).Assembly.Location); start.ArgumentList.Add("--runtime-rule-worker");
        using var worker = Process.Start(start) ?? throw new IOException("Owned worker did not start");
        IntPtr path = IntPtr.Zero, thread = IntPtr.Zero; bool finished = false;
        try
        {
            worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            var plan = PublicLibraryLoader.Inspect(worker.Handle, worker.Id);
            var resident = worker.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string dll = new[] { "version.dll", "winmm.dll", "cryptui.dll" }.First(n => !resident.Contains(n));
            string file = Path.Combine(Environment.SystemDirectory, dll);
            byte[] text = Encoding.Unicode.GetBytes(file + '\0');
            path = LoaderFixtureNative.VirtualAllocEx(worker.Handle, IntPtr.Zero, (UIntPtr)text.Length, 0x3000, 4);
            if (path == IntPtr.Zero || !LoaderFixtureNative.WriteProcessMemory(worker.Handle, path, text, (UIntPtr)text.Length, out var written) || written.ToUInt64() != (ulong)text.Length)
                throw new IOException("Owned worker path upload failed");
            plan.Verify();
            thread = LoaderFixtureNative.CreateRemoteThread(worker.Handle, IntPtr.Zero, UIntPtr.Zero, (IntPtr)unchecked((long)plan.Entry), path, 0, out _);
            if (thread == IntPtr.Zero || LoaderFixtureNative.WaitForSingleObject(thread, 10000) != 0) throw new IOException("Owned worker loader did not terminate");
            finished = true; worker.Refresh();
            var loaded = worker.Modules.Cast<ProcessModule>().Single(m => m.FileName.Equals(file, StringComparison.OrdinalIgnoreCase));
            if (worker.HasExited || !LoaderFixtureNative.VirtualFreeEx(worker.Handle, path, UIntPtr.Zero, 0x8000)) throw new IOException("Owned worker cleanup failed");
            path = IntPtr.Zero;
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { gameAccess = false, worker = worker.Id, entry = $"0x{plan.Entry:x}", plan.Hops,
                loaded = loaded.FileName, loadedBase = $"0x{loaded.BaseAddress.ToInt64():x}", terminated = true, pathReleased = true, windows = FileVersionInfo.GetVersionInfo(Path.Combine(Environment.SystemDirectory, "kernel32.dll")).FileVersion }));
            Console.WriteLine("PASS public loader resolved and invoked in owned worker; DLL mapped, thread terminated, path released; gameAccess=false");
        }
        finally
        {
            if (path != IntPtr.Zero && finished) LoaderFixtureNative.VirtualFreeEx(worker.Handle, path, UIntPtr.Zero, 0x8000);
            if (thread != IntPtr.Zero) LoaderFixtureNative.CloseHandle(thread);
            if (!worker.HasExited) worker.Kill(); worker.WaitForExit(5000);
        }
    }
    private static class LoaderFixtureNative
    {
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr a, UIntPtr s, uint t, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool VirtualFreeEx(IntPtr p, IntPtr a, UIntPtr s, uint t);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] b, UIntPtr s, out UIntPtr w);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr CreateRemoteThread(IntPtr p, IntPtr a, UIntPtr s, IntPtr entry, IntPtr data, uint flags, out uint tid);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr h);
    }
}

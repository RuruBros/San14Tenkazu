using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace San14Tenkazu;

// Strict quiescence: never write a partly executing instruction or a routine with a return
// address on a suspended stack. All scans are conservative; an uncertain stack refuses the edit.
internal sealed class RuntimeProcessFreeze : IDisposable
{
    private readonly IntPtr process;
    private bool suspended;
    private RuntimeProcessFreeze(IntPtr process) { this.process = process; }

    internal static RuntimeProcessFreeze Acquire(IntPtr process, int pid, (ulong Start, ulong End)[] ranges)
    {
        var lease = new RuntimeProcessFreeze(process);
        int status = NtSuspendProcess(process);
        if (status < 0) throw new Win32Exception(L.RuntimeErrors.SuspendFailed($"{status:X8}"));
        lease.suspended = true;
        try
        {
            int[] ids = Threads(pid);
            foreach (int id in ids) lease.CheckThread(id, ranges);
            if (!ids.SequenceEqual(Threads(pid))) throw new InvalidOperationException(L.RuntimeErrors.ThreadsChanged);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static int[] Threads(int pid)
    {
        using var p = Process.GetProcessById(pid);
        return p.Threads.Cast<ProcessThread>().Select(t => t.Id).Order().ToArray();
    }

    private void CheckThread(int id, (ulong Start, ulong End)[] ranges)
    {
        IntPtr thread = OpenThread(0x0008 | 0x0040, false, (uint)id);
        if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr buffer = Marshal.AllocHGlobal(1248);
        IntPtr aligned = (IntPtr)((buffer.ToInt64() + 15) & ~15L);
        try
        {
            Marshal.Copy(new byte[1232], 0, aligned, 1232);
            Marshal.WriteInt32(aligned, 48, 0x00100001); // AMD64 CONTEXT_CONTROL
            if (!GetThreadContext(thread, aligned)) throw new Win32Exception(Marshal.GetLastWin32Error());
            ulong rip = unchecked((ulong)Marshal.ReadInt64(aligned, 248));
            ulong rsp = unchecked((ulong)Marshal.ReadInt64(aligned, 152));
            bool Affected(ulong value) => ranges.Any(r => value >= r.Start && value < r.End);
            if (Affected(rip)) throw new InvalidOperationException(L.RuntimeErrors.FunctionExecuting);
            byte[] basic = new byte[48];
            int status = NtQueryInformationThread(thread, 0, basic, basic.Length, out _);
            if (status < 0) throw new Win32Exception(L.RuntimeErrors.ThreadQueryFailed($"{status:X8}"));
            ulong teb = BitConverter.ToUInt64(basic, 8);
            byte[] bounds = Read(teb + 8, 16);
            ulong top = BitConverter.ToUInt64(bounds), bottom = BitConverter.ToUInt64(bounds, 8);
            if (rsp < bottom || rsp > top || top - rsp > 4 * 1024 * 1024)
                throw new InvalidOperationException(L.RuntimeErrors.StackRangeUnknown);
            byte[] stack = Read(rsp, checked((int)(top - rsp)));
            for (int offset = 0; offset + 8 <= stack.Length; offset += 8)
                if (Affected(BitConverter.ToUInt64(stack, offset)))
                    throw new InvalidOperationException(L.RuntimeErrors.ReturnAddressExecuting);
        }
        finally { Marshal.FreeHGlobal(buffer); CloseHandle(thread); }
    }

    private byte[] Read(ulong address, int length)
    {
        if (length == 0) return [];
        byte[] bytes = new byte[length];
        if (!ReadProcessMemory(process, (IntPtr)unchecked((long)address), bytes, (UIntPtr)length, out var read) || read.ToUInt64() != (ulong)length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return bytes;
    }

    public void Dispose()
    {
        if (!suspended) return;
        int status = NtResumeProcess(process);
        if (status < 0) throw new Win32Exception(L.RuntimeErrors.ResumeFailed($"{status:X8}"));
        suspended = false;
    }

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr process);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationThread(IntPtr thread, int info, byte[] value, int size, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadContext(IntPtr thread, IntPtr context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr read);
}

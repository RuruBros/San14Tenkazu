using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace San14Tenkazu.Seasons;

// Resolve the target's public ABI from its loaded PE, retaining any existing dispatch hooks.
// File versions and whole-file hashes do not describe whether this operation is valid.
internal sealed class PublicLibraryLoader
{
    internal sealed record Module(string Path, ulong Base, int Size);
    internal sealed record Region(ulong Base, ulong Allocation, ulong Size, uint State, uint Protection, uint Type);
    private readonly Func<ulong, int, byte[]> read;
    private readonly Func<ulong, Region> query;
    private readonly Func<Module[]> modules;
    private readonly List<(ulong At, byte[] Bytes, Region Region)> snapshots = [];
    private readonly Module[] initial;
    internal ulong Entry { get; }
    internal int Hops { get; private set; }
    internal PublicLibraryLoader(Func<ulong, int, byte[]> read, Func<ulong, Region> query, Func<Module[]> modules)
    {
        this.read = read; this.query = query; this.modules = modules; initial = modules();
        var kernel = Find("kernel32.dll");
        if (!Path.GetFullPath(kernel.Path).Equals(Path.Combine(System.Environment.SystemDirectory, "kernel32.dll"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Public loader is not the system kernel32 module");
        Entry = Export(kernel, "LoadLibraryW", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        ulong cursor = Entry; var visited = new HashSet<ulong>();
        for (int hop = 0; hop < 16; hop++)
        {
            if (!visited.Add(cursor)) throw new InvalidDataException("Public loader dispatch cycle");
            Region region = query(cursor);
            if (region.State != 0x1000 || (region.Protection & 0x100) != 0 || (region.Protection & 0xff) is not (0x20 or 0x40 or 0x80))
                throw new InvalidDataException("Public loader dispatch is not readable executable memory");
            byte[] data = Capture(cursor, 16); Hops++;
            int prefix = data[0] == 0x48 && data[1] == 0xff && data[2] == 0x25 ? 1 : 0;
            if (data[0] == 0xe9) cursor = checked((ulong)((long)cursor + 5 + BitConverter.ToInt32(data, 1)));
            else if (data[0] == 0xeb) cursor = checked((ulong)((long)cursor + 2 + (sbyte)data[1]));
            else if (data[prefix] == 0xff && data[prefix + 1] == 0x25)
            {
                ulong slot = checked((ulong)((long)cursor + prefix + 6 + BitConverter.ToInt32(data, prefix + 2)));
                cursor = BitConverter.ToUInt64(Capture(slot, 8));
            }
            else
            {
                var owner = initial.SingleOrDefault(m => cursor >= m.Base && cursor - m.Base < (ulong)m.Size);
                if (owner is null || region.Type != 0x1000000 || region.Allocation != owner.Base || !ExecutableSection(owner, cursor))
                    throw new InvalidDataException("Public loader terminal is outside a loaded executable image section");
                Verify(); return;
            }
        }
        throw new InvalidDataException("Public loader dispatch exceeds bounded traversal");
    }
    private Module Find(string name) => initial.SingleOrDefault(m => Path.GetFileName(m.Path).Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"Public loader forwarder module is not loaded: {name}");
    private byte[] Capture(ulong at, int length)
    {
        if (length <= 0 || length > 1024 * 1024 || at == 0 || at > 0x00007fffffffffffUL - (uint)length)
            throw new InvalidDataException("Public loader read exceeds bounds");
        ulong end = at + (uint)length;
        for (ulong p = at; p < end;)
        {
            Region r = query(p);
            if (r.State != 0x1000 || (r.Protection & 0x100) != 0 || (r.Protection & 0xff) is not (2 or 4 or 8 or 0x20 or 0x40 or 0x80)
                || p < r.Base || r.Size == 0 || r.Base > ulong.MaxValue - r.Size || p >= r.Base + r.Size)
                throw new InvalidDataException("Public loader read range is inaccessible");
            int count = checked((int)(Math.Min(end, r.Base + r.Size) - p));
            byte[] bytes = read(p, count);
            if (bytes.Length != count) throw new IOException("Public loader short read");
            snapshots.Add((p, bytes, r)); p += (uint)count;
        }
        // Return exactly the captured bytes, without a second unsnapshotted read.
        byte[] result = new byte[length]; int offset = length;
        for (int i = snapshots.Count - 1; offset > 0; i--)
        { var s = snapshots[i]; offset -= s.Bytes.Length; s.Bytes.CopyTo(result, offset); }
        return result;
    }
    private byte[] At(Module module, uint rva, int size)
    {
        if (rva > module.Size || size < 0 || (ulong)rva + (uint)size > (ulong)module.Size)
            throw new InvalidDataException("Public loader PE range exceeds module");
        Region region = query(module.Base + rva);
        if (region.Type != 0x1000000 || region.Allocation != module.Base) throw new InvalidDataException("Public loader image mapping changed");
        int first = snapshots.Count;
        byte[] result = Capture(module.Base + rva, size);
        for (int i = first; i < snapshots.Count; i++)
            if (snapshots[i].Region.Type != 0x1000000 || snapshots[i].Region.Allocation != module.Base)
                throw new InvalidDataException("Public loader PE read crossed its image mapping");
        return result;
    }
    private (byte[] Header, uint Pe) Header(Module module)
    {
        byte[] dos = At(module, 0, 64); uint pe = BitConverter.ToUInt32(dos, 60);
        if (BitConverter.ToUInt16(dos) != 0x5a4d || pe > 0x100000) throw new InvalidDataException("Public loader DOS header invalid");
        byte[] header = At(module, pe, 264);
        if (BitConverter.ToUInt32(header) != 0x4550 || BitConverter.ToUInt16(header, 4) != 0x8664
            || BitConverter.ToUInt16(header, 24) != 0x20b || BitConverter.ToUInt16(header, 20) < 240
            || BitConverter.ToUInt32(header, 80) != module.Size || BitConverter.ToUInt32(header, 132) < 1)
            throw new InvalidDataException("Public loader requires a consistent AMD64 PE image");
        return (header, pe);
    }
    private bool ExecutableSection(Module module, ulong address)
    {
        var (h, pe) = Header(module); int count = BitConverter.ToUInt16(h, 6);
        if (count is < 1 or > 96) throw new InvalidDataException("Public loader section count invalid");
        byte[] sections = At(module, checked(pe + 24u + BitConverter.ToUInt16(h, 20)), count * 40);
        ulong rva = address - module.Base;
        for (int i = 0; i < count; i++)
        {
            int at = i * 40; uint start = BitConverter.ToUInt32(sections, at + 12), size = BitConverter.ToUInt32(sections, at + 8);
            if (rva >= start && rva - start < size && (BitConverter.ToUInt32(sections, at + 36) & 0x20000000) != 0) return true;
        }
        return false;
    }
    private string Text(Module module, uint rva)
    {
        var bytes = new List<byte>();
        for (uint i = 0; i < 256; i++) { byte b = At(module, checked(rva + i), 1)[0]; if (b == 0) return Encoding.ASCII.GetString(bytes.ToArray()); bytes.Add(b); }
        throw new InvalidDataException("Public loader export name exceeds bounds");
    }
    private ulong Export(Module module, string name, HashSet<string> seen)
    {
        if (seen.Count >= 16 || !seen.Add(module.Path + "!" + name)) throw new InvalidDataException("Public loader forwarder cycle");
        var (h, _) = Header(module); uint rva = BitConverter.ToUInt32(h, 136), size = BitConverter.ToUInt32(h, 140);
        if (rva == 0 || size < 40 || (ulong)rva + size > (ulong)module.Size) throw new InvalidDataException("Public loader export directory invalid");
        byte[] e = At(module, rva, 40); uint functions = BitConverter.ToUInt32(e, 20), count = BitConverter.ToUInt32(e, 24);
        if (functions is 0 or > 65536 || count > 65536) throw new InvalidDataException("Public loader export count exceeds bounds");
        uint? index = null;
        if (name.StartsWith('#') && uint.TryParse(name.AsSpan(1), out uint ordinal)) index = checked(ordinal - BitConverter.ToUInt32(e, 16));
        else
        {
            byte[] names = At(module, BitConverter.ToUInt32(e, 32), checked((int)count * 4));
            byte[] ordinals = At(module, BitConverter.ToUInt32(e, 36), checked((int)count * 2));
            int low = 0, high = (int)count - 1;
            while (low <= high)
            {
                int i = low + (high - low) / 2;
                int order = string.CompareOrdinal(Text(module, BitConverter.ToUInt32(names, i * 4)), name);
                if (order == 0) { index = BitConverter.ToUInt16(ordinals, i * 2); break; }
                if (order < 0) low = i + 1; else high = i - 1;
            }
        }
        if (index is null || index >= functions) throw new InvalidDataException($"Public loader export missing: {name}");
        uint target = BitConverter.ToUInt32(At(module, checked(BitConverter.ToUInt32(e, 28) + index.Value * 4), 4));
        if (target >= rva && target - rva < size)
        {
            string forward = Text(module, target); int split = forward.LastIndexOf('.');
            if (split <= 0 || split == forward.Length - 1) throw new InvalidDataException("Public loader forwarder invalid");
            return Export(Find(forward[..split] + ".dll"), forward[(split + 1)..], seen);
        }
        if (target == 0 || target >= module.Size || !ExecutableSection(module, module.Base + target)) throw new InvalidDataException("Public loader export is not executable code");
        return module.Base + target;
    }
    internal void Verify()
    {
        Module[] current = modules();
        foreach (var module in initial.Where(m => snapshots.Any(s => s.Region.Allocation == m.Base)))
            if (!current.Contains(module)) throw new InvalidDataException("Public loader module identity changed");
        foreach (var s in snapshots)
            if (query(s.At) != s.Region || !read(s.At, s.Bytes.Length).AsSpan().SequenceEqual(s.Bytes))
                throw new InvalidDataException("Public loader bytes/protection changed before invocation");
    }
    internal static PublicLibraryLoader Inspect(IntPtr handle, int pid) => new((at, size) =>
    {
        byte[] data = new byte[size];
        if (!ReadProcessMemory(handle, (IntPtr)unchecked((long)at), data, (UIntPtr)size, out var read) || read.ToUInt64() != (ulong)size) throw new Win32Exception(Marshal.GetLastWin32Error());
        return data;
    }, at =>
    {
        byte[] data = new byte[48];
        if (VirtualQueryEx(handle, (IntPtr)unchecked((long)at), data, (UIntPtr)48).ToUInt64() != 48) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(BitConverter.ToUInt64(data), BitConverter.ToUInt64(data, 8), BitConverter.ToUInt64(data, 24), BitConverter.ToUInt32(data, 32), BitConverter.ToUInt32(data, 36), BitConverter.ToUInt32(data, 40));
    }, () => { using var process = Process.GetProcessById(pid); return process.Modules.Cast<ProcessModule>().Select(m => new Module(m.FileName, (ulong)m.BaseAddress.ToInt64(), m.ModuleMemorySize)).ToArray(); });
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] data, UIntPtr size, out UIntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr VirtualQueryEx(IntPtr process, IntPtr address, byte[] data, UIntPtr size);
}

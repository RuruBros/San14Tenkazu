using System.Runtime.InteropServices;
using System.Text.Json;
using San14Tenkazu;

bool appearance = args.Contains("--appearance");
string outputRoot = appearance ? ".cache/appearance-check" : ".cache/revival-check";
var game = GameMemory.FindGame();
if (!game.Verified) throw new Exception("Verified game required");
var layout = GameMemory.Resolve(game, null, CancellationToken.None);
var data = GameMemory.ReadSnapshot(game, layout);
if (GameMemory.ReadPace(game, layout) != GamePace.Paused) throw new Exception("Pause the game before capture");
using var memory = new Reader(game.ProcessId, game.ModuleBase);
ulong raw = BitConverter.ToUInt64(memory.Read(game.ModuleBase + 0x277DF18, 8));
var rows = new List<object>();
int year = data.Date!.Value.Year + 10; // Explicit emulator test input, never written to the running game.
var inactive = data.Officers.Where(o => o.Status == (appearance ? 7 : 9) &&
    (BitConverter.ToUInt16(o.Data, 0x15E) & 0x4000) == 0).Take(3).ToArray();
foreach (var officer in inactive)
foreach (var mode in new[] { RevivalMode.Serving, RevivalMode.Free })
{
    var targets = OfficerRevival.Targets(data, mode);
    var selected = targets.Where(t => t.Base <= GameMemory.CityCount).Take(1)
        .Concat(targets.Where(t => t.Base >= GameMemory.GateFirst).Take(1));
    foreach (var target in selected)
    {
        var request = new OfficerRevival.Request(officer.Id, mode, target.Base, year);
        var prepared = GameMemory.PrepareRevival(memory, game, layout, data, request);
        rows.Add(new { id = officer.Id, officer.Slot, officer.Name, address = officer.Address,
            originalStatus = officer.Status, birthYear = officer[OfficerFields.Birth], debutYear = officer[OfficerFields.Debut], mode = (int)mode,
            home = target.Base, year, body = Convert.ToHexString(prepared.Body),
            guards = prepared.Guards.Select(g => new { address = g.Address, hex = Convert.ToHexString(g.Expected), nonzero = g.Nonzero }),
            changes = prepared.Changes.Select(s => s is TransferStore store ? (object)new { address = store.Address, hex = Convert.ToHexString(store.Value) }
                : new { function = ((TransferCall)s).Invocation.Function, rcx = ((TransferCall)s).Invocation.Rcx, rdx = ((TransferCall)s).Invocation.Rdx }),
            expected = prepared.Plan.Edits.Single().Changes.Select(c => new { offset = c.Field.Offset, hex = Convert.ToHexString(c.Field.Encode(c.Value)) }) });
    }
}
if (rows.Count == 0) throw new Exception("No inactive officers or destinations captured");
var capturedClock = memory.Read(layout.State + 0x34, 5);
if (!capturedClock.AsSpan().SequenceEqual(data.Date.Value is var date
    ? new byte[] { (byte)date.Year, (byte)(date.Year >> 8), (byte)date.Month, (byte)date.Day, (byte)data.Hour } : []))
    throw new Exception("Clock changed during capture");
var output = new { pid = game.ProcessId, exe = game.ModuleBase, raw, state = layout.State,
    date = new { year = data.Date.Value.Year, month = data.Date.Value.Month, day = data.Date.Value.Day }, hour = data.Hour,
    classes = new { game.Classes.City, game.Classes.Officer, game.Classes.Gate, game.Classes.Corps, game.Classes.Force },
    corpsForce = Convert.ToHexString(data.CorpsForce),
    cities = data.Cities.Select(c => new { id = c.CityId, address = c.Address, hex = Convert.ToHexString(c.Data) }),
    gates = data.Gates.Select(c => new { id = c.Id, address = c.Address, hex = Convert.ToHexString(c.Data) }),
    officers = data.Officers.Select(o => new { slot = o.Slot, address = o.Address, hex = Convert.ToHexString(o.Data) }),
    forces = data.ForceRecords.Values.Select(f => new { id = f.Id, address = f.Address, hex = Convert.ToHexString(f.Data) }),
    rows, reads = memory.Reads.Select(r => new { address = r.Key, hex = Convert.ToHexString(r.Value) }) };
Directory.CreateDirectory(outputRoot);
File.WriteAllText(Path.Combine(outputRoot, "input.json"), JsonSerializer.Serialize(output));
Console.WriteLine(JsonSerializer.Serialize(new { game.ProcessId, date = output.date, data.Hour,
    officers = inactive.Select(o => new { o.Id, o.Name, o.Status }), cases = rows.Count, reads = memory.Reads.Count, gameWrites = false }));

sealed class Reader : IRecordMemory, IDisposable
{
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint flags, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, UIntPtr length, out UIntPtr got);
    readonly IntPtr handle;
    internal readonly Dictionary<ulong, byte[]> Reads = [];
    internal Reader(int pid, ulong exe)
    {
        handle = OpenProcess(0x410, false, pid);
        if (handle == IntPtr.Zero) throw new Exception("Read access denied");
        ulong header = (exe + 0x27D10E0) & ~0xFFFUL;
        int offset = (int)(exe + 0x27D10E0 - header);
        for (int attempt = 0; attempt < 30; ++attempt)
        {
            byte[] before = NativeRead(header, 0x1000);
            ulong Q(int at) => BitConverter.ToUInt64(before, offset + at);
            ulong start = Math.Min(Q(8), Q(0x50)) & ~0xFFFUL;
            ulong end = new[] { Q(8) + Q(0x38) * 24, Q(0x50) + Q(0x80) * 24,
                Q(0x10) + Q(0x40) * 8, Q(0x18) + Q(0x40) * 8, Q(0x28) + Q(0x40) * 8 }.Max();
            end = (end + 0xFFF) & ~0xFFFUL;
            if (end <= start || end - start > 32 * 1024 * 1024) throw new Exception("Unexpected list pool dimensions");
            byte[] pool = NativeRead(start, (int)(end - start));
            if (!before.AsSpan(offset, 0x88).SequenceEqual(NativeRead(header, 0x1000).AsSpan(offset, 0x88))) continue;
            Reads.Add(header, before); Reads.Add(start, pool); return;
        }
        throw new Exception("Could not freeze a stable native list pool");
    }
    public byte[] Read(ulong address, int length)
    {
        foreach (var r in Reads) if (r.Key <= address && address + (ulong)length <= r.Key + (ulong)r.Value.Length)
            return r.Value.AsSpan((int)(address - r.Key), length).ToArray();
        byte[] bytes = NativeRead(address, length);
        Reads.Add(address, bytes);
        return bytes.ToArray();
    }
    private byte[] NativeRead(ulong address, int length)
    {
        byte[] bytes = new byte[length];
        if (!ReadProcessMemory(handle, (IntPtr)(long)address, bytes, (UIntPtr)length, out var got) || got.ToUInt64() != (ulong)length)
            throw new Exception($"Read failed at {address:X}");
        return bytes;
    }
    public void Write(ulong address, byte[] bytes) => throw new Exception("Live writes forbidden");
    public void Dispose() => CloseHandle(handle);
}

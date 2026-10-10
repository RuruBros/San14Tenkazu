using System.Runtime.InteropServices;
using System.Text.Json;
using San14Tenkazu;

string outputRoot = ".cache/status-change-check";
var game = GameMemory.FindGame();
if (!game.Verified) throw new Exception("Verified game required");
var layout = GameMemory.Resolve(game, null, CancellationToken.None);
if (GameMemory.ReadPace(game, layout) != GamePace.Paused) throw new Exception("Pause the game before capture");
var data = GameMemory.ReadSnapshot(game, layout);
using var memory = new CaptivityReader(game.ProcessId);
ulong raw = BitConverter.ToUInt64(memory.Read(game.ModuleBase + 0x277DF18, 8));
var rows = new List<object>();
var targets = OfficerCaptivity.Targets(data);
var officers = data.Officers.Where(o => o.Id > 0 && o.Status is >= 2 and <= 6)
    .GroupBy(o => (o.Status, Player: data.ForceOfCorps(o.Corps) == data.PlayerForce))
    .Select(g => g.FirstOrDefault(o => targets.Any(t => !OfficerCaptivity.Plan(data, new(o.Id, t.Force)).Refused)))
    .OfType<OfficerRecord>().ToArray();
foreach (var officer in officers)
foreach (var target in targets.Where(t => !OfficerCaptivity.Plan(data, new(officer.Id, t.Force)).Refused).Take(2))
{
    var request = new OfficerCaptivity.Request(officer.Id, target.Force);
    var prepared = GameMemory.PrepareCaptivity(memory, game, layout, data, request);
    rows.Add(new { id = officer.Id, officer.Name, address = officer.Address, originalStatus = officer.Status,
        originalForce = data.ForceOfCorps(officer.Corps), captor = target.Force, home = target.Base, place = prepared.Place,
        body = Convert.ToHexString(prepared.Body),
        guards = prepared.Guards.Select(g => new { address = g.Address, hex = Convert.ToHexString(g.Expected) }) });
}
if (rows.Count == 0) throw new Exception("No capture requests could be prepared");
var output = new { pid = game.ProcessId, exe = game.ModuleBase, raw, state = layout.State, data.PlayerForce,
    date = new { year = data.Date!.Value.Year, month = data.Date.Value.Month, day = data.Date.Value.Day }, hour = data.Hour,
    classes = new { game.Classes.City, game.Classes.Officer, game.Classes.Gate, game.Classes.Corps, game.Classes.Force },
    corpsForce = Convert.ToHexString(data.CorpsForce),
    cities = data.Cities.Select(c => new { id = c.CityId, address = c.Address, hex = Convert.ToHexString(c.Data) }),
    gates = data.Gates.Select(c => new { id = c.Id, address = c.Address, hex = Convert.ToHexString(c.Data),
        building = c.Building is { } b ? new { id = b.Index, address = b.Address, hex = Convert.ToHexString(b.Data) } : null }),
    counties = data.Counties.Select(c => new { id = c.Index, address = c.Address, hex = Convert.ToHexString(c.Data) }),
    tileOwners = Convert.ToHexString(data.TileOwners), tileCounties = data.TileCounties,
    officers = data.Officers.Select(o => new { slot = o.Slot, address = o.Address, hex = Convert.ToHexString(o.Data) }),
    forces = data.ForceRecords.Values.Select(f => new { id = f.Id, address = f.Address, hex = Convert.ToHexString(f.Data) }),
    rows, reads = memory.Reads.Select(r => new { address = r.Key, hex = Convert.ToHexString(r.Value) }) };
Directory.CreateDirectory(outputRoot);
File.WriteAllText(Path.Combine(outputRoot, "input.json"), JsonSerializer.Serialize(output));
Console.WriteLine(JsonSerializer.Serialize(new { game.ProcessId, output.PlayerForce, output.date, data.Hour,
    officers = officers.Select(o => new { o.Id, o.Name, o.Status, force = data.ForceOfCorps(o.Corps) }),
    cases = rows.Count, reads = memory.Reads.Count, gameWrites = false }));

sealed class CaptivityReader : IRecordMemory, IDisposable
{
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint flags, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, UIntPtr length, out UIntPtr got);
    private readonly IntPtr handle;
    internal readonly Dictionary<ulong, byte[]> Reads = [];
    internal CaptivityReader(int pid)
    {
        handle = OpenProcess(0x410, false, pid);
        if (handle == IntPtr.Zero) throw new Exception("Read access denied");
    }
    public byte[] Read(ulong address, int length)
    {
        byte[] bytes = new byte[length];
        if (!ReadProcessMemory(handle, (IntPtr)(long)address, bytes, (UIntPtr)length, out var got) || got.ToUInt64() != (ulong)length)
            throw new Exception($"Unreadable {address:X}/{length}");
        if (Reads.TryGetValue(address, out var old) && !old.AsSpan(0, Math.Min(old.Length, length)).SequenceEqual(bytes.AsSpan(0, Math.Min(old.Length, length))))
            throw new Exception($"Input changed during capture: {address:X}");
        if (!Reads.TryGetValue(address, out old) || old.Length < bytes.Length) Reads[address] = bytes;
        return bytes;
    }
    public void Write(ulong address, byte[] bytes) => throw new Exception("Read-only capture must not write");
    public void Dispose() => CloseHandle(handle);
}

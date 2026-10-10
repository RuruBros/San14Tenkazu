using System.Runtime.InteropServices;
using System.Text.Json;
using San14Tenkazu;

// Read-only live capture. All overrides are explicit copied inputs for Native replay.
var game = GameMemory.FindGame();
var layout = GameMemory.Resolve(game, null, CancellationToken.None);
if (GameMemory.ReadPace(game, layout) != GamePace.Paused) throw new Exception("Pause the game before capture.");
var data = GameMemory.ReadSnapshot(game, layout);
using var memory = new Reader(game.ProcessId);
ulong raw = BitConverter.ToUInt64(memory.Read(game.ModuleBase + 0x277DF18, 8));
var rows = new List<object>();
foreach (OfficerRecord original in data.Officers.Where(p => p.Status == 1))
{
    int force = data.ForceOfCorps(original.Corps);
    var cities = data.Cities.Where(c => c.CityId != original.BaseId && data.ForceOfCorps(c.Corps) == force).ToArray();
    foreach (CityRecord city in cities.GroupBy(c => c.Corps == original.Corps).Select(g => g.First()))
        Emit(data, original, city.CityId, "actual");
    if (force == data.PlayerForce && cities.Length == 0)
    {
        CityRecord city = data.Cities.First(c => c.CityId is >= 1 and <= 46 && c.CityId != original.BaseId &&
            data.ForceOfCorps(c.Corps) > 0 && !data.Officers.Any(p => p.Status == 1 && p.BaseId == c.CityId));
        var corpsField = new FieldDef("corps", RecordKind.City, CityFields.CorpsOffset, FieldKind.UInt16, 0, 51);
        memory.Overrides[city.Address + (ulong)CityFields.CorpsOffset] = corpsField.Encode(original.Corps);
        GameSnapshot owned = data.With([new(RecordKind.City, city.Address, city.Data, "copied player city", [new(corpsField, original.Corps)])]);
        Emit(owned, original, city.CityId, "copied-player-city");
        memory.Overrides.Clear();
        cities = [owned.Cities[city.CityId]];
    }
    // Retain the live player's ruler and create only the delegated destination input when none exists.
    if (force == data.PlayerForce && cities.Length > 0 && cities.All(c => c.Corps == original.Corps))
    {
        var unused = data.CorpsRecords.Values.First(c => c.Id > 0 && c.Force == 0);
        int delegateId = data.Officers.First(p => p.Status == 4 && data.ForceOfCorps(p.Corps) == force).Id;
        memory.Overrides[unused.Address + 0x10] = [(byte)force, 2];
        memory.Overrides[unused.Address + 0x12] = BitConverter.GetBytes((ushort)delegateId);
        memory.Overrides[cities[0].Address + (ulong)CityFields.CorpsOffset] = BitConverter.GetBytes((ushort)unused.Id);
        OfficerRecord leader = data.OfficerById[delegateId];
        memory.Overrides[leader.Address + (ulong)OfficerFields.CorpsOffset] = [(byte)unused.Id];
        memory.Overrides[leader.Address + (ulong)OfficerFields.StatusOffset] = [2];
        GameSnapshot copy = data.With([new(RecordKind.City, cities[0].Address, cities[0].Data, "delegated destination",
            [new(new("corps", RecordKind.City, CityFields.CorpsOffset, FieldKind.UInt16, 0, 51), unused.Id)])]);
        byte beforeForce = copy.CorpsForce[unused.Id];
        copy.CorpsForce[unused.Id] = (byte)force;
        try { Emit(copy, original, cities[0].CityId, "copied-delegated-city"); }
        finally { copy.CorpsForce[unused.Id] = beforeForce; }
        memory.Overrides.Clear();
    }
}
void Emit(GameSnapshot snapshot, OfficerRecord officer, int home, string scenario)
{
    var plan = Transfers.Plan(snapshot, officer, Transfer.Move, home);
    if (plan.Refused) { rows.Add(new { officer.Id, officer.Name, home, scenario, refused = plan.Refusal }); return; }
    try
    {
        var p = GameMemory.PrepareTransfer(memory, game, layout, snapshot, officer, Transfer.Move, home, plan);
        rows.Add(new
        {
            officer.Id, officer.Name, home, scenario, address = officer.Address, oldCorps = officer.Corps,
            force = snapshot.ForceOfCorps(officer.Corps), player = snapshot.ForceOfCorps(officer.Corps) == snapshot.PlayerForce,
            destinationCorps = snapshot.Cities[home].Corps, forceAddress = snapshot.ForceRecords[snapshot.ForceOfCorps(officer.Corps)].Address,
            corpsAddress = layout.Corps[officer.Corps], baseAddress = snapshot.Cities[home].Address,
            body = Convert.ToHexString(p.Body), guards = p.Guards.Select(g => new { address = g.Address, hex = Convert.ToHexString(g.Expected) }),
            checks = p.Checks.OfType<TransferCall>().Select(c => new { function = c.Invocation.Function, rcx = c.Invocation.Rcx,
                rdx = c.Invocation.Rdx, more = c.Invocation.More, corpsAt = c.CorpsAt, zero = c.MustReturnZero }),
            changes = p.Changes.Select(s => s is TransferStore v ? (object)new { address = v.Address, hex = Convert.ToHexString(v.Value) }
                : new { function = ((TransferCall)s).Invocation.Function, rcx = ((TransferCall)s).Invocation.Rcx,
                    rdx = ((TransferCall)s).Invocation.Rdx, more = ((TransferCall)s).Invocation.More, corpsAt = ((TransferCall)s).CorpsAt,
                    rdxFromReturn = ((TransferCall)s).RdxFromReturn }),
            units = p.Units.Select(u => new { address = u.Address, slot = u.Slot, tile = u.Tile,
                place = officer[OfficerFields.PlaceField] }), evidence = p.Evidence,
            inputChanges = memory.Overrides.Select(r => new { address = r.Key, hex = Convert.ToHexString(r.Value) }).ToArray()
        });
    }
    catch (Exception ex) { rows.Add(new { officer.Id, officer.Name, home, scenario, refused = ex.Message }); }
}
var after = GameMemory.ReadSnapshot(game, layout);
if (data.Date != after.Date || data.Hour != after.Hour || !data.Officers.Zip(after.Officers).All(p => p.First.Data.SequenceEqual(p.Second.Data)))
    throw new Exception("Game changed during read-only capture.");
Directory.CreateDirectory(".cache/ruler-transfer-check");
File.WriteAllText(".cache/ruler-transfer-check/input.json", JsonSerializer.Serialize(new
{
    exe = game.ModuleBase, raw, state = layout.State, date = data.Date is { } date ? new { date.Year, date.Month, date.Day } : null,
    hour = data.Hour, gameWrites = false, clockStable = true,
    rows, reads = memory.Reads.Select(r => new { address = r.Key, hex = Convert.ToHexString(r.Value) })
}));
Console.WriteLine(JsonSerializer.Serialize(new { gameWrites = false, clockStable = true, date = data.Date?.ToString(), rows = rows.Count }));

sealed class Reader : IRecordMemory, IDisposable
{
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint flags, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, UIntPtr length, out UIntPtr got);
    readonly IntPtr handle;
    internal Dictionary<ulong, byte[]> Reads = [];
    internal Dictionary<ulong, byte[]> Overrides = [];
    internal Reader(int pid) { handle = OpenProcess(0x410, false, pid); }
    public byte[] Read(ulong address, int length)
    {
        byte[] Patch(byte[] b)
        {
            foreach (var r in Overrides)
            {
                ulong lo = Math.Max(address, r.Key), hi = Math.Min(address + (ulong)length, r.Key + (ulong)r.Value.Length);
                if (lo < hi) r.Value.AsSpan((int)(lo - r.Key), (int)(hi - lo)).CopyTo(b.AsSpan((int)(lo - address)));
            }
            return b;
        }
        foreach (var r in Reads)
            if (r.Key <= address && address + (ulong)length <= r.Key + (ulong)r.Value.Length)
                return Patch(r.Value.AsSpan((int)(address - r.Key), length).ToArray());
        var b = new byte[length];
        if (!ReadProcessMemory(handle, (IntPtr)(long)address, b, (UIntPtr)length, out var got) || got.ToUInt64() != (ulong)length)
            throw new Exception($"read {address:X} {length}");
        Reads[address] = b; return Patch(b.ToArray());
    }
    public void Write(ulong address, byte[] bytes) => throw new Exception("Live writes forbidden");
    public void Dispose() => CloseHandle(handle);
}

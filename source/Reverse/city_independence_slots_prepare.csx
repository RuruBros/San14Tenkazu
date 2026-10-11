using System.Text.Json;
using San14Tenkazu;

// Generate the actual product preflight/frame bytes from authenticated frozen
// records. There is no process access or record mutation in this exporter.
using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
var root = document.RootElement;
ulong U(string key) => root.GetProperty(key).GetUInt64();
var cities = root.GetProperty("cities").EnumerateArray().Select(r =>
    new CityRecord(r.GetProperty("index").GetInt32(), r.GetProperty("address").GetUInt64(), Convert.FromHexString(r.GetProperty("hex").GetString()!))).ToArray();
var officers = root.GetProperty("officers").EnumerateArray().Select(r =>
    new OfficerRecord(r.GetProperty("index").GetInt32(), r.GetProperty("address").GetUInt64(), Convert.FromHexString(r.GetProperty("hex").GetString()!))).ToArray();
var forces = root.GetProperty("forces").EnumerateArray().Select(r =>
    new ForceRecord(r.GetProperty("index").GetInt32(), r.GetProperty("address").GetUInt64(), Convert.FromHexString(r.GetProperty("hex").GetString()!))).ToArray();
var corps = root.GetProperty("corps").EnumerateArray().Select(r =>
    new CorpsRecord(r.GetProperty("index").GetInt32(), r.GetProperty("address").GetUInt64(), Convert.FromHexString(r.GetProperty("hex").GetString()!))).ToArray();
var units = root.GetProperty("units").EnumerateArray().Select(r =>
    new UnitRecord(r.GetProperty("index").GetInt32(), r.GetProperty("address").GetUInt64(), Convert.FromHexString(r.GetProperty("hex").GetString()!), 0)).ToArray();
ulong V(byte[] bytes) => BitConverter.ToUInt64(bytes);
var classes = new GameClasses(V(cities[28].Data), 0, V(officers[997].Data), 0, 0, V(forces[3].Data), V(corps[3].Data), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
var identity = new GameIdentity(0, DateTime.UnixEpoch, U("exe"), root.GetProperty("input_sha256").GetString()!, true, classes);
var tables = new GameTables(U("raw") + 0xDD38, U("raw") + 0xDF30, U("raw") + 0xE0D0, U("raw") + 0xE270,
    0, 0, U("raw") + 0x73A50, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
var layout = new GameLayout(tables, cities[0].Address, 0, 0, officers[0].Address, officers.Length, 0, 0, 0,
    forces.Select(f => f.Address).ToArray(), corps.Select(c => c.Address).ToArray(), 0, units.Length, U("state"));
var snapshot = new GameSnapshot { Cities = cities, Counties = [], Officers = officers, CastleByCity = [], TileOwners = [], TileCounties = [],
    Forces = forces.ToDictionary(f => f.Id, f => new ForceInfo(f.Id, f.Ruler, 0, "")),
    ForceRecords = forces.ToDictionary(f => f.Id), CorpsRecords = corps.ToDictionary(c => c.Id),
    CorpsForce = corps.Select(c => (byte)c.Force).ToArray(),
    OfficerById = officers.Where(o => o.Id > 0).GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First()), Units = units };
var memory = new FrozenReads(root.GetProperty("reads"));
var rows = new List<object>();
foreach (ForceRecord force in forces.Where(f => f.Id is >= 1 and <= 45 && f.Ruler == 0))
{
    var request = new CityIndependence.Request(28, 997, 3, force.Id, cities[28].Address, snapshot.OfficerById[997].Address, force.Id);
    byte[] body = GameMemory.PrepareExplicitIndependence(memory, identity, layout, snapshot, request);
    rows.Add(new { slot = force.Id, body = Convert.ToHexString(body), forceAddress = force.Address,
        governorAddress = request.GovernorAddress, cityAddress = request.CityAddress,
        frame = Convert.ToHexString(GameMemory.FrameStub(0x6000000000, 0x6000003000, 0x6000000400, 0, 0, 0)) });
}
File.WriteAllText(args[1], JsonSerializer.Serialize(new { input_sha256 = root.GetProperty("input_sha256").GetString(), rows }));
Console.WriteLine($"Prepared {rows.Count} exact product bodies from frozen records.");

sealed class FrozenReads
    : IRecordMemory
{
    private readonly (ulong Address, byte[] Bytes)[] blocks;
    public FrozenReads(JsonElement reads) => blocks = reads.EnumerateArray().Select(r =>
        (r.GetProperty("address").GetUInt64(), Convert.FromHexString(r.GetProperty("hex").GetString()!))).ToArray();
    public byte[] Read(ulong address, int length)
    {
        foreach (var block in blocks)
            if (address >= block.Address && address + (ulong)length <= block.Address + (ulong)block.Bytes.Length)
                return block.Bytes.AsSpan((int)(address - block.Address), length).ToArray();
        throw new InvalidOperationException($"Missing frozen read {address:X} ({length}).");
    }
    public void Write(ulong address, byte[] data) => throw new InvalidOperationException("Frozen writes forbidden.");
}

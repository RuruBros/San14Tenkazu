using System.Runtime.InteropServices;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Formulas;

var game = GameMemory.FindGame();
var layout = GameMemory.Resolve(game, null, CancellationToken.None);
var data = GameMemory.ReadSnapshot(game, layout);
using var memory = new Reader(game.ProcessId);
ulong raw = BitConverter.ToUInt64(memory.Read(game.ModuleBase + 0x277DF18, 8));
World w = TransferRouting.Read(memory, raw);
var rows = new List<object>();
var routes = new List<object>();
foreach (UnitRecord unit in data.Units)
foreach (int home in new[] { 6, 7, 10 })
{
    int force = Map.BaseForce(w, Map.Base(w, home)!);
    var path = TransferRouting.Find(w, unit, force, home);
    routes.Add(new { slot=unit.Slot, address=unit.Address, commander=unit.Commander, kind=unit.Kind, unit=Convert.ToHexString(unit.Data), home, force, count=path.Count });
}
foreach (int id in data.Officers.Where(p=>p.Status==2).Select(p=>p.Id).Concat(data.Units.Select(u => u.Commander)).Distinct())
{
    OfficerRecord officer = data.OfficerById[id];
    foreach (int home in new[] { 6, 10 })
        Emit(data, officer, home, []);
}
// Explicit copied inputs: make the currently travelling leaders resident, and put one transport
// physically on its target city. These are emulator fixtures, never live gameplay writes.
foreach (OfficerRecord original in data.Officers.Where(p=>p.Status==2))
{
    var change=new RecordEdit(RecordKind.Officer, original.Address, original.Data, original.Name,[new(OfficerFields.PlaceField,original.BaseId)]);
    GameSnapshot copy=data.With([change]);
    memory.Overrides[original.Address+(ulong)OfficerFields.PlaceField.Offset]=OfficerFields.PlaceField.Encode(original.BaseId);
    foreach(int home in new[]{6,10})Emit(copy,copy.OfficerById[original.Id],home,memory.Overrides.Select(r=>new{address=r.Key,hex=Convert.ToHexString(r.Value)}).ToArray());
    memory.Overrides.Clear();
}
UnitRecord transport=data.Units.First(u=>u.Kind==2 && data.ForceOfCorps(data.OfficerById[u.Commander].Corps)!=data.ForceOfCity(6));
int tile=Map.BaseHex(w,Map.Base(w,6)!);
var tileField=new FieldDef("tile",RecordKind.Unit,0x2A,FieldKind.UInt16,0,48400);
var unitEdit=new RecordEdit(RecordKind.Unit,transport.Address,transport.Data,"transport",[new(tileField,tile)]);
GameSnapshot arrival=data.With([unitEdit]);
memory.Overrides[transport.Address+0x2A]=tileField.Encode(tile);
Emit(arrival,arrival.OfficerById[transport.Commander],6,memory.Overrides.Select(r=>new{address=r.Key,hex=Convert.ToHexString(r.Value)}).ToArray());
memory.Overrides.Clear();

UnitRecord commanded=data.Units.First(u=>u.Kind==1);
OfficerRecord commander=data.OfficerById[commanded.Commander];
Rec commandedCorps=w.Table(Tables.Corps)[commander.Corps];
OfficerRecord leader=data.OfficerById[commandedCorps.U16(0x12)];
GameSnapshot leaderUnit=data.With([
    new RecordEdit(RecordKind.Officer,commander.Address,commander.Data,commander.Name,[new(OfficerFields.StatusField,2)]),
    new RecordEdit(RecordKind.Officer,leader.Address,leader.Data,leader.Name,[new(OfficerFields.StatusField,4)])]);
memory.Overrides[commander.Address+(ulong)OfficerFields.StatusOffset]=[2];
memory.Overrides[leader.Address+(ulong)OfficerFields.StatusOffset]=[4];
memory.Overrides[commandedCorps.NativeAddress+0x12]=BitConverter.GetBytes((ushort)commander.Id);
Emit(leaderUnit,leaderUnit.OfficerById[commander.Id],6,memory.Overrides.Select(r=>new{address=r.Key,hex=Convert.ToHexString(r.Value)}).ToArray());
memory.Overrides.Clear();

int group=BitConverter.ToUInt16(commanded.Data,0x58);
ulong troop=BitConverter.ToUInt64(memory.Read(raw+(ulong)Tables.Troops+(ulong)group*8,8));
memory.Overrides[troop+0x2C]=BitConverter.GetBytes((ushort)commander.Id);
UnitRecord remaining=data.Units.First(u=>u.Address!=commanded.Address && data.ForceOfCorps(data.OfficerById[u.Commander].Corps)==data.ForceOfCorps(commander.Corps));
var groupField=new FieldDef("group",RecordKind.Unit,0x58,FieldKind.UInt16,0,500);
memory.Overrides[remaining.Address+0x58]=groupField.Encode(group);
GameSnapshot grouped=data.With([new RecordEdit(RecordKind.Unit,remaining.Address,remaining.Data,"remaining",[new(groupField,group)])]);
Emit(grouped,grouped.OfficerById[commander.Id],6,memory.Overrides.Select(r=>new{address=r.Key,hex=Convert.ToHexString(r.Value)}).ToArray());
memory.Overrides.Clear();

void Emit(GameSnapshot data, OfficerRecord officer, int home, object[] inputChanges)
{
        int id=officer.Id;
        int force = Map.BaseForce(w, Map.Base(w, home)!);
        Transfer kind = force == data.ForceOfCorps(officer.Corps) ? Transfer.Move : Transfer.Defect;
        var plan = Transfers.Plan(data, officer, kind, home);
        if (plan.Refused) { rows.Add(new{id,name=officer.Name,home,force,refused=plan.Refusal});return; }
        if (!Transfers.RequiresNative(data, officer, kind)) return;
        try
        {
            var p = GameMemory.PrepareTransfer(memory, game, layout, data, officer, kind, home, plan);
            rows.Add(new { id, name=officer.Name, home, force, address=officer.Address, oldCorps=officer.Corps,
                body=Convert.ToHexString(p.Body), guards=p.Guards.Select(g=>new {address=g.Address,hex=Convert.ToHexString(g.Expected)}),
                checks=p.Checks.OfType<TransferCall>().Select(c=>new{function=c.Invocation.Function,rcx=c.Invocation.Rcx,rdx=c.Invocation.Rdx,more=c.Invocation.More,corpsAt=c.CorpsAt}),
                changes=p.Changes.Select(s=>s is TransferStore v ? (object)new{address=v.Address,hex=Convert.ToHexString(v.Value)} : new{function=((TransferCall)s).Invocation.Function,rcx=((TransferCall)s).Invocation.Rcx,rdx=((TransferCall)s).Invocation.Rdx,more=((TransferCall)s).Invocation.More,corpsAt=((TransferCall)s).CorpsAt}),
                units=p.Units.Select(u => new { address=u.Address, slot=u.Slot, tile=u.Tile }), evidence=p.Evidence, inputChanges });
        }
        catch (Exception ex) { rows.Add(new { id, name=officer.Name, home, force, refused=ex.Message }); }
}
File.WriteAllText(".cache/transfer-check/input.json", JsonSerializer.Serialize(new { exe=game.ModuleBase, raw, state=layout.State, date=$"{data.Date?.Year}-{data.Date?.Month}-{data.Date?.Day}", hour=data.Hour, rows, routes, reads=memory.Reads.Select(r => new { address=r.Key, hex=Convert.ToHexString(r.Value) }) }));
Console.WriteLine(JsonSerializer.Serialize(new { date=data.Date, hour=data.Hour, units=data.Units.Length, rows=rows.Count, reads=memory.Reads.Count }));

sealed class Reader : IRecordMemory, IDisposable
{
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint flags, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, UIntPtr length, out UIntPtr got);
    readonly IntPtr handle;
    internal Dictionary<ulong, byte[]> Reads = [];
    internal Dictionary<ulong, byte[]> Overrides = [];
    internal Reader(int pid) { handle=OpenProcess(0x410,false,pid); }
    public byte[] Read(ulong address, int length)
    {
        byte[] Patch(byte[] b) { foreach(var r in Overrides){ulong lo=Math.Max(address,r.Key),hi=Math.Min(address+(ulong)length,r.Key+(ulong)r.Value.Length);if(lo<hi)r.Value.AsSpan((int)(lo-r.Key),(int)(hi-lo)).CopyTo(b.AsSpan((int)(lo-address)));}return b; }
        foreach (var r in Reads) if (r.Key<=address && address+(ulong)length<=r.Key+(ulong)r.Value.Length) return Patch(r.Value.AsSpan((int)(address-r.Key),length).ToArray());
        var b=new byte[length];
        if (!ReadProcessMemory(handle,(IntPtr)(long)address,b,(UIntPtr)length,out var got) || got.ToUInt64()!=(ulong)length) throw new Exception($"read {address:X} {length}");
        Reads[address]=b; return Patch(b.ToArray());
    }
    public void Write(ulong address, byte[] bytes) => throw new Exception("Live writes forbidden");
    public void Dispose() => CloseHandle(handle);
}

using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// Native object allocation and graphical bindings are supplied by the host. The gameplay ID map,
// free-ID chain, active membership and pending-delete membership follow the command manager itself.
internal sealed class CommandStorage(World w, IDictionary<int, CommandCancellation.Command> byId,
    List<CommandCancellation.Command> active, List<CommandCancellation.Command> pending,
    ushort[] freeIds, ushort freeHead, Action<CommandCancellation.Command> detachUi,
    Action<CommandCancellation.Command> dispose)
{
    internal const int Capacity = 0x73C;
    internal ushort FreeHead { get; private set; } = freeHead;
    internal IDictionary<int, CommandCancellation.Command> ById => byId;
    internal List<CommandCancellation.Command> Active => active;
    internal List<CommandCancellation.Command> Pending => pending;
    internal ushort[] FreeIds => freeIds;

    private void SyncActive() => w.CmdActions = active.Select(c => (c.Kind, c.Officers.ToList())).ToList();

    internal void Refresh() => SyncActive();

    // 0x228710: valid IDs 1..6000 erase every integer-list alias, without cancelling or deleting the command.
    internal void RemoveOfficer(CommandCancellation.Command command, int id)
    {
        if ((uint)(id - 1) >= 6000) return;
        command.Officers = command.Officers.Where(member => member != id).ToArray();
        SyncActive();
    }

    // 0x1DE1A0: pop the head before testing it, then insert the map and active list before setting +0x58.
    internal bool Register(CommandCancellation.Command command)
    {
        int id = FreeHead;
        if (id < Capacity) FreeHead = freeIds[id];
        if (id >= Capacity) return false;
        // std::map insertion retains an existing key; normal free IDs cannot already be registered.
        byId.TryAdd(id, command);
        active.Add(command);
        BinaryPrimitives.WriteInt32LittleEndian(command.Data.Bytes.AsSpan(0x58), id);
        SyncActive();
        return true;
    }

    // 0x1EC7D0: ID removal and free-slot release precede graphical detachment and object destruction.
    // Valid registration assigns IDs 0..1851. A negative ID would overwrite manager header bytes natively.
    private void Destroy(CommandCancellation.Command command)
    {
        int id = command.Data.I32(0x58);
        if (id < 0) throw new NotSupportedException("A negative command ID corrupts the native manager header.");
        byId.Remove(id);
        if (id < Capacity)
        {
            freeIds[id] = FreeHead;
            FreeHead = (ushort)id;
        }
        detachUi(command);
        dispose(command);
    }

    // 0x1F22C0: every nonzero mode destroys immediately; zero queues the object. Both erase all pointer aliases.
    internal void Remove(CommandCancellation.Command? command, int immediate)
    {
        if (command is null) return;
        if (immediate == 0) pending.Add(command);
        else Destroy(command);
        active.RemoveAll(c => c.Data.NativeAddress == command.Data.NativeAddress);
        SyncActive();
    }

    internal void Apply(CommandCancellation.RemovalRequest request) => Remove(request.Source, request.Immediate ? 1 : 0);

    // 0x22AA70 scans active commands in list order, ignores ID -1, and preserves first-match behavior.
    internal CommandCancellation.Command? FindOfficer(int id) => active.FirstOrDefault(c =>
        c.Data.I32(0x58) != -1 && c.Officers.Contains(id));
}

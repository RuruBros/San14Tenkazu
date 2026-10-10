using System.Buffers.Binary;

namespace San14Tenkazu;

internal sealed record PreparedRevival(OfficerRevival.Request Request, OfficerRecord Officer, EditPlan Plan,
    IReadOnlyList<TransferGuard> Guards, IReadOnlyList<TransferStep> Changes)
{
    internal byte[] Body => TransferFrame.Build(Guards, [], Changes);
}

internal static partial class GameMemory
{
    internal const ulong OfficerListAddRva = 0x31F4F0;
    private static readonly byte[] OfficerListAddStart = Convert.FromHexString(
        "48895C2408574883EC20488BDA488BF94885D20F84E6000000488B02488BCAFF50");

    internal static PreparedRevival PrepareRevival(GameIdentity identity, GameLayout layout, GameSnapshot data,
        OfficerRevival.Request request)
    {
        RequireRevivalGame(identity, layout);
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return PrepareRevival(new ProcessMemory(handle), identity, layout, data, request); }
        finally { CloseHandle(handle); }
    }

    private static void RequireRevivalGame(GameIdentity identity, GameLayout layout)
    {
        if (!identity.Verified) throw new InvalidOperationException(L.Calls.WhyUnverified);
        if (!IsSameGame(identity) || !layout.SameStorage(ReadCurrentLayout(identity)))
            throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        if (ReadPace(identity, layout) != GamePace.Paused) throw new InvalidOperationException(L.Transfers.PauseRequired);
    }

    // The same preflight executes on read-only captured memory in the native replay checks.
    internal static PreparedRevival PrepareRevival(IRecordMemory memory, GameIdentity identity, GameLayout layout,
        GameSnapshot data, OfficerRevival.Request request)
    {
        if (!identity.Verified) throw new InvalidOperationException(L.Calls.WhyUnverified);
        EditPlan plan = OfficerRevival.Plan(data, request);
        if (plan.Refused) throw new InvalidOperationException(plan.Refusal);
        OfficerRecord officer = data.OfficerById[request.Officer];
        var guards = new List<TransferGuard>();
        void Guard(ulong address, byte[] expected)
        {
            if (!memory.Read(address, expected.Length).AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException(L.Transfers.StateChanged);
            guards.Add(new(address, expected));
        }
        ulong Q(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(address, 8));
        ulong raw = Q(identity.ModuleBase + BasePointerRva);
        if (raw == 0 || Q(raw + 0x871E8) != layout.State || raw + 0xE270 != layout.Tables.Tile)
            throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        Guard(identity.ModuleBase + BasePointerRva, BitConverter.GetBytes(raw));
        Guard(raw + 0x871E8, BitConverter.GetBytes(layout.State));
        // Guard the state header and clock; RequireRevivalGame independently checks pause before arming.
        byte[] clock = memory.Read(layout.State + 0x24, 0x18);
        if (data.Date is not { } date || BinaryPrimitives.ReadUInt16LittleEndian(clock.AsSpan(0x10)) != date.Year ||
            clock[0x12] != date.Month || clock[0x13] != date.Day)
            throw new InvalidOperationException(L.Transfers.StateChanged);
        Guard(layout.State + 0x24, clock);
        Guard(officer.Address, officer.Data);
        if (Q(raw + 0x158 + (ulong)officer.Id * 8) != officer.Address ||
            Q(officer.Address) != identity.Classes.Officer || Q(raw + 0x73A50) == officer.Address)
            throw new InvalidOperationException(L.Transfers.StateChanged);
        Guard(raw + 0x158 + (ulong)officer.Id * 8, BitConverter.GetBytes(officer.Address));
        Guard(identity.Classes.Officer + 0x18, BitConverter.GetBytes(identity.ModuleBase + 0x2341F0));
        Guard(identity.ModuleBase + OfficerListAddRva, OfficerListAddStart);

        // The destination and its owner must still be the records used to make the plan.
        bool city = request.Base <= CityCount;
        ulong targetAddress;
        byte[] targetData;
        if (city) { var target = data.Cities[request.Base]; targetAddress = target.Address; targetData = target.Data; }
        else { var target = data.Gates.Single(g => g.Id == request.Base); targetAddress = target.Address; targetData = target.Data; }
        ulong targetSlot = raw + (city ? 0xDD38UL + (ulong)request.Base * 8 : 0xDED8UL + (ulong)(request.Base - 51) * 8);
        Guard(targetSlot, BitConverter.GetBytes(targetAddress));
        Guard(targetAddress, targetData.AsSpan(0, 0x52).ToArray());
        if (Q(targetAddress) != (city ? identity.Classes.City : identity.Classes.Gate))
            throw new InvalidOperationException(L.Transfers.DestinationInvalid);
        if (request.Mode == RevivalMode.Serving)
        {
            var target = OfficerRevival.Targets(data, request.Mode).Single(b => b.Base == request.Base);
            ulong corps = Q(raw + 0xE0D0 + (ulong)target.Corps * 8);
            byte[] corpsData = memory.Read(corps, 0x18);
            if (corpsData[0x10] != target.Force || corpsData[0x11] == 0 || Q(corps) != identity.Classes.Corps)
                throw new InvalidOperationException(L.Transfers.DestinationChanged);
            Guard(raw + 0xE0D0 + (ulong)target.Corps * 8, BitConverter.GetBytes(corps));
            Guard(corps, corpsData);
            OfficerRecord ruler = data.OfficerById[data.Forces[target.Force].RulerId];
            Guard(ruler.Address, ruler.Data.AsSpan(0, 0xE7).ToArray());
            ForceRecord force = data.ForceRecords[target.Force];
            Guard(raw + 0xDF30 + (ulong)target.Force * 8, BitConverter.GetBytes(force.Address));
            Guard(force.Address, force.Data.AsSpan(0, 0x12).ToArray());
        }
        var list = ReadRevivalOfficerList(memory, identity.ModuleBase, raw, guards);
        if (list.Count(a => a == officer.Address) != 0) throw new InvalidOperationException(L.Revival.AlreadyListed);
        var changes = plan.Edits.Single().Changes.Select(c => (TransferStep)new TransferStore(
            officer.Address + (ulong)c.Field.Offset, c.Field.Encode(c.Value))).ToList();
        // The game's own add validates the officer and refuses a second alias. No native list nodes are fabricated.
        changes.Add(new TransferCall(new(identity.ModuleBase + OfficerListAddRva, raw, officer.Address)));
        return new(request, officer, plan, guards, changes);
    }

    private static IReadOnlyList<ulong> ReadRevivalOfficerList(IRecordMemory memory, ulong exe, ulong raw,
        List<TransferGuard>? guards = null)
    {
        byte[] Read(ulong address, int length, bool guard = true)
        {
            byte[] bytes = memory.Read(address, length);
            if (guard) guards?.Add(new(address, bytes));
            return bytes;
        }
        ulong Q(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(Read(address, 8));
        ulong wrapper = Q(raw + 0x100);
        if (wrapper == 0) throw new InvalidDataException(L.Revival.ListInvalid);
        uint index = BinaryPrimitives.ReadUInt32LittleEndian(Read(wrapper, 4));
        ulong poolAddress = exe + 0x27D10E0;
        byte[] pool = Read(poolAddress, 0x88, guard: false);
        ulong P(int at) => BinaryPrimitives.ReadUInt64LittleEndian(pool.AsSpan(at));
        if (index >= P(0x40) || P(8) == 0 || P(0x10) == 0 || P(0x18) == 0 || P(0x28) == 0)
            throw new InvalidDataException(L.Revival.ListInvalid);
        if (guards is not null && (P(0x20) == 0 || P(0x30) >= P(0x38)))
            throw new InvalidOperationException(L.Revival.ListFull);
        // Other paused UI lists use this shared pool. Guard its storage/capacity, not their allocation counters.
        // Available nodes are tested on the executing frame before any write, even if the free head changed.
        if (guards is not null)
        {
            guards.Add(new(poolAddress, pool.AsSpan(0, 0x20).ToArray()));
            guards.Add(new(poolAddress + 0x28, pool.AsSpan(0x28, 8).ToArray()));
            guards.Add(new(poolAddress + 0x38, pool.AsSpan(0x38, 0x10).ToArray()));
            guards.Add(new(poolAddress + 0x20, [], Nonzero: true));
            guards.Add(new(poolAddress + 0x68, [], Nonzero: true));
            if (P(0x68) == 0) throw new InvalidOperationException(L.Revival.ListFull);
        }
        ulong node = Q(P(0x10) + index * 8UL), tail = Q(P(0x18) + index * 8UL), count = Q(P(0x28) + index * 8UL);
        if (count > 6000 || count > P(0x38)) throw new InvalidDataException(L.Revival.ListInvalid);
        var seen = new HashSet<ulong>();
        var officers = new List<ulong>();
        ulong previous = 0;
        while (node != 0)
        {
            if (node < P(8) || node >= P(8) + P(0x38) * 24 || (node - P(8)) % 24 != 0 ||
                !seen.Add(node) || seen.Count > (int)count)
                throw new InvalidDataException(L.Revival.ListInvalid);
            byte[] bytes = Read(node, 24);
            if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16)) != previous)
                throw new InvalidDataException(L.Revival.ListInvalid);
            officers.Add(BinaryPrimitives.ReadUInt64LittleEndian(bytes));
            previous = node;
            node = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8));
        }
        if (previous != tail || (ulong)officers.Count != count) throw new InvalidDataException(L.Revival.ListInvalid);
        return officers;
    }

    internal static IReadOnlyList<string> ApplyRevival(GameIdentity identity, GameLayout layout, GameSnapshot data,
        OfficerRevival.Request request)
    {
        PreparedRevival prepared = PrepareRevival(identity, layout, data, request);
        RequireRevivalGame(identity, layout);
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try
        {
            byte[] body = prepared.Body;
            (string? why, int result) = RunOnFrame(handle, identity, _ => new(identity.ModuleBase + OfficerListAddRva,
                OfficerListAddStart, 0, 0, [], NeedsPlayer: false, Body: body), codePages: (body.Length + 0x400 + StubPage - 1) / StubPage);
            if (why is not null) throw new InvalidOperationException(L.Transfers.ExecutionUnconfirmed(why));
            if (result != 1) throw new InvalidOperationException(L.Transfers.WriteStateChanged);
            var memory = new ProcessMemory(handle);
            byte[] fresh = memory.Read(prepared.Officer.Address, prepared.Officer.Data.Length);
            ulong raw = BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(identity.ModuleBase + BasePointerRva, 8));
            int count = ReadRevivalOfficerList(memory, identity.ModuleBase, raw).Count(a => a == prepared.Officer.Address);
            if (count != 1 || prepared.Plan.Edits.Single().Changes.Any(c => c.Field.Read(fresh) != c.Value))
                throw new InvalidOperationException(L.Revival.Unconfirmed);
            return [L.Revival.Evidence(prepared.Officer.Name, OfficerFields.StatusName(fresh[OfficerFields.StatusOffset]),
                fresh[OfficerFields.CorpsOffset], OfficerFields.BaseField.Read(fresh), OfficerFields.Death.Read(fresh), count)];
        }
        finally { CloseHandle(handle); }
    }
}

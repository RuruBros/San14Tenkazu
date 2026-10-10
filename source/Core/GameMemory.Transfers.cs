using System.Buffers.Binary;
using San14Tenkazu.Formulas;

namespace San14Tenkazu;

internal sealed record PreparedTransfer(OfficerRecord Officer, int Home, int Force, EditPlan Plan,
    IReadOnlyList<UnitRecord> Units, IReadOnlyList<TransferGuard> Guards,
    IReadOnlyList<TransferStep> Checks, IReadOnlyList<TransferStep> Changes, IReadOnlyList<string> Evidence)
{
    internal byte[] Body => TransferFrame.Build(Guards, Checks, Changes);
}

internal static partial class GameMemory
{
    private static readonly IReadOnlyDictionary<ulong, byte[]> TransferStarts = new Dictionary<ulong, byte[]>
    {
        [0x233440] = Convert.FromHexString("48895C2410564883EC50488B15C7AA540233DB488BF1488B42704885C0742548"),
        [0x241C90] = Convert.FromHexString("40534883EC20440FB7495841BAF4010000488B0570C25302453BCA770A4A8B9C"),
        [0x240CB0] = Convert.FromHexString("40534883EC200FB681E00000004889742430488BF148897C24384863FA3BF80F"),
        [0x2408E0] = Convert.FromHexString("83FA0477210FB681500100000FB3D08881500100004585C0740C0FB6C00FABD0"),
        [0x2EA1B0] = Convert.FromHexString("48895C241855565741544155415641574881EC90090000488B05C2197A014833"),
        [0x2E8E70] = Convert.FromHexString("488BC4554154415541564157488BEC4881EC8000000048C745A0FEFFFFFF4889"),
        [0x29EDE0] = Convert.FromHexString("44894424185556574154415541564157488D6C24D94881ECC000000048C745FF"),
        [0x228710] = Convert.FromHexString("40564883EC208BF2488BD18D46FF3D6F1700000F878C000000488B412048"),
        [0x240C90] = Convert.FromHexString("85D27905C6413000C3B8330000003BD00F4FD0885130C3CCCCCCCCCCCCCC"),
        [0x2827C0] = Convert.FromHexString("4C8BDC565741564881EC8000000049C74388FEFFFFFF49895B0849896B18"),
        [0x26CC10] = Convert.FromHexString("48895C24084889742410574883EC20488BFA488BF1E836DFFBFF488BC848"),
    };

    internal static PreparedTransfer PrepareTransfer(GameIdentity identity, GameLayout layout, GameSnapshot data,
        OfficerRecord officer, Transfer kind, int home, EditPlan plan)
    {
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        if (!identity.Verified) throw new InvalidOperationException(L.Calls.WhyUnverified);
        if (ReadPace(identity, layout) != GamePace.Paused)
            throw new InvalidOperationException(L.Transfers.PauseRequired);
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Transfers.ReadAccessDenied);
        try { return PrepareTransfer(new ProcessMemory(handle), identity, layout, data, officer, kind, home, plan); }
        finally { CloseHandle(handle); }
    }

    // Read-only preflight, shared by the live process and captured-memory integration checks.
    internal static PreparedTransfer PrepareTransfer(IRecordMemory memory, GameIdentity identity, GameLayout layout,
        GameSnapshot data, OfficerRecord officer, Transfer kind, int home, EditPlan plan)
    {
        if (plan.Refused || !Transfers.RequiresNative(data, officer, kind)) throw new ArgumentException("Native transfer required.");
        var guards = new List<TransferGuard>();
        var checks = new List<TransferStep>();
        var changes = new List<TransferStep>();
        var evidence = new List<string>();
        ulong exe = identity.ModuleBase;
        ulong Q(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(address, 8));
        void Guard(ulong address, byte[] expected)
        {
            if (!memory.Read(address, expected.Length).AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException(L.Transfers.StateChanged);
            guards.Add(new(address, expected));
        }
        void RecordGuard(ulong address, byte[] bytes, int offset, int size) => Guard(address + (ulong)offset, bytes.AsSpan(offset, size).ToArray());
        void Store(ulong address, byte[] value) => changes.Add(new TransferStore(address, value));
        void Call(ulong rva, ulong rcx, ulong rdx, params ulong[] more) => changes.Add(new TransferCall(new(exe + rva, rcx, rdx, more)));
        foreach (RecordEdit edit in plan.Edits)
        {
            ValidateSameRecord(identity.Classes, edit, memory.Read(edit.Address, edit.Expected.Length));
            RecordGuard(edit.Address, edit.Expected, 0, 8);
            (int start, int length) = IdentityOf(edit.Kind);
            RecordGuard(edit.Address, edit.Expected, start, length);
            foreach (FieldDef field in LinkedFields.Of(edit.Kind).Concat(edit.Changes.Select(c => c.Field)).Distinct())
                RecordGuard(edit.Address, edit.Expected, field.Offset, field.Size);
        }
        ulong raw = Q(exe + BasePointerRva);
        if (raw == 0 || raw + (ulong)Tables.Tiles != layout.Tables.Tile || Q(raw + 0x871E8) != layout.State)
            throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        Guard(exe + BasePointerRva, BitConverter.GetBytes(raw));
        Guard(raw + 0x871E8, BitConverter.GetBytes(layout.State));
        Guard(layout.State + 0x24, memory.Read(layout.State + 0x24, 0x18));
        World world = TransferRouting.Read(memory, raw);
        Rec destination = Map.Base(world, home) ?? throw new InvalidOperationException(L.Transfers.DestinationMissing);
        int corps = destination.U16(0x30), force = Map.BaseForce(world, destination);
        if (!Objects.Valid(world, destination) || corps is < 1 or > 51 || force is < 1 or > MaxPlayerForce)
            throw new InvalidOperationException(L.Transfers.DestinationInvalid);
        int plannedCorps = (int)plan.Edits.First(e => e.Kind == RecordKind.Officer).Changes
            .Where(c => c.Field == OfficerFields.CorpsField).Select(c => c.Value).DefaultIfEmpty(officer.Corps).First();
        bool ruler = officer.Status == 1;
        if ((!ruler && corps != plannedCorps) || data.ForceOfCorps(corps) != force ||
            ruler && (kind != Transfer.Move || force != data.ForceOfCorps(officer.Corps) ||
                home is not (>= 1 and <= CityCount)))
            throw new InvalidOperationException(L.Transfers.DestinationChanged);
        RecordGuard(destination.NativeAddress, destination.Bytes, 0, 8);
        RecordGuard(destination.NativeAddress, destination.Bytes, 0x2E, 4);
        RecordGuard(destination.NativeAddress, destination.Bytes, 0x4E, 2);
        RecordGuard(destination.NativeAddress, destination.Bytes, 0x69, 1);
        Rec targetCorps = world.Table(Tables.Corps)[corps];
        Guard(raw + (ulong)Tables.Corps + (ulong)corps * 8, BitConverter.GetBytes(targetCorps.NativeAddress));
        RecordGuard(targetCorps.NativeAddress, targetCorps.Bytes, 0, 0x18);
        if (ruler)
        {
            Rec own = world.Table(Tables.Corps)[officer.Corps];
            Rec lord = world.Table(Tables.Forces)[force];
            if (own.U16(0x12) != officer.Id || lord.U16(0x10) != officer.Id || own.U8(0x10) != force)
                throw new InvalidOperationException(L.Transfers.StateChanged);
            Guard(raw + 0x158 + (ulong)officer.Id * 8, BitConverter.GetBytes(officer.Address));
            Guard(raw + (ulong)Tables.Officers + (ulong)officer.Slot * 8, BitConverter.GetBytes(officer.Address));
            Guard(raw + (ulong)Tables.Cities + (ulong)home * 8, BitConverter.GetBytes(destination.NativeAddress));
            Guard(raw + (ulong)Tables.Corps + (ulong)officer.Corps * 8, BitConverter.GetBytes(own.NativeAddress));
            Guard(raw + (ulong)Tables.Forces + (ulong)force * 8, BitConverter.GetBytes(lord.NativeAddress));
            RecordGuard(own.NativeAddress, own.Bytes, 0, 0x18);
            RecordGuard(lord.NativeAddress, lord.Bytes, 0, 0x12);
        }

        UnitRecord[] units = data.Units.Where(u => u.Commander == officer.Id).ToArray();
        if (units.Length > 1) throw new InvalidOperationException(L.Transfers.MultipleUnits);
        foreach (UnitRecord unit in units)
        {
            ValidateSameRecord(identity.Classes, new(RecordKind.Unit, unit.Address, unit.Data, officer.Name, []), memory.Read(unit.Address, unit.Data.Length));
            RecordGuard(unit.Address, unit.Data, 0, 0x5C);
            RecordGuard(unit.Address, unit.Data, 0x13E, 0x1C);
            if (unit.Data[0x25] != 0 || unit.Data[0x56] != 0 || unit.Data[0x140] != 0)
                throw new InvalidOperationException(L.Transfers.UnitBusy(officer.Name));
            checks.Add(new TransferCall(new(exe + 0x233440, unit.Address, 0), MustReturnZero: true));
            IReadOnlyList<int> route = TransferRouting.Find(world, unit, force, home);
            if (route.Count == 0)
                throw new InvalidOperationException(L.Transfers.NoRoute(officer.Name, CityNames.BaseName(home)));
            evidence.Add(L.Transfers.RouteEvidence(officer.Name, unit.Slot, unit.Tile, route[^1], route.Count - 1, force));
        }

        // Clear old posts before native succession; do not erase a governor the game subsequently appoints.
        foreach (RecordEdit edit in plan.Edits.Where(e => e.Kind != RecordKind.Officer))
            foreach (FieldChange change in edit.Changes) Store(edit.Address + (ulong)change.Field.Offset, change.Field.Encode(change.Value));
        if (officer.Status == 2)
        {
            Rec old = world.Table(Tables.Corps)[officer.Corps];
            int oldForce = old.U8(0x10);
            // Match 230410's serving/counting predicate, excluding the temporarily detached leader.
            OfficerRecord? survivor = data.Officers.FirstOrDefault(p => p.Id != officer.Id && p.Status is >= 1 and <= 4 &&
                data.ForceOfCorps(p.Corps) == oldForce && OfficerCapture.CountedOfficer(world, new Rec(p.Slot, p.Data, Tables.Officers, p.Address)));
            if (old.U16(0x12) != officer.Id || !data.Cities.Any(c => data.ForceOfCorps(c.Corps) == oldForce) || survivor is null)
                throw new InvalidOperationException(L.Transfers.CorpsSurvivalUnknown);
            Guard(raw + (ulong)Tables.Corps + (ulong)officer.Corps * 8, BitConverter.GetBytes(old.NativeAddress));
            RecordGuard(old.NativeAddress, old.Bytes, 0, 0x18);
            RecordGuard(survivor.Address, survivor.Data, 0x10, 2);
            RecordGuard(survivor.Address, survivor.Data, 0xE0, 7);
            RecordGuard(survivor.Address, survivor.Data, 0x15E, 2);
            Rec survivorCorps = world.Table(Tables.Corps)[survivor.Corps];
            RecordGuard(survivorCorps.NativeAddress, survivorCorps.Bytes, 0, 0x18);
            CityRecord survivingCity = data.Cities.First(c => data.ForceOfCorps(c.Corps) == oldForce);
            RecordGuard(survivingCity.Address, survivingCity.Data, CityFields.CorpsOffset, 2);
            Store(old.NativeAddress + 0x12, new byte[2]);
            Store(officer.Address + OfficerFields.StatusOffset, [5]);
            Call(0x2E8E70, old.NativeAddress, 0, 1);
            evidence.Add(L.Transfers.SuccessionEvidence(officer.Corps));
        }
        // A same-force corps merge may change the destination's corps during succession.
        if (ruler)
        {
            // Compose 2DAE80's membership updates without constructing its personal travel command.
            // Travel detaches an active commander, sets unit+150 bit 1 and prevents Unit_SendHome.
            Rec origin = Map.Base(world, officer.BaseId) ?? throw new InvalidOperationException(L.Transfers.StateChanged);
            RecordGuard(origin.NativeAddress, origin.Bytes, 0, 0x32);
            Call(0x228710, origin.NativeAddress, (ulong)officer.Id);
            if (corps != officer.Corps) Call(0x240C90, destination.NativeAddress, (ulong)officer.Corps);
            Call(0x240CB0, officer.Address, (ulong)officer.Corps, 0);
        }
        else changes.Add(new TransferCall(new(exe + 0x240CB0, officer.Address, (ulong)corps, [0]), CorpsAt: destination.NativeAddress + 0x30));
        foreach (RecordEdit edit in plan.Edits.Where(e => e.Kind == RecordKind.Officer))
            foreach (FieldChange change in edit.Changes.Where(c => c.Field != OfficerFields.CorpsField))
                Store(edit.Address + (ulong)change.Field.Offset, change.Field.Encode(change.Value));

        if (ruler)
        {
            // 2DB0C5..2DB1C5: choose governors and then reconcile every corps of the force.
            // SelectGovernor's returned pointer must reach AssignGovernor on this same game frame.
            foreach (Rec post in world.Table(Tables.Cities).Skip(1).Concat(world.Table(Tables.Gates).Skip(1))
                .Where(b => Objects.Valid(world, b) && Map.BaseForce(world, b) == force))
            {
                RecordGuard(post.NativeAddress, post.Bytes, 0, 8);
                RecordGuard(post.NativeAddress, post.Bytes, 0x2E, 4);
                Call(0x2827C0, post.NativeAddress, 0);
                changes.Add(new TransferCall(new(exe + 0x26CC10, post.NativeAddress, 0), RdxFromReturn: true));
            }
            foreach (Rec district in world.Table(Tables.Corps).Skip(1).Where(c => c.U8(0x10) == force && Objects.CorpsValid(c)))
            {
                RecordGuard(district.NativeAddress, district.Bytes, 0, 0x18);
                Call(0x2E8E70, district.NativeAddress, 0, 0);
            }
        }

        foreach (UnitRecord unit in units)
        {
            int group = BinaryPrimitives.ReadUInt16LittleEndian(unit.Data.AsSpan(0x58));
            if (group is >= 1 and <= 500)
            {
                ulong troop = Q(raw + (ulong)Tables.Troops + (ulong)group * 8);
                byte[] troopData = memory.Read(troop, 0x40);
                Guard(raw + (ulong)Tables.Troops + (ulong)group * 8, BitConverter.GetBytes(troop));
                Guard(troop, troopData);
                if (BinaryPrimitives.ReadUInt16LittleEndian(troopData.AsSpan(0x2C)) == officer.Id)
                {
                    UnitRecord? next = ActiveUnits(memory, exe, raw).FirstOrDefault(u => u.Address != unit.Address &&
                        BinaryPrimitives.ReadUInt16LittleEndian(u.Data.AsSpan(0x58)) == group);
                    if (next is not null)
                    {
                        RecordGuard(next.Address, next.Data, 0, 0x14);
                        RecordGuard(next.Address, next.Data, 0x58, 2);
                        checks.Add(new TransferCall(new(exe + 0x233440, next.Address, 0), MustReturnZero: true));
                        if (next.Commander < 6000) Store(troop + 0x2C, BitConverter.GetBytes((ushort)next.Commander));
                    }
                }
            }
            Call(0x241C90, unit.Address, 0);
            Call(0x2408E0, unit.Address, 3, 0);
            if (unit.Tile == Map.BaseHex(world, destination)) Call(0x29EDE0, unit.Address, destination.NativeAddress, 0);
            else Call(0x2EA1B0, unit.Address, 0, 0, 0);
        }
        return new(officer, home, force, plan, units, guards, checks, changes, evidence);
    }

    private static IEnumerable<UnitRecord> ActiveUnits(IRecordMemory memory, ulong exe, ulong raw)
    {
        ulong Q(ulong at) => BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(at, 8));
        ulong wrapper = Q(raw + 0x60);
        if (wrapper == 0) throw new InvalidDataException(L.Transfers.UnitListMissing);
        uint index = BinaryPrimitives.ReadUInt32LittleEndian(memory.Read(wrapper, 4));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(memory.Read(exe + 0x27D1120, 4));
        ulong heads = Q(exe + 0x27D10F0);
        if (index >= count || heads == 0) throw new InvalidDataException(L.Transfers.UnitListChanged);
        var visited = new HashSet<ulong>();
        for (ulong node = Q(heads + index * 8); node != 0; node = Q(node + 8))
        {
            if (!visited.Add(node) || visited.Count > 501) throw new InvalidDataException(L.Transfers.UnitListMismatch);
            ulong unit = Q(node);
            byte[] bytes = memory.Read(unit, 0x200);
            yield return new(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)), unit, bytes, 0);
        }
    }

    internal static IReadOnlyList<string> ApplyTransfer(GameIdentity identity, GameLayout layout, GameSnapshot data,
        OfficerRecord officer, Transfer kind, int home, EditPlan plan)
    {
        // Repeat the read-only route check after confirmation, immediately before arming the native frame.
        PreparedTransfer prepared = PrepareTransfer(identity, layout, data, officer, kind, home, plan);
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try
        {
            ulong exe = identity.ModuleBase;
            FrameFollowup[] functions = prepared.Checks.Concat(prepared.Changes).OfType<TransferCall>()
                .Select(c => c.Invocation.Function).Distinct().Select(f => new FrameFollowup(new(f, 0, 0), TransferStarts[f - exe])).ToArray();
            (string? why, int result) = RunOnFrame(handle, identity, _ => new(exe + 0x240CB0, TransferStarts[0x240CB0],
                0, 0, [], NeedsPlayer: false, After: functions, Body: prepared.Body), codePages: 8);
            if (why is not null) throw new InvalidOperationException(L.Transfers.ExecutionUnconfirmed(why));
            if (result != 1) throw new InvalidOperationException(L.Transfers.WriteStateChanged);
            byte[] fresh = ReadBytes(handle, officer.Address, officer.Data.Length);
            if (officer.Status == 1 && (fresh[OfficerFields.StatusOffset] != 1 || fresh[OfficerFields.CorpsOffset] != officer.Corps ||
                OfficerFields.BaseField.Read(fresh) != home ||
                prepared.Units.Count == 0 && OfficerFields.PlaceField.Read(fresh) != home ||
                BitConverter.ToUInt16(ReadBytes(handle, layout.Forces[prepared.Force] + ForceFields.RulerOffset, 2)) != officer.Id ||
                BitConverter.ToUInt16(ReadBytes(handle, layout.Corps[officer.Corps] + 0x12, 2)) != officer.Id ||
                BitConverter.ToUInt16(ReadBytes(handle, data.Cities[home].Address + (ulong)CityFields.CorpsOffset, 2)) != officer.Corps))
                throw new InvalidOperationException(L.Transfers.ExecutionUnconfirmed(L.Transfers.StateChanged));
            var report = prepared.Evidence.ToList();
            report.Add(L.Transfers.AppliedOfficerEvidence(officer.Name, fresh[OfficerFields.CorpsOffset], OfficerFields.BaseField.Read(fresh), fresh[OfficerFields.StatusOffset]));
            foreach (UnitRecord unit in prepared.Units)
            {
                byte[] bytes = ReadBytes(handle, unit.Address, unit.Data.Length);
                report.Add(L.Transfers.AppliedUnitEvidence(unit.Slot, bytes[0x37], BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x3A)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x48)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x58))));
            }
            return report;
        }
        finally { CloseHandle(handle); }
    }
}

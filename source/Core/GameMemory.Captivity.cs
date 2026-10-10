using System.Buffers.Binary;
using San14Tenkazu.Formulas;

namespace San14Tenkazu;

internal sealed record PreparedCaptivity(OfficerCaptivity.Request Request, OfficerRecord Officer,
    OfficerCaptivity.Target Target, int Place, EditPlan Plan, IReadOnlyList<TransferGuard> Guards,
    IReadOnlyList<TransferStep> Changes)
{
    internal byte[] Body => TransferFrame.Build(Guards, [], Changes);
}

internal static partial class GameMemory
{
    internal const ulong OfficerCaptureRva = 0x2E0CF0;
    private static readonly byte[] OfficerCaptureStart = Convert.FromHexString(
        "44894C24205355564881ECA0000000418BD8488BEA488BF1E8F3E7040085C075");

    private static void RequireCaptivityGame(GameIdentity identity, GameLayout layout)
    {
        if (!identity.Verified) throw new InvalidOperationException(L.Calls.WhyUnverified);
        if (!IsSameGame(identity) || !layout.SameStorage(ReadCurrentLayout(identity)))
            throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        if (ReadPace(identity, layout) != GamePace.Paused) throw new InvalidOperationException(L.Transfers.PauseRequired);
    }

    internal static PreparedCaptivity PrepareCaptivity(GameIdentity identity, GameLayout layout, GameSnapshot data,
        OfficerCaptivity.Request request)
    {
        RequireCaptivityGame(identity, layout);
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return PrepareCaptivity(new ProcessMemory(handle), identity, layout, data, request); }
        finally { CloseHandle(handle); }
    }

    // Shared with read-only native replay: every guard runs before the capture call on the same game frame.
    internal static PreparedCaptivity PrepareCaptivity(IRecordMemory memory, GameIdentity identity, GameLayout layout,
        GameSnapshot data, OfficerCaptivity.Request request)
    {
        if (!identity.Verified) throw new InvalidOperationException(L.Calls.WhyUnverified);
        EditPlan plan = OfficerCaptivity.Plan(data, request);
        if (plan.Refused) throw new InvalidOperationException(plan.Refusal);
        var guards = new List<TransferGuard>();
        ulong Q(ulong address) => BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(address, 8));
        void Guard(ulong address, byte[] bytes)
        {
            if (!memory.Read(address, bytes.Length).AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException(L.Transfers.StateChanged);
            guards.Add(new(address, bytes));
        }
        ulong raw = Q(identity.ModuleBase + BasePointerRva);
        if (raw == 0 || raw + (ulong)Tables.Tiles != layout.Tables.Tile || Q(raw + 0x871E8) != layout.State)
            throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        Guard(identity.ModuleBase + BasePointerRva, BitConverter.GetBytes(raw));
        Guard(raw + 0x871E8, BitConverter.GetBytes(layout.State));
        byte[] clock = memory.Read(layout.State + 0x24, 0x18);
        if (data.Date is not { } date || BinaryPrimitives.ReadUInt16LittleEndian(clock.AsSpan(0x10)) != date.Year
            || clock[0x12] != date.Month || clock[0x13] != date.Day)
            throw new InvalidOperationException(L.Transfers.StateChanged);
        Guard(layout.State + 0x24, clock);
        Guard(identity.ModuleBase + OfficerCaptureRva, OfficerCaptureStart);
        OfficerRecord officer = data.OfficerById[request.Officer];
        OfficerCaptivity.Target target = OfficerCaptivity.Targets(data).Single(t => t.Force == request.Force);
        ForceRecord captor = data.ForceRecords[target.Force];
        OfficerRecord ruler = data.OfficerById[captor.Ruler];
        void Person(OfficerRecord person)
        {
            Guard(raw + 0x158 + (ulong)person.Id * 8, BitConverter.GetBytes(person.Address));
            Guard(person.Address, person.Data);
            if (Q(person.Address) != identity.Classes.Officer || Q(raw + 0x73A50) == person.Address)
                throw new InvalidOperationException(L.Transfers.StateChanged);
        }
        Person(officer); Person(ruler);
        Guard(raw + (ulong)Tables.Forces + (ulong)target.Force * 8, BitConverter.GetBytes(captor.Address));
        Guard(captor.Address, captor.Data);
        if (Q(captor.Address) != identity.Classes.Force) throw new InvalidOperationException(L.Transfers.DestinationInvalid);
        foreach (int corps in new[] { officer.Corps, ruler.Corps }.Distinct())
        {
            if (corps is < 0 or > 51) throw new InvalidOperationException(L.Transfers.StateChanged);
            ulong slot = raw + (ulong)Tables.Corps + (ulong)corps * 8, address = Q(slot);
            Guard(slot, BitConverter.GetBytes(address));
            byte[] bytes = memory.Read(address, 0x30);
            if (bytes[0x10] != data.ForceOfCorps(corps)) throw new InvalidOperationException(L.Transfers.StateChanged);
            Guard(address, bytes);
            if (data.ForceRecords.TryGetValue(bytes[0x10], out var force))
            {
                Guard(raw + (ulong)Tables.Forces + (ulong)force.Id * 8, BitConverter.GetBytes(force.Address));
                Guard(force.Address, force.Data);
            }
        }
        bool city = target.Base <= CityCount;
        ulong baseAddress = city ? data.Cities[target.Base].Address : data.Gates.Single(g => g.Id == target.Base).Address;
        byte[] baseData = city ? data.Cities[target.Base].Data : data.Gates.Single(g => g.Id == target.Base).Data;
        Guard(raw + (city ? (ulong)Tables.Cities + (ulong)target.Base * 8
            : (ulong)Tables.Gates + (ulong)(target.Base - 51) * 8), BitConverter.GetBytes(baseAddress));
        Guard(baseAddress, baseData.AsSpan(0, 0x70).ToArray());
        if (Q(baseAddress) != (city ? identity.Classes.City : identity.Classes.Gate))
            throw new InvalidOperationException(L.Transfers.DestinationInvalid);
        // 0x22A7B0 resolves even a city's parent through its building, tile and county.
        int buildingId = BinaryPrimitives.ReadUInt16LittleEndian(baseData.AsSpan(0x4E));
        if (buildingId >= MaxBuildings) throw new InvalidOperationException(L.Transfers.DestinationInvalid);
        ulong buildingSlot = raw + (ulong)Tables.Buildings + (ulong)buildingId * 8, building = Q(buildingSlot);
        Guard(buildingSlot, BitConverter.GetBytes(building));
        byte[] buildingData = memory.Read(building, 0x20);
        Guard(building, buildingData);
        int tile = BinaryPrimitives.ReadUInt16LittleEndian(buildingData.AsSpan(BuildingTileOffset));
        if (tile >= TileCount) throw new InvalidOperationException(L.Transfers.DestinationInvalid);
        ulong tileSlot = raw + (ulong)Tables.Tiles + (ulong)tile * 8, tileAddress = Q(tileSlot);
        Guard(tileSlot, BitConverter.GetBytes(tileAddress));
        byte[] tileData = memory.Read(tileAddress, 0x20);
        Guard(tileAddress, tileData);
        int county = BinaryPrimitives.ReadUInt16LittleEndian(tileData.AsSpan(0x12));
        if (county > 500) throw new InvalidOperationException(L.Transfers.DestinationInvalid);
        ulong countySlot = raw + (ulong)Tables.Counties + (ulong)county * 8, countyAddress = Q(countySlot);
        Guard(countySlot, BitConverter.GetBytes(countyAddress));
        byte[] countyData = memory.Read(countyAddress, 0x98);
        Guard(countyAddress, countyData);
        int place = countyData[CountyFields.HomeCityOffset];
        if (place is < 1 or > TribeLast) throw new InvalidOperationException(L.Transfers.DestinationInvalid);
        if (!city)
        {
            // A gate's corps can retain its old owner. Guard the county-centre tile used by ForceOfGate.
            if (county == 0) throw new InvalidOperationException(L.Transfers.DestinationInvalid);
            int center = BinaryPrimitives.ReadUInt16LittleEndian(countyData.AsSpan(CountyFields.CenterTileOffset));
            if (center >= TileCount) center = 0;
            ulong centerSlot = raw + (ulong)Tables.Tiles + (ulong)center * 8, centerAddress = Q(centerSlot);
            Guard(centerSlot, BitConverter.GetBytes(centerAddress));
            byte[] centerData = memory.Read(centerAddress, 0x20);
            if (centerData[0x14] != target.Force) throw new InvalidOperationException(L.Transfers.DestinationChanged);
            Guard(centerAddress, centerData);
        }
        TransferStep[] changes = [new TransferCall(new(identity.ModuleBase + OfficerCaptureRva, officer.Address, 0,
            [0, 0, captor.Address, 0]))];
        return new(request, officer, target, place, plan, guards, changes);
    }

    internal static IReadOnlyList<string> ApplyCaptivity(GameIdentity identity, GameLayout layout, PreparedCaptivity prepared)
    {
        RequireCaptivityGame(identity, layout);
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try
        {
            byte[] body = prepared.Body;
            (string? why, int result) = RunOnFrame(handle, identity, _ => new(identity.ModuleBase + OfficerCaptureRva,
                OfficerCaptureStart, 0, 0, [], NeedsPlayer: false, Body: body), codePages: (body.Length + 0x400 + StubPage - 1) / StubPage);
            if (why is not null) throw new InvalidOperationException(L.Transfers.ExecutionUnconfirmed(why));
            if (result != 1) throw new InvalidOperationException(L.Transfers.WriteStateChanged);
            byte[] fresh = ReadBytes(handle, prepared.Officer.Address, prepared.Officer.Data.Length);
            if (fresh[OfficerFields.StatusOffset] != 6 || OfficerFields.Office.Read(fresh) != OfficerFields.NoOffice
                || OfficerFields.Captivity.Read(fresh) != 0 || OfficerFields.BaseField.Read(fresh) != prepared.Target.Base
                || OfficerFields.PlaceField.Read(fresh) != prepared.Place)
                throw new InvalidOperationException(L.StatusChange.Unconfirmed);
            return [L.StatusChange.CaptureEvidence(prepared.Officer.Name, prepared.Target.Name, prepared.Target.BaseName,
                (int)OfficerFields.PlaceField.Read(fresh), fresh[OfficerFields.CorpsOffset], (int)OfficerFields.PrisonerOf.Read(fresh))];
        }
        finally { CloseHandle(handle); }
    }
}

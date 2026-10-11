namespace San14Tenkazu;

internal static partial class GameMemory
{
    private const ulong NewForceForEditRva = 0x3BF120, NewForceEventRva = 0x39FD00, NewForceBodyRva = 0x2D7C80;
    private const ulong NewForceEventSlotRva = 0x13BAEF8;
    private static readonly byte[] NewForceForEditStart = Convert.FromHexString(
        "48895C2410564883EC204C8B02488BF2488B0DE1ED3B024963400C3D701700007F0A488B9CC158010000EB07488B99503A07004963401048897C243083F8337F0A488BBC");
    private static readonly byte[] NewForceEventStart = Convert.FromHexString(
        "48895C2408574883EC30488B02488BDA8B480CE8E8990300488B0B488BD3488BF88B4910E8E7830300488BCF488BD8E8CCF7F8FF85C07426488BCBE8C0F7F8FF85C0741A");
    private static readonly byte[] NewForceBodyStart = Convert.FromHexString(
        "40555356574154415541564157488D6C24E94881ECE800000048C745D7FEFFFFFF488B05E83E7B014833C448894507458BF144894C2440418BF84C8BFA4C8BE9");
    private static readonly byte[] IndependenceTribeStart = Convert.FromHexString(
        "4883EC280FB791E20000008D42FF83F83C770485D27520E8E4A5FFFF488BC848");
    private static readonly byte[] IndependenceSpecialStart = Convert.FromHexString(
        "48895C2408574883EC20488BD98BFA0FB74910E8583D090085C0750D33C0488B");

    // Only the verified game's native creation performs the lifecycle. No raw-field
    // fallback, renderer suppression or synthetic force/corps records are used here.
    internal static GameSnapshot MakeCityIndependent(GameIdentity identity, GameLayout layout, CityIndependence.Request request)
    {
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        if (!identity.Verified) throw new InvalidOperationException(L.Independence.NotRun(L.Calls.WhyUnverified));
        if (!layout.SameStorage(Resolve(identity, null, CancellationToken.None)))
            throw new InvalidOperationException(L.Independence.Changed);
        if (ReadPace(identity, layout) != GamePace.Paused) throw new InvalidOperationException(L.Independence.PauseFirst);
        GameSnapshot before = ReadSnapshot(identity, layout);
        if (CityIndependence.Prepare(before, request.City) != request) throw new InvalidOperationException(L.Independence.Changed);
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try
        {
            ulong exe = identity.ModuleBase;
            if (request.RequestedSlot != 0)
            {
                byte[] body = PrepareExplicitIndependence(new ProcessMemory(handle), identity, layout, before, request);
                (string? explicitWhy, int explicitResult) = RunOnFrame(handle, identity, _ => new FrameCall(exe + NewForceBodyRva,
                    NewForceBodyStart, 0, 0, [], NeedsPlayer: false, Body: body),
                    codePages: (body.Length + 0x400 + StubPage - 1) / StubPage);
                if (explicitWhy is not null) throw new InvalidOperationException(L.Independence.NotRun(explicitWhy));
                if (explicitResult == 0) throw new InvalidOperationException(L.Independence.Changed);
            }
            else
            {
                ulong instance = TryReadPointer(handle, exe + NewForceEventSlotRva) ?? 0;
                ulong vtable = instance == 0 ? 0 : TryReadPointer(handle, instance) ?? 0;
                // The edit event delegates through a static event instance's vtable.
                // Verify that indirect target as well as both native creation bodies.
                if (!CodeIs(handle, exe + NewForceEventRva, NewForceEventStart)
                    || !CodeIs(handle, exe + NewForceBodyRva, NewForceBodyStart)
                    || instance == 0 || vtable == 0
                    || TryReadPointer(handle, vtable) != exe + NewForceEventRva)
                    throw new InvalidOperationException(L.Independence.NotRun(L.Calls.WhyCode));
                // 0x2D76A0 needs a native-invalid ordinary corps (number byte 0).
                if (!layout.Corps.Skip(1).Take(MaxPlayerForce).Any(address => address != 0
                    && ReadBytes(handle, address, CorpsRead) is { } corps
                    && BitConverter.ToUInt64(corps) == identity.Classes.Corps && corps[0x11] == 0))
                    throw new InvalidOperationException(L.Independence.NoCorps);
                (string? why, int result) = RunOnFrame(handle, identity, data => new FrameCall(exe + NewForceForEditRva,
                    NewForceForEditStart, 0, data + StubArgs, IndependenceArgs(data, request.Governor, request.City), NeedsPlayer: false));
                if (why is not null) throw new InvalidOperationException(L.Independence.NotRun(why));
                if (result == 0) throw new InvalidOperationException(L.Independence.Refused);
            }
        }
        finally { CloseHandle(handle); }
        GameSnapshot after = ReadSnapshot(identity, layout);
        if (!CityIndependence.Confirmed(before, after, request)) throw new InvalidOperationException(L.Independence.Unconfirmed);
        return after;
    }

    // Read-only preflight plus frame-time guards. The fifth native argument is an
    // explicit slot, not a patched selection rule or a fabricated force record.
    internal static byte[] PrepareExplicitIndependence(IRecordMemory memory, GameIdentity identity, GameLayout layout,
        GameSnapshot before, CityIndependence.Request request)
    {
        if (!identity.Verified) throw new InvalidOperationException(L.Independence.NotRun(L.Calls.WhyUnverified));
        if (request.RequestedSlot != request.NewForce || request.NewForce is < 1 or > MaxPlayerForce
            || CityIndependence.Refusal(before, request.City) is not null
            || before.Cities[request.City].Address != request.CityAddress
            || before.Cities[request.City].Governor != request.Governor
            || before.ForceOfCity(request.City) != request.FromForce
            || before.OfficerById[request.Governor].Address != request.GovernorAddress
            || !before.ForceRecords.TryGetValue(request.NewForce, out ForceRecord? force) || force.Ruler != 0)
            throw new InvalidOperationException(L.Independence.Changed);
        ulong exe = identity.ModuleBase;
        var guards = new List<TransferGuard>();
        ulong Q(ulong address) => BitConverter.ToUInt64(memory.Read(address, 8));
        void Guard(ulong address, byte[] expected)
        {
            if (!memory.Read(address, expected.Length).AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException(L.Independence.Changed);
            guards.Add(new(address, expected));
        }
        void Class(ulong address, ulong expected)
        {
            if (Q(address) != expected) throw new InvalidOperationException(L.Independence.Changed);
        }
        ulong raw = Q(exe + BasePointerRva);
        if (raw == 0 || Q(raw + 0x871E8) != layout.State || raw + 0xE270 != layout.Tables.Tile)
            throw new InvalidOperationException(L.Independence.Changed);
        Guard(exe + BasePointerRva, BitConverter.GetBytes(raw));
        Guard(raw + 0x871E8, BitConverter.GetBytes(layout.State));
        Guard(layout.State + 0x24, memory.Read(layout.State + 0x24, 0x18));
        foreach ((ulong rva, byte[] start) in new (ulong, byte[])[] {
            (NewForceBodyRva, NewForceBodyStart), (AgreementMapRva, AgreementMapStart),
            (0x232400, IndependenceTribeStart), (0x233110, IndependenceSpecialStart) })
            Guard(exe + rva, start);
        OfficerRecord governor = before.OfficerById[request.Governor];
        CityRecord city = before.Cities[request.City];
        Class(governor.Address, identity.Classes.Officer);
        Class(city.Address, identity.Classes.City);
        Class(force.Address, identity.Classes.Force);
        Guard(raw + 0x158 + (ulong)governor.Id * 8, BitConverter.GetBytes(governor.Address));
        Guard(raw + 0xDD38 + (ulong)city.CityId * 8, BitConverter.GetBytes(city.Address));
        Guard(raw + 0xDF30 + (ulong)force.Id * 8, BitConverter.GetBytes(force.Address));
        Guard(governor.Address, governor.Data.AsSpan(0, 0x160).ToArray());
        Guard(city.Address, city.Data);
        Guard(force.Address, force.Data);
        ForceRecord originalForce = before.ForceRecords[request.FromForce];
        OfficerRecord originalRuler = BaseGrants.Ruler(before, request.FromForce)!;
        Class(originalForce.Address, identity.Classes.Force);
        Class(originalRuler.Address, identity.Classes.Officer);
        Guard(raw + 0xDF30 + (ulong)request.FromForce * 8, BitConverter.GetBytes(originalForce.Address));
        Guard(raw + 0x158 + (ulong)originalRuler.Id * 8, BitConverter.GetBytes(originalRuler.Address));
        Guard(originalForce.Address, originalForce.Data);
        Guard(originalRuler.Address, originalRuler.Data.AsSpan(0, 0xE7).ToArray());
        ulong sourceCorps = layout.Corps[city.Corps];
        Class(sourceCorps, identity.Classes.Corps);
        byte[] source = memory.Read(sourceCorps, CorpsRead);
        if (source[0x10] != request.FromForce || source[0x11] == 0)
            throw new InvalidOperationException(L.Independence.Changed);
        Guard(raw + 0xE0D0 + (ulong)city.Corps * 8, BitConverter.GetBytes(sourceCorps));
        Guard(sourceCorps, source);
        bool hasCorps = false;
        for (int i = 1; i <= MaxPlayerForce; i++)
        {
            ulong address = layout.Corps[i];
            if (address == 0) continue;
            byte[] corps = memory.Read(address, CorpsRead);
            if (BitConverter.ToUInt64(corps) != identity.Classes.Corps || corps[0x11] != 0) continue;
            Guard(raw + 0xE0D0 + (ulong)i * 8, BitConverter.GetBytes(address));
            Guard(address, corps);
            hasCorps = true;
            break;
        }
        if (!hasCorps) throw new InvalidOperationException(L.Independence.NoCorps);
        return TransferFrame.Build(guards, [
            new TransferCall(new(exe + 0x232400, governor.Address, 0), MustReturnZero: true),
            new TransferCall(new(exe + 0x233110, governor.Address, 0), MustReturnZero: true)
        ], [
            new TransferCall(new(exe + NewForceBodyRva, governor.Address, city.Address, [1, 0, (ulong)request.RequestedSlot])),
            new TransferCall(new(exe + AgreementMapRva, 0, 0))
        ]);
    }

    internal static byte[] IndependenceArgs(ulong data, int governor, int city)
    {
        const int Event = 0x20;
        var args = new byte[Event + 0x18];
        BitConverter.TryWriteBytes(args.AsSpan(0), data + StubArgs + Event);
        BitConverter.TryWriteBytes(args.AsSpan(Event + 0xC), governor);
        BitConverter.TryWriteBytes(args.AsSpan(Event + 0x10), city);
        return args;
    }
}

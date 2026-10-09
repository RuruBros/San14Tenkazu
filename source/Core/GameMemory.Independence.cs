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

    // Only the verified game's native event performs the lifecycle. No raw-field
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
        finally { CloseHandle(handle); }
        GameSnapshot after = ReadSnapshot(identity, layout);
        if (!CityIndependence.Confirmed(before, after, request)) throw new InvalidOperationException(L.Independence.Unconfirmed);
        return after;
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

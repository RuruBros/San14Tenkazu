using System.Runtime.InteropServices;

namespace San14Tenkazu;

// How [AUTO 중지] ended AUTO(데모) 플레이. ByGame: the game ran its own switch, so its screen follows at once.
// Otherwise only the two values were written (the top-left ruler panel keeps the demo's force until a save is
// loaded) and Why says why the game's switch was not used.
internal readonly record struct DemoEnd(bool ByGame, string? Why);

// The editor runs the game's own code on its frame (RunOnFrame): a call, optionally followed by the native
// transition/refresh calls that must finish in that same frame.
// CApp::Update (exe+GameBuildFacts.Engine.UpdateRva, the third slot of CApp's vtable) runs the frame: the state manager and with it the
// states and their menus, one frame at a time, paused or not (60 times a second, checked live; the thread it runs on
// stayed the same within a run, but not from one hour to the next). The editor points that slot for a moment at a
// small stub that calls the function once and goes on to Update, and puts the slot back as soon as the stub has run.
// The stub's pages stay allocated (8 KiB), as a frame that read the slot just before may still be on its way in;
// such a late entry finds the stub done and goes straight on to Update.
// The functions (read from the verified build's code, 2026-10-03):
// - The demo play switch (exe+0x33A630). 설정 → 데모 플레이 (exe+0x33B9F0) calls it with 1: it sets the demo bit and
//   puts on the top-left ruler panel the first force of the force order (state +0x1630, most cities first). With 0
//   it clears the bit, puts the player force (state +0x3A) back on the panel and switches the demo-only parts of the
//   screen back; the game never calls it with 0. Calling it again changes nothing.
// - The edit mode's 세력 항복 (S14Event::SExecSurrenderForceForEdit, exe+0x3C1A00). It reads two rulers' officer IDs
//   from the event data its context points at (+0xC surrenders to +0x10) and refuses (returns 0) when either is not a
//   ruler, both lead one force, or the first leads the player's force; otherwise it runs the event's 세력 항복
//   (S14Event::SExecSurrenderForce, exe+0x3A5060), which hands the force over with the function an accepted 항복 권고
//   uses too (exe+0x2A2320, quietly: no fall-of-a-force scene) and redraws the map.
// - The scenario events' 거점 소유자 변경 (S14Event::SExecChangeFootholdOwner, exe+0x3A9300; GiveBase). It reads a base's
//   ID and an officer's ID from the event data its context points at (+0xC, +0x10) and hands the base to the
//   officer's corps (exe+0x26D590, the function a capture uses too), then redraws the map. It always returns 1.
// - The add and remove of the game's list of held 명품 (exe+0x31F2E0, exe+0x32B0E0; SyncOwnedItems).
// - The camera's flight to a hex (exe+0x451360; ShowTileInGame), which the game's lists and buttons call.
internal static partial class GameMemory
{
    private const ulong AppUpdateSlotRva = GameBuildFacts.Engine.UpdateSlotRva, AppUpdateRva = GameBuildFacts.Engine.UpdateRva, DemoSwitchRva = 0x33A630, SurrenderRva = 0x3C1A00;
    private const ulong OwnedItemAddRva = 0x31F2E0, OwnedItemRemoveRva = 0x32B0E0, CameraToTileRva = 0x451360, GiveBaseRva = 0x3A9300;
    // The first bytes of the functions in the verified build.
    private static readonly byte[] AppUpdateStart = Convert.FromHexString(
        "48895C241048896C241848897424205741544155415641574883EC30448BA9C8");
    private static readonly byte[] DemoSwitchStart = Convert.FromHexString(
        "41564883EC4048C7442420FEFFFFFF48895C245048896C2458488974246048897C24688BF1488B05BC384402488B90E871080081A2A8160000FFFEFFFF85C974");
    private static readonly byte[] SurrenderStart = Convert.FromHexString(
        "48895C24084889742410574883EC20488B02488BF28B480CE843790100488B0E488BD6488BD88B4910E832790100488BCB488BF8E8C7DAF6FF85C07441488BCF");
    private static readonly byte[] OwnedItemAddStart = Convert.FromHexString(
        "48895C2408574883EC20488BDA488BF94885D2747D488B02488BCAFF501885C07470807B5001756A833DA11D4B0200740D488D0DA01D4B02FF150A100201488B");
    private static readonly byte[] OwnedItemRemoveStart = Convert.FromHexString(
        "48895424104883EC284C8D8108010000498B40084885C0742648833DE75F4A0200741C488B0DE65F4A028B004885C9740E3B0509604A027306488B04C1EB0233");
    private static readonly byte[] CameraToTileStart = Convert.FromHexString(
        "488BC45556574881ECE000000048C7442430FEFFFFFF488958100F2970D8488B050BA863014833C448898424C00000000F28F3418BE88BF28BD9E8D1FDBBFF48");
    private static readonly byte[] GiveBaseStart = Convert.FromHexString(
        "48895C2418574883EC50488B02488BDA8B480CE808FF0200488B0B488BD3488BF88B4910E8D7030300488BCF488BD8E8CC61F8FF85C00F84AE00000048896C24");

    // The stub: one page of code, then one page of data: its status byte, the call's eax (StubResult), where Update
    // is (StubNext), and from StubArgs the data the call's arguments point at.
    private const int StubPage = 0x1000;
    internal const int StubResult = 4, StubNext = 8, StubArgs = 0x40;
    internal const byte StubArmed = 0, StubRunning = 1, StubDone = 2, StubSkipped = 3;
    // Frames come every 17 ms; the call itself takes a moment more.
    private const int FrameWait = 2000, CallWait = 10000, StubPoll = 15;

    private const uint MemReserve = 0x2000, MemRelease = 0x8000, PageReadWrite = 0x04, PageExecuteRead = 0x20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr handle, IntPtr address, UIntPtr size, uint allocationType, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr handle, IntPtr address, UIntPtr size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr handle, IntPtr address, UIntPtr size, uint protect, out uint oldProtect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr handle, IntPtr address, UIntPtr size);

    // One call: the function, its first bytes in the verified build, rcx and rdx, the data put at StubArgs for them,
    // and whether to call it only while the player force byte is set. More are the arguments after the second, as
    // 64-bit values (a double's bits for a double; FrameStub says where they go).
    internal readonly record struct FrameInvocation(ulong Function, ulong Rcx, ulong Rdx, ulong[]? More = null);
    internal readonly record struct FrameStore(ulong Address, byte Value);
    private sealed record FrameFollowup(FrameInvocation Call, byte[] Start);
    private sealed record FrameCall(ulong Function, byte[] Start, ulong Rcx, ulong Rdx, byte[] Args, bool NeedsPlayer,
        ulong[]? More = null, FrameFollowup[]? After = null, FrameStore[]? Stores = null, byte[]? Body = null);

    // Runs the call once on the game's next frame; `call` is given where the stub's data page is. Why is null when it
    // ran (Result is its eax); otherwise it says why not.
    private static (string? Why, int Result) RunOnFrame(IntPtr handle, GameIdentity identity, Func<ulong, FrameCall> call, int codePages = 1)
    {
        FrameDispatchGate.Wait();try{return RunOnFrameLocked(handle,identity,call,codePages);}finally{FrameDispatchGate.Release();}
    }
    internal static readonly SemaphoreSlim FrameDispatchGate=new(1);
    private static (string? Why, int Result) RunOnFrameLocked(IntPtr handle, GameIdentity identity, Func<ulong, FrameCall> call, int codePages)
    {
        if (!identity.Verified || !GameCapabilities.Inspect(identity,GameFeature.GameCalls).CanAttempt) return (L.Calls.WhyUnverified, 0);
        ulong slot = identity.ModuleBase + AppUpdateSlotRva, originalUpdate = identity.ModuleBase + AppUpdateRva;
        ulong update=EnvironmentRules.EnvironmentNativeBundle.FrameEntry(identity,ReadPointers(handle,slot,1)[0]);
        if(update==0||!CodeIs(handle,originalUpdate,AppUpdateStart))return(L.Calls.WhyCode,0);
        if(update!=originalUpdate&&!CodeIs(handle,update,EnvironmentRules.EnvironmentNativeBundle.Export(identity,"EnvironmentFrame").Code))return(L.Calls.WhyCode,0);

        int codeSize = checked(codePages * StubPage);
        IntPtr page = VirtualAllocEx(handle, IntPtr.Zero, (UIntPtr)(codeSize + StubPage), MemCommit | MemReserve, PageReadWrite);
        if (page == IntPtr.Zero) return (L.Calls.WhyNoPage(Marshal.GetLastWin32Error()), 0);
        using var allocation = new FrameAllocation(handle, page);
        ulong code = (ulong)page.ToInt64(), data = code + (ulong)codeSize;
        FrameCall c = call(data);
        if (!CodeIs(handle, c.Function, c.Start) || c.Args.Length > StubPage - StubArgs
            || (c.After ?? []).Any(next => !CodeIs(handle, next.Call.Function, next.Start)))
        {
            return (L.Calls.WhyCode, 0);
        }
        WriteBytes(handle, data + StubNext, BitConverter.GetBytes(update));
        if (c.Args.Length > 0) WriteBytes(handle, data + StubArgs, c.Args);
        const int BodyOffset = 0x400;
        byte[] stub = FrameStub(code, data, c.Body is null ? c.Function : code + BodyOffset, c.Rcx, c.Rdx,
            c.NeedsPlayer ? identity.ModuleBase + BasePointerRva : 0, c.More,
            c.Body is null ? c.After?.Select(next => next.Call).ToArray() : [], c.Stores);
        if (c.Body is { } body)
        {
            if (stub.Length > BodyOffset || body.Length > codeSize - BodyOffset)
            {
                return (L.Calls.WhyCode, 0);
            }
            Array.Resize(ref stub, BodyOffset + body.Length);
            body.CopyTo(stub, BodyOffset);
        }
        if (stub.Length > codeSize)
        {
            return (L.Calls.WhyCode, 0);
        }
        WriteBytes(handle, code, stub);
        if (!VirtualProtectEx(handle, page, (UIntPtr)codeSize, PageExecuteRead, out _))
            return (L.Calls.WhyNoPage(Marshal.GetLastWin32Error()), 0);
        if (!FlushInstructionCache(handle, page, (UIntPtr)codeSize))
            return (L.Calls.WhyNoPage(Marshal.GetLastWin32Error()), 0);

        if (!VirtualProtectEx(handle, (IntPtr)unchecked((long)slot), (UIntPtr)8, PageReadWrite, out uint slotProtect))
            return (L.Calls.WhyNoPage(Marshal.GetLastWin32Error()), 0);
        byte reached = StubArmed;
        try
        {
            // Once publication is attempted a game thread may already have read this address,
            // even if WriteBytes reports a failure. Only unpublished allocations can be freed.
            allocation.MarkPublished();
            WriteBytes(handle, slot, BitConverter.GetBytes(code));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                reached = ReadBytes(handle, data, 1)[0];
                if (reached is StubDone or StubSkipped) break;
                if (clock.ElapsedMilliseconds > (reached == StubRunning ? CallWait : FrameWait)) break;
                Thread.Sleep(StubPoll);
            }
        }
        finally
        {
            try { if(update==originalUpdate)WriteBytes(handle,slot,BitConverter.GetBytes(update));else RestoreEnvironmentDispatch(handle,identity,code); }
            finally { VirtualProtectEx(handle, (IntPtr)unchecked((long)slot), (UIntPtr)8, slotProtect, out _); }
        }
        return reached switch
        {
            StubDone => (null, BitConverter.ToInt32(ReadBytes(handle, data + StubResult, 4))),
            StubSkipped => (L.Calls.WhySkipped, 0),
            _ => (L.Calls.WhyNoFrame((reached == StubRunning ? CallWait : FrameWait) / 1000), 0),
        };
    }

    private static bool CodeIs(IntPtr handle, ulong address, byte[] start) =>
        ReadBytes(handle, address, start.Length).AsSpan().SequenceEqual(start);

    // Has the game run its demo play switch with 0 on its next frame. Null when it ran; otherwise why not (the
    // values written so far stay as they are).
    private static string? RunDemoSwitchOff(IntPtr handle, GameIdentity identity) =>
        RunOnFrame(handle, identity, _ => new FrameCall(identity.ModuleBase + DemoSwitchRva, DemoSwitchStart, 0, 0, [], NeedsPlayer: true)).Why;

    // Has force `from` surrender to force `to` through the edit mode's 세력 항복 on the game's next frame. Both are
    // forces with a ruler (not tribes) and `from` is not the player's; the game checks that too. Throws when it was
    // not done; the game's change cannot be undone by the editor.
    internal static void SurrenderForce(GameIdentity identity, GameLayout layout, ForceSurrenders.Request request)
    {
        int from = request.From, to = request.To;
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        if (from == to || from is < 1 or > MaxPlayerForce || to is < 1 or > MaxPlayerForce) throw new ArgumentOutOfRangeException(nameof(from));
        if (ReadPace(identity, layout) != GamePace.Paused) throw new InvalidOperationException(L.Surrender.PauseFirst);
        uint access = ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation;
        IntPtr handle = OpenProcess(access, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try
        {
            int fromRuler = RulerOf(handle, identity, layout, from), toRuler = RulerOf(handle, identity, layout, to);
            if (fromRuler != request.FromRuler || toRuler != request.ToRuler)
                throw new InvalidOperationException(L.Surrender.Changed);
            if (layout.State != 0 && ReadBytes(handle, layout.State + StatePlayerForceOffset, 1)[0] == from)
                throw new InvalidOperationException(L.Surrender.PlayerForce);
            (string? why, int result) = RunOnFrame(handle, identity, data =>
            {
                // The context the function is given (its first field points at the event data) and the event data
                // after it: +0xC the ruler who surrenders, +0x10 the one who takes the force in.
                const int Event = 0x20;
                var args = new byte[Event + 0x18];
                BitConverter.TryWriteBytes(args.AsSpan(0), data + StubArgs + Event);
                BitConverter.TryWriteBytes(args.AsSpan(Event + 0xC), fromRuler);
                BitConverter.TryWriteBytes(args.AsSpan(Event + 0x10), toRuler);
                return new FrameCall(identity.ModuleBase + SurrenderRva, SurrenderStart, 0, data + StubArgs, args, NeedsPlayer: false);
            });
            if (why is not null) throw new InvalidOperationException(L.Surrender.NotRun(why));
            if (result == 0) throw new InvalidOperationException(L.Surrender.Refused);
            if (BitConverter.ToUInt16(ReadBytes(handle, layout.Forces[from] + 0x10, 2)) == fromRuler)
                throw new InvalidOperationException(L.Surrender.Unconfirmed);
        }
        finally { CloseHandle(handle); }
    }

    // Hands a city or a gate no corps holds to the corps of a force's ruler through the scenario events' 거점 소유자 변경
    // on the game's next frame (BaseGrants says what it changes). The base and the ruler are their records as read; the
    // game looks the base up by its ID among its bases and falls back to the first one when none has it, and takes an
    // officer it does not find for none (which empties a base), so both are checked here. Throws when it was not run;
    // the game's change cannot be undone by the editor.
    internal static void GiveBase(GameIdentity identity, int baseId, ulong baseAddress, int ruler, ulong rulerAddress)
    {
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        bool city = baseId is >= 1 and <= TribeLast;
        if (!city && baseId is < GateFirst or > GateLast) throw new ArgumentOutOfRangeException(nameof(baseId));
        uint access = ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation;
        IntPtr handle = OpenProcess(access, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try
        {
            byte[] place = ReadBytes(handle, baseAddress, city ? CityStride : CityFields.IdOffset + 2);
            if (BitConverter.ToUInt64(place, 0) != (city ? identity.Classes.City : identity.Classes.Gate) ||
                BitConverter.ToUInt16(place, CityFields.IdOffset) != baseId || city && place[0xCE] is >= 18 and <= 22)
                throw new InvalidOperationException(L.Give.Gone);
            if (BitConverter.ToUInt16(place, CityFields.CorpsOffset) != 0) throw new InvalidOperationException(L.Give.Taken);
            byte[] officer = ReadBytes(handle, rulerAddress, OfficerFields.StatusOffset + 1);
            if (BitConverter.ToUInt64(officer, 0) != identity.Classes.Officer || BitConverter.ToUInt16(officer, OfficerFields.IdOffset) != ruler ||
                officer[OfficerFields.StatusOffset] != 1)
                throw new InvalidOperationException(L.Errors.ForceGone);
            (string? why, _) = RunOnFrame(handle, identity, data =>
            {
                // The context (its first field points at the event data) and the event data after it: +0xC the base,
                // +0x10 the officer whose corps takes it.
                const int Event = 0x20;
                var args = new byte[Event + 0x18];
                BitConverter.TryWriteBytes(args.AsSpan(0), data + StubArgs + Event);
                BitConverter.TryWriteBytes(args.AsSpan(Event + 0xC), baseId);
                BitConverter.TryWriteBytes(args.AsSpan(Event + 0x10), ruler);
                return new FrameCall(identity.ModuleBase + GiveBaseRva, GiveBaseStart, 0, data + StubArgs, args, NeedsPlayer: false);
            });
            if (why is not null) throw new InvalidOperationException(L.Give.NotRun(why));
        }
        finally { CloseHandle(handle); }
    }

    // A force's ruler (officer ID), checked: the record is a force's and has one.
    private static int RulerOf(IntPtr handle, GameIdentity identity, GameLayout layout, int force)
    {
        byte[] record = ReadBytes(handle, layout.Forces[force], ForceRead);
        int ruler = BitConverter.ToUInt16(record, 0x10);
        if (BitConverter.ToUInt64(record, 0) != identity.Classes.Force || ruler is 0 or 0xFFFF)
            throw new InvalidOperationException(L.Errors.ForceGone);
        return ruler;
    }

    // The camera's flight to a hex (exe+0x451360: hex, a flag, milliseconds, distance, pitch, a mode) as the game's
    // lists and buttons fly it when a city or a unit is picked: (hex, 1, 500, -1.0, -1.0, 1), -1 keeping the distance
    // and the pitch as the player left them. It reads the hex's place on the map (exe+0x1886B0: x and z from the hex
    // grid; the height left at 0 while the game's dword at exe+0x1B59290 is 0) and hands the main camera
    // (CMainCameraCtrl) a move with its turn kept, which it glides through frame by frame, the game paused or not. The
    // flag 1 only tells a running CProgressState that the view moved; the editor passes 0, for the camera alone (the
    // game's events move it without telling too). The camera keeper is a static the game makes on first use
    // (exe+0x11170 returns it): +0x50 the main camera, +0x280 the camera in charge when it is another one (a scene's),
    // and the main camera's +0x188 set while it takes no moves (its move, exe+0x143D0, returns at once). Read from the
    // verified build's code and checked live, 2026-10-03 (Reverse/notes/camera.md).
    private const ulong CameraKeeperRva = 0x1B62450, MainCameraVtableRva = 0x13426F8;
    private const int MainCamera = 0x50, OtherCamera = 0x280, MainCameraHeld = MainCamera + 0x188;
    internal const int CameraFlight = 500;

    // Has the game's camera fly to a hex. Null when it flies; otherwise why not.
    internal static string? ShowTileInGame(GameIdentity identity, int tile)
    {
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        ArgumentOutOfRangeException.ThrowIfNegative(tile);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(tile, TileCount);
        uint access = ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation;
        IntPtr handle = OpenProcess(access, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try
        {
            if (!identity.Verified || !GameCapabilities.Inspect(identity,GameFeature.GameCalls).CanAttempt) return L.Calls.WhyUnverified;
            ulong keeper = identity.ModuleBase + CameraKeeperRva;
            byte[] held = new byte[4];
            if (TryReadPointer(handle, keeper + MainCamera) != identity.ModuleBase + MainCameraVtableRva) return L.Calls.WhyNoCamera;
            if (TryReadPointer(handle, keeper + OtherCamera) is not 0UL) return L.Calls.WhyOtherCamera;
            if (!TryRead(handle, keeper + MainCameraHeld, held) || BitConverter.ToInt32(held) != 0) return L.Calls.WhyCameraHeld;
            ulong keep = (ulong)BitConverter.DoubleToInt64Bits(-1.0);
            return RunOnFrame(handle, identity, _ => new FrameCall(identity.ModuleBase + CameraToTileRva, CameraToTileStart,
                (ulong)tile, 0, [], NeedsPlayer: false, More: [CameraFlight, keep, keep, 1])).Why;
        }
        finally { CloseHandle(handle); }
    }

    // The game keeps the 명품 that are held (state 1) on a list of its own beside the table, and its rules read only
    // that list: the ability bonus and 옥새's 매력 (CPersonData::GetAbility, exe+0x22FCE0) walk it for the items whose
    // owner is the officer. The game's own give (exe+0x323ED0) writes the owner and, when the state was not 1 yet, sets
    // it and adds the item (exe+0x31F2E0, which adds only a held one, and twice if asked twice); taking an item away
    // removes it (exe+0x32B0E0, every node of it); loading a save builds the list afresh from the states. An owner and
    // state written into the record alone leave the list as it was (read 2026-10-03, checked in an emulator: 노자 given
    // that way added nothing to 하진's 지력 until it was added). [world + 0x110] points at the list's number among the
    // game's lists (a dword, below the dword at exe+0x27D1120), [exe+0x27D10F0] at their first nodes, and a node is
    // {record, next}; world is the game's own pointer at exe+GameBuildFacts.Memory.BasePointerRva.
    private const ulong OwnedItemsList = 0x110, ListFirstsRva = 0x27D10F0, ListCountRva = 0x27D1120;

    // Whether the list needs a 명품 added (true), removed (false) or neither (null): it holds exactly the held ones.
    internal static bool? OwnedListFix(int state, bool listed) => (state == ItemFields.Found, listed) switch
    {
        (true, false) => true,
        (false, true) => false,
        _ => null,
    };

    // After a write of 명품 records: has the game add each held one missing from its list and remove each listed one
    // no longer held, one call a frame. Returns a line for each it could not; the write itself stays, and the game
    // puts the list right when a save is loaded.
    internal static IReadOnlyList<string> SyncOwnedItems(GameIdentity identity, IReadOnlyList<RecordEdit> edits)
    {
        List<RecordEdit> items = [.. edits.Where(e => e.Kind == RecordKind.Item && e.Changes.Count > 0).DistinctBy(e => e.Address)];
        if (items.Count == 0) return [];
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        uint access = ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation;
        IntPtr handle = OpenProcess(access, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try
        {
            ulong world = identity.Verified && TryReadPointer(handle, identity.ModuleBase + BasePointerRva) is ulong w ? w : 0;
            HashSet<ulong>? listed = world == 0 ? null : ReadOwnedItems(handle, identity.ModuleBase, world);
            var lines = new List<string>();
            foreach (RecordEdit item in items)
            {
                byte[] state = new byte[1];
                if (!TryRead(handle, item.Address + (ulong)ItemFields.State.Offset, state)) continue;
                // Without the list, the one this write took across 발견 is the one it does not agree with.
                bool on = listed?.Contains(item.Address) ?? ItemFields.State.Read(item.Expected) == ItemFields.Found;
                if (OwnedListFix(state[0], on) is not bool add) continue;
                string? why = listed is not null ? null : identity.Verified ? L.Calls.WhyNoList : L.Calls.WhyUnverified;
                if (why is null)
                {
                    (why, int result) = RunOnFrame(handle, identity, _ => add
                        ? new FrameCall(identity.ModuleBase + OwnedItemAddRva, OwnedItemAddStart, world, item.Address, [], NeedsPlayer: false)
                        : new FrameCall(identity.ModuleBase + OwnedItemRemoveRva, OwnedItemRemoveStart, world, item.Address, [], NeedsPlayer: false));
                    if (why is null && ReadOwnedItems(handle, identity.ModuleBase, world)?.Contains(item.Address) != add)
                        why = L.Calls.WhyListUnchanged(result);
                }
                if (why is not null) lines.Add(add ? L.Apply.ItemNotCounted(item.Label, why) : L.Apply.ItemStillListed(item.Label, why));
            }
            return lines;
        }
        finally { CloseHandle(handle); }
    }

    // The records on the game's list of held 명품; null when it does not read as one.
    private static HashSet<ulong>? ReadOwnedItems(IntPtr handle, ulong exe, ulong world)
    {
        byte[] number = new byte[4], count = new byte[4], node = new byte[16];
        if (TryReadPointer(handle, world + OwnedItemsList) is not ulong list || list == 0 || !TryRead(handle, list, number) ||
            !TryRead(handle, exe + ListCountRva, count) || BitConverter.ToUInt32(number) >= BitConverter.ToUInt32(count) ||
            TryReadPointer(handle, exe + ListFirstsRva) is not ulong firsts || firsts == 0 ||
            TryReadPointer(handle, firsts + BitConverter.ToUInt32(number) * 8UL) is not ulong next)
            return null;
        var items = new HashSet<ulong>();
        for (int nodes = 0; next != 0; nodes++)
        {
            if (nodes > 2 * ItemSlots || !TryRead(handle, next, node)) return null;
            items.Add(BitConverter.ToUInt64(node, 0));
            next = BitConverter.ToUInt64(node, 8);
        }
        return items;
    }

    private static ulong? TryReadPointer(IntPtr handle, ulong address)
    {
        byte[] value = new byte[8];
        return TryRead(handle, address, value) ? BitConverter.ToUInt64(value) : null;
    }

    // The stub's code at `code`, its data page at `data` (x64). Entered in place of CApp::Update (rcx, rdx, r8, r9 and
    // xmm0~3 its arguments, kept), it runs once: status Armed → Running; with a `playerGuard` (the address of the
    // game's base pointer) it marks Skipped unless the player force byte ([guard] + 0x871E8, then +0x3A, as the game
    // reads it) is set; else it calls `function` with rcx and rdx, keeps eax at StubResult and marks Done. Every entry
    // ends in a jump to Update through StubNext. `more` are the arguments after the second, up to five: the third goes
    // in r8 and xmm2, the fourth in r9 and xmm3 (the same bits in both, so an integer or a double reads right), the
    // others on the stack after the callee's shadow space. Without them the stub is the one checked first.
    internal static byte[] FrameStub(ulong code, ulong data, ulong function, ulong rcx, ulong rdx, ulong playerGuard,
        ulong[]? more = null, IReadOnlyList<FrameInvocation>? after = null, IReadOnlyList<FrameStore>? stores = null)
    {
        more ??= [];
        after ??= [];
        if (more.Length > 5 || after.Any(call => call.More?.Length > 5)) throw new ArgumentOutOfRangeException(nameof(more));
        // The frame below the four pushes: shadow space, the stack arguments, then xmm0~3 kept; rsp ends 16-byte
        // aligned at the call (it is 8 off after the pushes) and the size fits a byte.
        int stacked = Math.Max(0, Math.Max(more.Length, after.Select(call => call.More?.Length ?? 0).DefaultIfEmpty().Max()) - 2);
        int kept = 0x20 + 8 * stacked, frame = kept + 0x40;
        if (frame % 16 != 8) frame += 8;
        ulong status = data, result = data + StubResult, next = data + StubNext;
        var b = new List<byte>();
        void Add(params byte[] bytes) => b.AddRange(bytes);
        // A rip-relative field: disp32 from the end of the instruction, `after` bytes past the disp32.
        void Rip(ulong target, int after) => b.AddRange(BitConverter.GetBytes(checked((int)((long)target - (long)code - (b.Count + 4 + after)))));
        int Jump8(byte opcode) { Add(opcode, 0); return b.Count - 1; }
        void Land(int at) => b[at] = unchecked((byte)checked((sbyte)(b.Count - (at + 1))));
        void SetStatus(byte value) { Add(0xC6, 0x05); Rip(status, 1); Add(value); }
        void ToUpdate() { Add(0xFF, 0x25); Rip(next, 0); }         // jmp [rip+next]

        Add(0x80, 0x3D); Rip(status, 1); Add(StubArmed);           // cmp byte [rip+status], Armed
        int run = Jump8(0x74);                                      // je run
        ToUpdate();
        Land(run);
        SetStatus(StubRunning);
        if (playerGuard != 0)
        {
            Add(0x48, 0xB8); Add(BitConverter.GetBytes(playerGuard)); // mov rax, playerGuard
            Add(0x48, 0x8B, 0x00);                                  // mov rax, [rax]
            Add(0x48, 0x85, 0xC0);                                  // test rax, rax
            int noBase = Jump8(0x74);                               // jz skip
            Add(0x48, 0x8B, 0x80); Add(BitConverter.GetBytes((int)(BaseAdjust + StatePointer))); // mov rax, [rax+0x871E8]
            Add(0x48, 0x85, 0xC0);                                  // test rax, rax
            int noState = Jump8(0x74);                              // jz skip
            Add(0x80, 0x78, StatePlayerForceOffset, 0x00);          // cmp byte [rax+0x3A], 0
            int call = Jump8(0x75);                                 // jne call
            Land(noBase); Land(noState);
            SetStatus(StubSkipped);
            ToUpdate();
            Land(call);
        }
        Add(0x51, 0x52, 0x41, 0x50, 0x41, 0x51);                    // push rcx, rdx, r8, r9
        Add(0x48, 0x83, 0xEC, (byte)frame);                         // sub rsp, frame
        for (byte r = 0; r < 4; r++) Add(0xF3, 0x0F, 0x7F, (byte)(0x44 | r << 3), 0x24, (byte)(kept + r * 0x10)); // movdqu [rsp+kept+16r], xmm r
        void Invoke(FrameInvocation call)
        {
            ulong[] args = call.More ?? [];
            for (int k = 2; k < args.Length; k++)
            {
                Add(0x48, 0xB8); Add(BitConverter.GetBytes(args[k]));
                Add(0x48, 0x89, 0x44, 0x24, (byte)(0x20 + 8 * (k - 2)));
            }
            if (args.Length > 0)
            {
                Add(0x49, 0xB8); Add(BitConverter.GetBytes(args[0]));
                Add(0x66, 0x49, 0x0F, 0x6E, 0xD0);
            }
            if (args.Length > 1)
            {
                Add(0x49, 0xB9); Add(BitConverter.GetBytes(args[1]));
                Add(0x66, 0x49, 0x0F, 0x6E, 0xD9);
            }
            Add(0x48, 0xB9); Add(BitConverter.GetBytes(call.Rcx));
            Add(0x48, 0xBA); Add(BitConverter.GetBytes(call.Rdx));
            Add(0x48, 0xB8); Add(BitConverter.GetBytes(call.Function));
            Add(0xFF, 0xD0);
        }
        Invoke(new FrameInvocation(function, rcx, rdx, more));
        Add(0x89, 0x05); Rip(result, 0);                            // mov [rip+result], eax
        foreach (FrameInvocation nextCall in after) Invoke(nextCall);
        // Undo retains the exact independent flag byte as read, even when a native setter cleared bit 7.
        foreach (FrameStore store in stores ?? [])
        {
            Add(0x48, 0xB8); Add(BitConverter.GetBytes(store.Address));
            Add(0xC6, 0x00, store.Value);
        }
        for (byte r = 0; r < 4; r++) Add(0xF3, 0x0F, 0x6F, (byte)(0x44 | r << 3), 0x24, (byte)(kept + r * 0x10)); // movdqu xmm r, [rsp+kept+16r]
        Add(0x48, 0x83, 0xC4, (byte)frame);                         // add rsp, frame
        Add(0x41, 0x59, 0x41, 0x58, 0x5A, 0x59);                    // pop r9, r8, rdx, rcx
        SetStatus(StubDone);
        ToUpdate();
        return b.ToArray();
    }
}

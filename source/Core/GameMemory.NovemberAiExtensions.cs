namespace San14Tenkazu;

internal static partial class GameMemory
{
    private const ulong NovemberSelectorTable = 0x1692E78;
    private static readonly ulong[] NovemberSelectorEntries = [0x8CA1C0, 0xB2E380, 0xB2E790, 0x1657D0, 0x1657D0,
        0x154910, 0x1657D0, 0xB2BDF0, 0xB2DF40, 0xB2DDD0, 0x40C620, 0xB2E7B0, 0xB2E920, 0xB2E7A0,
        0x70C80, 0x70C80, 0x20470, 0xB2E510];

    private static readonly (ulong Rva, string Hex)[] NovemberAiExtensionCode =
    [
        (0xB2E510, "4055415541564883EC50837924004D8BE8488BEA4C8BF1750FB8020000004883C450415E415D5DC3498BD648895C2478"),
        (0xB7100, "48895C2408574883EC2033DB488BFA895C2440E8D80E0801488BC84C8D4424404533C9488D1576632901E8411BA70084"),
        (0x331780, "4883EC284885C97414488B01FF501885C0740AB8010000004883C428C333C04883C428C3"),
        (0x22D520, "40534883EC200FB741123D7017000077138BC8488B05263A5502488B9CC858010000EB0E488B05153A5502488B98503A"),
        (0x235300, "48895C24084889742410574883EC20488B054ABC5402488BF9488B88E8710800E86BB80F00488BC8488BF0E850C40F00"),
        (0x20040, "40534883EC20488BCA498BD0E8AF700900488BC8488BD8E82417310085C07431488B0589D4B701488BD3488B4828E89D"),
        (0x201C0, "40574883EC4048C7442420FEFFFFFF48895C24584889742460488BF933DB48895C2450488BCAE8057E1101488BC84533"),
        (0x61C90, "40534883EC20488BD948C744243000000000488BCAE846630D018B53384C8D442430488BC84533C9E86370AC0084C075"),
        (0x306AF0, "48895C240848896C2410488974241848897C242041564883EC20488BD9E86EAC020085C07419488BCBE862AC020085C0"),
    ];

    private sealed record NovemberAiTarget(ulong Selector, ulong Recover, ulong Evade, ulong Retreat, ulong Table, ulong Data, ulong Flags);

    private static void RequireNovemberAi(GameIdentity identity, string stockStamp, AiTree[] file)
    {
        if (!AiBuildProfile.IsNovember(identity) || GameBuildProfiles.FindData(identity.ExeHash) is not { } profile || identity.DataProfile != profile ||
            stockStamp != AiBuildProfile.NovemberStock) throw new InvalidOperationException(L.Ai.Unverified);
        RequireAiExtensionTree(file);
    }

    private static byte[] NovemberAiTable(ulong module, ulong code) => NovemberSelectorEntries
        .Select((rva, i) => BitConverter.GetBytes(i == 17 && code != 0 ? code : module + rva)).SelectMany(bytes => bytes).ToArray();

    private static NovemberAiTarget ReadNovemberAiTarget(IntPtr handle, GameIdentity identity, AiTree[] file)
    {
        ulong module = identity.ModuleBase;
        var memory = new ProcessMemory(handle);
        ulong Node(int tree, int slot) => AiMemory.ReadNode(memory, module, AiBuildProfile.NovemberManager, file[tree], slot);
        ulong selector = Node(3, 7), recover = Node(3, 102), evade = Node(3, 8), retreat = Node(2, 347);
        ulong original = module + NovemberSelectorTable;
        if (!CodeIs(handle, original - 8, NovemberAiTable(module, 0))) throw new InvalidDataException(L.AiExtensions.ForeignNode);
        foreach (var (leaf, table, entry) in new[] { (recover, 0x1347330UL, 0x20040UL), (evade, 0x1347498UL, 0x201C0UL),
            (retreat, 0x134DEA0UL, 0x61C90UL) })
        {
            if (ReadPointers(handle, leaf, 1)[0] != module + table || ReadPointers(handle, module + table + 0x80, 1)[0] != module + entry)
                throw new InvalidDataException(L.AiExtensions.ForeignNode);
        }
        if (!ReadBytes(handle, selector + 0x28, 12).SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0 }) ||
            new[] { Node(3, 2), evade, retreat }.Any(leaf => BitConverter.ToInt32(ReadBytes(handle, leaf + 0x38, 4)) != 115104829))
            throw new InvalidDataException(L.AiExtensions.UnsupportedTree);
        foreach (var (rva, hex) in NovemberAiExtensionCode)
        {
            var code = Convert.FromHexString(hex);
            RequireEditPage(handle, module + rva, code.Length, PageExecuteRead, module);
            if (!CodeIs(handle, module + rva, code)) throw new InvalidDataException(L.Calls.WhyCode);
        }
        ulong tableAddress = ReadPointers(handle, selector, 1)[0];
        if (tableAddress == original) return new(selector, recover, evade, retreat, tableAddress, 0, AiExtensionSettings.Default.Flags);
        if (tableAddress < AiExtensionStub.Page + AiExtensionStub.Vtable || (tableAddress & 4095) != AiExtensionStub.Vtable)
            throw new InvalidDataException(L.AiExtensions.ForeignNode);
        ulong data = tableAddress - AiExtensionStub.Vtable, wrapper = data - AiExtensionStub.Page;
        RequireEditPage(handle, wrapper, AiExtensionStub.Page, PageExecuteRead, wrapper);
        RequireEditPage(handle, data, AiExtensionStub.Page, PageReadWrite, wrapper);
        ulong[] header = ReadPointers(handle, data, 8);
        if (header[0] != AiExtensionStub.Magic || header[1] != module || header[2] != selector || header[3] != original ||
            header[5] != recover || header[6] != evade || header[7] != retreat ||
            !CodeIs(handle, wrapper, AiExtensionStub.Build(module, data, AiExtensionStub.November)) ||
            !CodeIs(handle, tableAddress - 8, NovemberAiTable(module, wrapper)))
            throw new InvalidDataException(L.AiExtensions.ForeignNode);
        AiExtensionSettings.FromFlags(header[4]);
        return new(selector, recover, evade, retreat, tableAddress, data, header[4]);
    }

    internal static AiExtensionStatus ReadNovemberAiExtensions(GameIdentity identity, AiTree[] file, string stamp)
    {
        RequireNovemberAi(identity, stamp, file);
        lock (AiExtensionLock)
        {
            var session = RequirePlayingSession(identity);
            IntPtr handle = OpenRead(identity.ProcessId);
            try
            {
                var target = ReadNovemberAiTarget(handle, identity, file);
                var status = NovemberAiStatus(handle, target);
                if (RequirePlayingSession(identity).Entries[2] != session.Entries[2]) throw new InvalidOperationException(L.Errors.StateMoved);
                return status;
            }
            finally { CloseHandle(handle); }
        }
    }

    private static AiExtensionStatus NovemberAiStatus(IntPtr handle, NovemberAiTarget target)
    {
        if (target.Data == 0) return new(AiExtensionSettings.Default);
        ulong[] values = ReadPointers(handle, target.Data + AiExtensionStub.Config, 11);
        return new(AiExtensionSettings.FromFlags(values[0]), values[4], values[5], values[6], values[7], values[8], values[9], values[10]);
    }

    internal static AiExtensionStatus ApplyNovemberAiExtensions(GameIdentity identity, GameLayout layout, AiTree[] file, string stamp, AiExtensionSettings settings)
        => ApplyNovemberAiExtension(identity, layout, file, stamp, settings.Flags, null);

    private static AiExtensionStatus ApplyNovemberAiExtension(GameIdentity identity, GameLayout layout, AiTree[] file,
        string stamp, ulong flags, Action<object>? roundTrip)
    {
        RequireNovemberAi(identity, stamp, file);
        lock (AiExtensionLock)
        {
            RequirePlayingSession(identity);
            NovemberAiTarget expected;
            IntPtr read = OpenRead(identity.ProcessId);
            try { expected = ReadNovemberAiTarget(read, identity, file); }
            finally { CloseHandle(read); }
            (ulong Start, ulong End)[] executing = expected.Data == 0 ? [] : [(expected.Data - AiExtensionStub.Page, expected.Data)];
            return WithProfiledEditSession(identity, layout, (handle, snapshot) =>
        {
            if (snapshot.PlayerForce <= 0) throw new InvalidOperationException(L.Ai.Unverified);
            var before = ReadNovemberAiTarget(handle, identity, file);
            if (before != expected) throw new InvalidOperationException(L.Errors.StateMoved);
            if (before.Flags == flags && roundTrip is null) return NovemberAiStatus(handle, before);
            if (roundTrip is not null && before.Data != 0) throw new InvalidOperationException(L.AiExtensions.ForeignNode);
            RequireEditPage(handle, before.Selector, 8, PageReadWrite);
            IntPtr allocated = IntPtr.Zero;
            bool retain = false;
            ulong data = before.Data;
            try
            {
                if (data == 0)
                {
                    allocated = VirtualAllocEx(handle, IntPtr.Zero, (UIntPtr)(2 * AiExtensionStub.Page), MemCommit | MemReserve, PageReadWrite);
                    if (allocated == IntPtr.Zero) throw AccessError(L.RuntimeErrors.AllocationFailed);
                    ulong code = (ulong)allocated.ToInt64();
                    data = code + AiExtensionStub.Page;
                    ulong[] header = [AiExtensionStub.Magic, identity.ModuleBase, before.Selector, before.Table, before.Flags, before.Recover, before.Evade, before.Retreat];
                    WriteBytes(handle, data, header.SelectMany(BitConverter.GetBytes).ToArray());
                    WriteBytes(handle, code, AiExtensionStub.Build(identity.ModuleBase, data, AiExtensionStub.November));
                    WriteBytes(handle, data + AiExtensionStub.Vtable - 8, NovemberAiTable(identity.ModuleBase, code));
                    if (!VirtualProtectEx(handle, allocated, (UIntPtr)AiExtensionStub.Page, PageExecuteRead, out _) ||
                        !FlushInstructionCache(handle, allocated, (UIntPtr)AiExtensionStub.Page))
                        throw AccessError(L.RuntimeErrors.AllocationProtectionFailed);
                }
                ulong installed = data + AiExtensionStub.Vtable;
                RuntimeRuleWrite[] changes = [
                    new(data + AiExtensionStub.Config, BitConverter.GetBytes(before.Flags), BitConverter.GetBytes(flags), false),
                    new(before.Selector, BitConverter.GetBytes(before.Table), BitConverter.GetBytes(installed), false)
                ];
                var changed = changes.Where(change => !change.Before.SequenceEqual(change.After)).ToArray();
                using var memory = new RuntimeProcessMemory(handle);
                AiExtensionStatus? status = null;
                RuntimeRuleTransaction.Apply(memory, changed, () => retain = true, () =>
                {
                    var verified = ReadNovemberAiTarget(handle, identity, file);
                    if (verified.Selector != before.Selector || verified.Data != data || verified.Flags != flags)
                        throw new InvalidOperationException(L.AiExtensions.NotApplied);
                    status = NovemberAiStatus(handle, verified);
                    if (roundTrip is null) return;
                    RuntimeRuleTransaction.Apply(memory, changed.Reverse().Select(w => w with { Before = w.After, After = w.Before }).ToArray(),
                        () => { }, () =>
                        {
                            if (ReadNovemberAiTarget(handle, identity, file) != before || ReadPointers(handle, data + AiExtensionStub.Config, 1)[0] != before.Flags)
                                throw new InvalidOperationException(L.AiExtensions.NotApplied);
                        });
                    retain = false;
                });
                roundTrip?.Invoke(new { stage = "restored", identity.ProcessId, identity.ExeHash, snapshot.Date, snapshot.Hour,
                    node = before.Selector, originalTable = before.Table, changedTable = installed, originalFlags = before.Flags, changedFlags = flags });
                return status!;
            }
            finally
            {
                // Free a temporary table only when it was never visible to a running game thread.
                if (allocated != IntPtr.Zero && !retain && !VirtualFreeEx(handle, allocated, UIntPtr.Zero, MemRelease))
                    throw AccessError(L.RuntimeErrors.UnusedAllocationReleaseFailed);
            }
        }, executing);
        }
    }

#if LOCAL_DEVELOPER
    internal static void RoundTripNovemberAiExtension(GameIdentity identity, GameLayout layout, AiTree[] file, string stamp, Action<object> record)
        => ApplyNovemberAiExtension(identity, layout, file, stamp, AiExtensionSettings.Default.Flags | 1, record);
#endif
}

namespace San14Tenkazu;

internal static partial class GameMemory
{
    private static readonly object AiExtensionLock = new();
    private static readonly (ulong Rva, string Start)[] AiExtensionCode =
    [
        (AiExtensionStub.SelectorRva, "4055415541564883EC50837924004D8BE8488BEA4C8BF1750FB8020000004883C450415E415D5DC3"),
        (AiExtensionStub.AgentUnitRva, "48895C2408574883EC2033DB488BFA895C2440E8B819B400488BC84C8D4424404533C9488D15E6432901E82103A700"),
        (AiExtensionStub.ValidRva, "4883EC284885C97414488B01FF501885C0740AB8010000004883C428C333C04883C428C3"),
        (AiExtensionStub.CorpsIndexRva, "40534883EC200FB741123D7017000077138BC8488B051E245502488B9CC858010000EB0E488B050D245502488B98503A0700488BCBE8E639100085C0740D0FB683E00000004883C4205BC333C04883C4205BC3"),
        (AiExtensionStub.DirectCorpsRva, "48895C24084889742410574883EC20488B05A2A55402488BF9488B88E8710800E88BAF0F00488BC8488BF0E870BB0F0085C07467"),
        (AiExtensionStub.RecoverRva, "40534883EC20488BCA498BD0E8FF5F0900488BC8488BD8E884F4300085C07431488B0519A4B70148"),
        (AiExtensionStub.EvadeRva, "40574883EC4048C7442420FEFFFFFF48895C24584889742460488BF933DB48895C2450488BCAE835"),
        (AiExtensionStub.RetreatRva, "40534883EC20488BD948C744243000000000488BCAE80669B9008B53384C8D442430488BC84533C9"),
        (AiExtensionStub.HoldRva, "48895C240848896C2410488974241848897C242041564883EC20488BD9E8DEAD020085C07419488B"),
    ];

    private static ulong AiExtensionNode(AiTree[] file, AiLiveTree[] live)
    {
        if (file.Length != 4 || live.Length != 4) throw new InvalidDataException(L.AiExtensions.UnsupportedTree);
        AiNode[] units = file[(int)AiTreeKind.Unit].Nodes, troops = file[(int)AiTreeKind.Troops].Nodes;
        if (units.Length <= 102 || troops.Length <= 347 || units[7].Class != AiCatalog.Sequential
            || !units[7].Params.SequenceEqual(new[] { 0, 0, 1 }) || units[7].Parent != 1
            || units[102].Class != 0x8AEA0861 || units[8].Params is not [115104829]
            || units[2].Params is not [115104829] || troops[347].Params is not [115104829])
            throw new InvalidDataException(L.AiExtensions.UnsupportedTree);
        return live[(int)AiTreeKind.Unit].Nodes[7];
    }

    // A private vtable for this one node, never the shared SequentialNode vtable. RTTI and every other slot match.
    private static byte[] AiExtensionVtable(IntPtr handle, ulong exe, ulong code)
    {
        byte[] table = ReadBytes(handle, exe + AiExtensionStub.OriginalVtableRva - 8, 0x108);
        BitConverter.TryWriteBytes(table.AsSpan(8 + 0x80), code);
        return table;
    }

    private static ulong ReadAiExtensionData(IntPtr handle, GameIdentity identity, AiLiveTree[] live, ulong node)
    {
        ulong vtable = ReadPointers(handle, node, 1)[0], exe = identity.ModuleBase;
        if (vtable == exe + AiExtensionStub.OriginalVtableRva) return 0;
        if (vtable < AiExtensionStub.Page + AiExtensionStub.Vtable) throw new InvalidDataException(L.AiExtensions.ForeignNode);
        ulong data = vtable - AiExtensionStub.Vtable, code = data - AiExtensionStub.Page;
        ulong[] header = ReadPointers(handle, data, 8);
        if (header[0] != AiExtensionStub.Magic || header[1] != exe || header[2] != node
            || header[3] != exe + AiExtensionStub.OriginalVtableRva
            || header[5] != live[3].Nodes[102] || header[6] != live[3].Nodes[8] || header[7] != live[2].Nodes[347]
            || !CodeIs(handle, code, AiExtensionStub.Build(exe, data))
            || !CodeIs(handle, code + AiExtensionStub.Installer, AiExtensionStub.Install(data))
            || !CodeIs(handle, data + 0x100, AiExtensionVtable(handle, exe, code)))
            throw new InvalidDataException(L.AiExtensions.ForeignNode);
        AiExtensionSettings.FromFlags(header[4] & 7);
        return data;
    }

    internal static AiExtensionStatus ReadAiExtensions(GameIdentity identity, AiTree[] trees)
    {
        if (!identity.Verified || !IsSameGame(identity)) throw new InvalidOperationException(L.Ai.Unverified);
        lock (AiExtensionLock)
        {
            IntPtr handle = OpenRead(identity.ProcessId);
            try
            {
                AiLiveTree[] live = AiMemory.Read(new ProcessMemory(handle), identity.ModuleBase, trees);
                ulong data = ReadAiExtensionData(handle, identity, live, AiExtensionNode(trees, live));
                if (data == 0) return new(AiExtensionSettings.Default);
                ulong[] values = ReadPointers(handle, data + AiExtensionStub.Config, 11);
                return new(AiExtensionSettings.FromFlags(values[0] & 7), values[4], values[5], values[6], values[7], values[8], values[9], values[10]);
            }
            finally { CloseHandle(handle); }
        }
    }

    internal static AiExtensionStatus ApplyAiExtensions(GameIdentity identity, AiTree[] trees, AiExtensionSettings settings)
    {
        settings.Validate();
        if (!identity.Verified || !IsSameGame(identity)) throw new InvalidOperationException(L.Ai.Unverified);
        lock (AiExtensionLock)
        {
            IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, identity.ProcessId);
            if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
            try
            {
                AiLiveTree[] live = AiMemory.Read(new ProcessMemory(handle), identity.ModuleBase, trees);
                ulong exe = identity.ModuleBase, node = AiExtensionNode(trees, live);
                foreach ((ulong rva, string start) in AiExtensionCode)
                    if (!CodeIs(handle, exe + rva, Convert.FromHexString(start))) throw new InvalidOperationException(L.Calls.WhyCode);
                foreach ((ulong leaf, ulong rva) in new[] { (live[3].Nodes[8], AiExtensionStub.EvadeRva),
                    (live[3].Nodes[102], AiExtensionStub.RecoverRva), (live[2].Nodes[347], AiExtensionStub.RetreatRva) })
                    if (ReadPointers(handle, ReadPointers(handle, leaf, 1)[0] + 0x80, 1)[0] != exe + rva)
                        throw new InvalidDataException(L.AiExtensions.ForeignNode);
                ulong expected = ReadPointers(handle, node, 1)[0], data = ReadAiExtensionData(handle, identity, live, node);
                foreach (ulong leaf in new[] { live[3].Nodes[2], live[3].Nodes[8], live[2].Nodes[347] })
                    if (BitConverter.ToInt32(ReadBytes(handle, leaf + 0x38, 4)) != 115104829)
                        throw new InvalidDataException(L.AiExtensions.UnsupportedTree);
                if (new[] { 0, 0, 1 }.Where((p, i) => AiMemory.Value(new ProcessMemory(handle), live[3], 7, i) != p).Any())
                    throw new InvalidDataException(L.AiExtensions.UnsupportedTree);
                if (data == 0)
                {
                    IntPtr allocation = VirtualAllocEx(handle, IntPtr.Zero, (UIntPtr)(2 * AiExtensionStub.Page), MemCommit | MemReserve, PageReadWrite);
                    if (allocation == IntPtr.Zero) throw AccessError(L.Calls.WhyNoPage(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
                    ulong code = (ulong)allocation.ToInt64(); data = code + AiExtensionStub.Page;
                    bool exposed = false;
                    try
                    {
                        ulong[] header = [AiExtensionStub.Magic, exe, node, expected, 0, live[3].Nodes[102], live[3].Nodes[8], live[2].Nodes[347]];
                        WriteBytes(handle, data, header.SelectMany(BitConverter.GetBytes).ToArray());
                        byte[] stub = AiExtensionStub.Build(exe, data);
                        if (stub.Length >= AiExtensionStub.Installer) throw new InvalidOperationException(L.Calls.WhyCode);
                        WriteBytes(handle, code, stub);
                        WriteBytes(handle, code + AiExtensionStub.Installer, AiExtensionStub.Install(data));
                        WriteBytes(handle, data + 0x100, AiExtensionVtable(handle, exe, code));
                        if (!VirtualProtectEx(handle, allocation, (UIntPtr)AiExtensionStub.Page, PageExecuteRead, out _)
                            || !FlushInstructionCache(handle, allocation, (UIntPtr)AiExtensionStub.Page))
                            throw AccessError(L.Calls.WhyNoPage(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
                        // Retain after dispatch even on a timeout: a frame may already have loaded the pointer.
                        exposed = true;
                        SetAiExtensionConfig(handle, identity, node, expected, data, settings.Flags);
                    }
                    finally { if (!exposed) VirtualFreeEx(handle, allocation, UIntPtr.Zero, MemRelease); }
                }
                else SetAiExtensionConfig(handle, identity, node, expected, data, settings.Flags);
                if (ReadPointers(handle, node, 1)[0] != data + AiExtensionStub.Vtable
                    || ReadPointers(handle, data + AiExtensionStub.Config, 1)[0] != settings.Flags)
                    throw new InvalidOperationException(L.AiExtensions.NotApplied);
            }
            finally { CloseHandle(handle); }
            return ReadAiExtensions(identity, trees);
        }
    }

    // Used by the disabled native probe to put the original vptr back. Keep pages for possible late entries.
    internal static void RemoveDisabledAiExtension(GameIdentity identity, AiTree[] trees)
    {
        lock (AiExtensionLock)
        {
            if (!identity.Verified || !IsSameGame(identity)) throw new InvalidOperationException(L.Ai.Unverified);
            IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, identity.ProcessId);
            if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
            try
            {
                AiLiveTree[] live = AiMemory.Read(new ProcessMemory(handle), identity.ModuleBase, trees);
                ulong node = AiExtensionNode(trees, live), data = ReadAiExtensionData(handle, identity, live, node);
                if (data == 0) return;
                ulong flags = ReadPointers(handle, data + AiExtensionStub.Config, 1)[0];
                if ((flags & 1) != 0) throw new InvalidOperationException(L.AiExtensions.NotApplied);
                SetAiExtensionConfig(handle, identity, node, data + AiExtensionStub.Vtable, data, flags,
                    identity.ModuleBase + AiExtensionStub.OriginalVtableRva);
                if (ReadPointers(handle, node, 1)[0] != identity.ModuleBase + AiExtensionStub.OriginalVtableRva)
                    throw new InvalidOperationException(L.AiExtensions.NotApplied);
            }
            finally { CloseHandle(handle); }
        }
    }

    private static void SetAiExtensionConfig(IntPtr handle, GameIdentity identity, ulong node, ulong expected, ulong data, ulong flags, ulong? vtable = null)
    {
        ulong install = data - AiExtensionStub.Page + AiExtensionStub.Installer;
        ulong before = ReadPointers(handle, data + AiExtensionStub.Config, 1)[0] & 7;
        ulong tag;
        do { tag = BitConverter.ToUInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)) & ~7UL; } while (tag == 0);
        WriteBytes(handle, data + 0x80, BitConverter.GetBytes(tag | flags));
        WriteBytes(handle, data + AiExtensionStub.Config, BitConverter.GetBytes(tag | before));
        bool completed = false;
        try
        {
            (string? why, int result) = RunOnFrame(handle, identity, _ => new(install, AiExtensionStub.Install(data), node, expected,
                [], NeedsPlayer: false, More: [vtable ?? data + AiExtensionStub.Vtable, tag | before]));
            if (why is not null || result != 1) throw new InvalidOperationException(why ?? L.AiExtensions.NotApplied);
            completed = true;
        }
        finally
        {
            // Removing the tag also cancels a dispatched call that has not reached its configuration CAS yet.
            WriteBytes(handle, data + AiExtensionStub.Config, BitConverter.GetBytes(completed ? flags : before));
        }
    }
}

using System.Runtime.InteropServices;

namespace San14Tenkazu;

internal static partial class GameMemory
{
    private static readonly object RuntimeRuleLock = new();

    internal static RuntimeRuleSettings? ReadRuntimeRules(GameIdentity identity)
    {
        if (!identity.Verified || !IsSameGame(identity)) return null;
        GameCapabilities.Require(identity, GameFeature.RuntimeTraits);
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return RuntimeRulePatches.DetectFull((address, size) => ReadBytes(handle, address, size), identity.ModuleBase); }
        finally { CloseHandle(handle); }
    }

    internal static bool RuntimeExperienceNeedsReapply(GameIdentity identity, GameLayout layout, RuntimeRuleSettings settings)
    {
        if (!settings.Growth) return false;
        GameCapabilities.Require(identity, GameFeature.RuntimeAbilities);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle = OpenRead(identity.ProcessId);
        try
        {
            var current = ReadCurrentLayout(identity);
            if (!layout.SameStorage(current)) throw new InvalidOperationException(L.RuntimeErrors.Loading);
            for (int slot = 1; slot < current.OfficerCount; slot++)
            {
                byte[] record = ReadBytes(handle, current.OfficerArray + (ulong)(slot * OfficerStride), OfficerStride);
                if (BitConverter.ToUInt64(record) != identity.Classes.Officer) throw new InvalidDataException(L.RuntimeErrors.OfficerTableChanged);
                if (RuntimeRuleExperience.NeedsReapply(record, settings)) return true;
            }
            return false;
        }
        finally { CloseHandle(handle); }
    }

    internal static int ApplyRuntimeRules(GameIdentity identity, RuntimeRuleSettings wanted, string journalPath, bool convertExperience, bool reapply = false)
        => ApplyRuntimeRulesCore(identity, wanted, journalPath, convertExperience, reapply, beforeLoad: false);

    // Called only by the explicit fresh-process preparation flow, before the user loads a save.
    // This transaction changes rules, never officer data or installation files.
    internal static void PrepareRuntimeRules(GameIdentity identity, RuntimeRuleSettings wanted, string journalPath)
        => ApplyRuntimeRulesCore(identity, wanted, journalPath, false, false, beforeLoad: true);

    internal static bool TryPrepareRuntimeRulesAutomatically(GameIdentity identity, RuntimeRuleSettings wanted, string journalPath)
        => ApplyRuntimeRulesCore(identity, wanted, journalPath, false, false, beforeLoad: true, requireNoData: true) >= 0;

    private static int ApplyRuntimeRulesCore(GameIdentity identity, RuntimeRuleSettings wanted, string journalPath, bool convertExperience, bool reapply, bool beforeLoad, bool requireNoData = false)
    {
        wanted.Validate();
        if (!identity.Verified || !IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        GameCapabilities.Require(identity, GameFeature.RuntimeAbilities);
        if (wanted.TraitList9) GameCapabilities.Require(identity, GameFeature.TraitList);
        lock (RuntimeRuleLock)
        {
            var journal = new RuntimeRuleJournal(journalPath);
            var receipt = journal.Read();
            var previous = receipt?.ExpectedFor(identity) ?? RuntimeRuleSettings.Original;
            // A fully verified original state can be reapplied after a reset. Unknown/partial
            // states still fail verification, and prepared recovery receipts are never adopted.
            if (reapply && ReadRuntimeRules(identity) == RuntimeRuleSettings.Original) previous = RuntimeRuleSettings.Original;
            ulong listAllocation = receipt?.Owns(identity) == true ? receipt.TraitListAllocation : 0;
            // Both explicit changes and saved-preference reconciliation use the same paused transaction.
            GameLayout? layout = beforeLoad ? null : ReadCurrentLayout(identity);
            if (!beforeLoad && ReadPace(identity, layout!) != GamePace.Paused) throw new InvalidOperationException(L.RuntimeErrors.PauseRequired);
            IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation | 0x0800, false, identity.ProcessId);
            if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
            bool newListAllocation = false, retainListAllocation = false;
            try
            {
                var ranges = RuntimeRulePatches.All.Where(p => !p.Bytes(previous).SequenceEqual(p.Bytes(wanted))
                        || (previous.Growth || wanted.Growth) && p.CodeStart == GameBuildFacts.Runtime.AbilityCodeStart)
                    .Select(p => (identity.ModuleBase + p.CodeStart, identity.ModuleBase + p.CodeEnd)).ToList();
                if (previous.TraitList9 || wanted.TraitList9)
                    ranges.Add((identity.ModuleBase + RuntimeTraitList.CodeStart, identity.ModuleBase + RuntimeTraitList.CodeEnd));
                if (listAllocation != 0) ranges.Add((listAllocation, listAllocation + RuntimeTraitList.CodeSize));
                using var freeze = RuntimeProcessFreeze.Acquire(handle, identity.ProcessId,
                    ranges.ToArray());
                if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
                // Recheck under the same freeze as the write. If loading completed since
                // observation, defer to the connected transaction so XP is not bypassed.
                if (requireNoData && TryReadCurrentLayout(identity) is not null) return -1;
                using var memory = new RuntimeProcessMemory(handle);
                RuntimeRulePatches.VerifyFull(memory.Read, identity.ModuleBase, previous);
                ulong activeList = RuntimeTraitList.Detect(memory.Read, identity.ModuleBase);
                if (activeList != 0 && activeList != listAllocation)
                    throw new InvalidDataException(L.RuntimeErrors.TraitOwnershipChanged);
                if (listAllocation != 0)
                {
                    RuntimeTraitList.VerifyAllocation(memory.Read, identity.ModuleBase, listAllocation);
                }
                if (previous.TraitList9 || wanted.TraitList9)
                    RuntimeTraitList.VerifyOriginalTables(memory.Read, identity.ModuleBase, activeList);
                GameLayout? current = null;
                if (!beforeLoad)
                {
                    ulong root = ReadPointers(handle, identity.ModuleBase + BasePointerRva, 1)[0];
                    current = root == 0 ? null : TryBuildLayout(handle, identity, VerifiedTables(root + BaseAdjust));
                    if (current is null || !layout!.SameStorage(current)) throw new InvalidOperationException(L.RuntimeErrors.Loading);
                    if (ReadPace(identity, current) != GamePace.Paused) throw new InvalidOperationException(L.RuntimeErrors.PaceChanged);
                }
                if (listAllocation != 0) ProtectRuntimeTraitList(handle, listAllocation);
                var writes = RuntimeRulePatches.All.Select(p => new RuntimeRuleWrite(identity.ModuleBase + p.Rva,
                    p.Bytes(previous), p.Bytes(wanted), p.Rva < GameBuildFacts.Runtime.ExecutableLimit))
                    .Where(p => !p.Before.SequenceEqual(p.After)).ToList();
                int conversions = 0;
                if (!beforeLoad && (wanted.Growth || previous.Growth))
                {
                    // Include every actual officer record, including unnamed/not-yet-appeared ones.
                    for (int slot = 1; slot < current!.OfficerCount; slot++)
                    {
                        ulong address = current.OfficerArray + (ulong)(slot * OfficerStride);
                        byte[] record = memory.Read(address, OfficerStride);
                        if (BitConverter.ToUInt64(record) != identity.Classes.Officer) throw new InvalidDataException(L.RuntimeErrors.OfficerTableChanged);
                        var converted = RuntimeRuleExperience.Plan(record, address, previous, wanted, reapply, convertExperience);
                        writes.AddRange(converted); conversions += converted.Length;
                    }
                }
                if (wanted.TraitList9 && listAllocation == 0)
                {
                    listAllocation = AllocateRuntimeTraitList(handle, identity.ModuleBase,
                        memory.Read(identity.ModuleBase + RuntimeTraitList.Definition.Columns.Rva, RuntimeTraitList.Definition.Columns.Bytes.Length / 2));
                    newListAllocation = true;
                }
                if (previous.TraitList9 != wanted.TraitList9)
                    writes.Add(new(identity.ModuleBase + RuntimeTraitList.Site,
                        previous.TraitList9 ? RuntimeTraitList.Jump(listAllocation) : RuntimeTraitList.Original,
                        wanted.TraitList9 ? RuntimeTraitList.Jump(listAllocation) : RuntimeTraitList.Original, true));
                var transaction = new RuntimeRuleReceipt(identity.ProcessId, identity.StartTimeUtc, identity.ModuleBase,
                    identity.ExeHash, previous, wanted, "prepared", writes.ToArray(), listAllocation);
                try
                {
                    RuntimeRuleTransaction.Apply(memory, writes, () => { journal.Save(transaction); retainListAllocation = true; }, () =>
                    {
                        RuntimeRulePatches.VerifyFull(memory.Read, identity.ModuleBase, wanted);
                        journal.Save(transaction with { Phase = "committed" });
                    });
                }
                catch (RuntimeRuleRecoveryException) { throw; } // Keep prepared bytes for explicit forensic recovery.
                catch
                {
                    journal.Save(transaction with { Phase = "rolled-back" });
                    retainListAllocation = true;
                    throw;
                }
                return conversions;
            }
            finally
            {
                // Once recorded/exposed, cached list objects can retain pointers even after OFF.
                // Reuse the same allocation for later ON; the OS reclaims it when the game exits.
                try
                {
                    if (newListAllocation && !retainListAllocation
                        && !VirtualFreeEx(handle, (IntPtr)unchecked((long)listAllocation), UIntPtr.Zero, MemRelease))
                        throw AccessError(L.RuntimeErrors.UnusedAllocationReleaseFailed);
                }
                finally { CloseHandle(handle); }
            }
        }
    }

    internal static ulong AllocateRuntimeTraitList(IntPtr handle, ulong module, byte[]? currentColumns = null)
    {
        IntPtr allocation = VirtualAllocEx(handle, IntPtr.Zero, (UIntPtr)RuntimeTraitList.Size, MemCommit | MemReserve, PageReadWrite);
        if (allocation == IntPtr.Zero) throw AccessError(L.RuntimeErrors.AllocationFailed);
        ulong address = unchecked((ulong)allocation.ToInt64());
        try
        {
            byte[] bytes = RuntimeTraitList.Build(module, address, currentColumns);
            WriteBytes(handle, address, bytes);
            if (!VirtualProtectEx(handle, allocation, (UIntPtr)RuntimeTraitList.Size, 0x02, out _)
                || !VirtualProtectEx(handle, allocation, (UIntPtr)RuntimeTraitList.CodeSize, PageExecuteRead, out _)
                || !FlushInstructionCache(handle, allocation, (UIntPtr)RuntimeTraitList.CodeSize))
                throw AccessError(L.RuntimeErrors.AllocationProtectionFailed);
            ProtectRuntimeTraitList(handle, address);
            RuntimeTraitList.VerifyAllocation((at, length) => ReadBytes(handle, at, length), module, address);
            return address;
        }
        catch
        {
            if (!VirtualFreeEx(handle, allocation, UIntPtr.Zero, MemRelease)) throw AccessError(L.RuntimeErrors.InitializationReleaseFailed);
            throw;
        }
    }

    internal static void ProtectRuntimeTraitList(IntPtr handle, ulong address)
    {
        // Validate the entire owned allocation before changing any page, including reuse of old receipts.
        for (int offset = 0; offset < RuntimeTraitList.Size; offset += 4096)
        {
            byte[] info = new byte[48];
            if (VirtualQueryEx(handle, (IntPtr)unchecked((long)(address + (uint)offset)), info, (UIntPtr)48) == UIntPtr.Zero
                || BitConverter.ToUInt64(info, 8) != address || BitConverter.ToUInt32(info, 32) != MemCommit
                || BitConverter.ToUInt32(info, 40) != 0x20000)
                throw new InvalidDataException(L.RuntimeErrors.AllocationOwnershipChanged);
        }
        for (int offset = 0; offset < RuntimeTraitList.Size; offset += 4096)
        {
            uint protection = offset < RuntimeTraitList.CodeSize ? PageExecuteRead
                : RuntimeTraitList.WritablePages.Contains(offset) ? PageReadWrite : 0x02;
            IntPtr page = (IntPtr)unchecked((long)(address + (uint)offset));
            if (!VirtualProtectEx(handle, page, (UIntPtr)4096, protection, out _))
                throw AccessError(L.RuntimeErrors.PageProtectionFailed);
            byte[] info = new byte[48];
            if (VirtualQueryEx(handle, page, info, (UIntPtr)48) == UIntPtr.Zero || BitConverter.ToUInt32(info, 36) != protection)
                throw new InvalidDataException(L.RuntimeErrors.PageProtectionUnconfirmed);
        }
    }

    internal sealed class RuntimeProcessMemory(IntPtr handle) : IRuntimeRuleMemory, IDisposable
    {
        private readonly Dictionary<ulong, uint> protections = [];
        public byte[] Read(ulong address, int length) => ReadBytes(handle, address, length);
        public void Write(RuntimeRuleWrite change, byte[] bytes)
        {
            var address = (IntPtr)unchecked((long)change.Address);
            try
            {
                for (ulong page = change.Address & ~4095UL; page < change.Address + (ulong)bytes.Length; page += 4096)
                {
                    if (!VirtualProtectEx(handle, (IntPtr)unchecked((long)page), (UIntPtr)4096, change.Executable ? 0x40U : 0x04U, out uint original))
                        throw AccessError(L.RuntimeErrors.MemoryProtectionFailed);
                    protections.TryAdd(page, original); // Keep the FIRST value even after a failed restore.
                }
                WriteBytes(handle, change.Address, bytes);
                if (change.Executable && !FlushInstructionCache(handle, address, (UIntPtr)bytes.Length))
                    throw AccessError(L.RuntimeErrors.InstructionCacheFailed);
            }
            finally
            {
                RestoreProtections();
            }
        }
        private void RestoreProtections()
        {
            var errors = new List<Exception>();
            foreach (var (page, protection) in protections)
                if (!VirtualProtectEx(handle, (IntPtr)unchecked((long)page), (UIntPtr)4096, protection, out _))
                    errors.Add(AccessError(L.RuntimeErrors.ProtectionRestoreFailed($"{page:X}")));
            if (errors.Count != 0) throw new AggregateException(errors);
        }
        public void Dispose() => RestoreProtections();
    }
}

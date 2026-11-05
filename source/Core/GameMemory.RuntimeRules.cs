using System.Runtime.InteropServices;

namespace San14Tenkazu;

internal static partial class GameMemory
{
    private static readonly object RuntimeRuleLock = new();

    internal static RuntimeRuleSettings? ReadRuntimeRules(GameIdentity identity)
    {
        if (!RuntimeRuleBuildProfile.Supports(identity) || !IsSameGame(identity)) return null;
        GameCapabilities.Require(identity, GameFeature.RuntimeTraits);
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return RuntimeRulePatches.DetectFull((address, size) => ReadBytes(handle, address, size), identity.ModuleBase, RuntimeRuleBuildProfile.Find(identity)); }
        finally { CloseHandle(handle); }
    }

    // Whether the connected game still needs the officer-data side of the saved rules: experience on the
    // wrong curve, levels clamped below their experience, or abilities/traits the original loader removed
    // that the located save still holds.
    internal static bool RuntimeRulesNeedReapply(GameIdentity identity, GameLayout layout, RuntimeRuleSettings settings, string? runtimeReceipt = null)
    {
        lock (RuntimeRuleLock) return RuntimeRulesNeedReapplyLocked(identity, layout, settings, runtimeReceipt);
    }

    private static bool RuntimeRulesNeedReapplyLocked(GameIdentity identity, GameLayout layout, RuntimeRuleSettings settings, string? runtimeReceipt)
    {
        if (!settings.Growth && !settings.Traits9 && !settings.Expanded) return false;
        GameCapabilities.Require(identity, GameFeature.RuntimeAbilities);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        var current = ReadCurrentLayout(identity);
        if (!layout.SameStorage(current)) throw new InvalidOperationException(L.RuntimeErrors.Loading);
        byte[]?[] records = ReadOfficerRecords(identity, current);
        var history = runtimeReceipt is null ? null : new SaveRestoreEdits(SaveRestoreEdits.PathFor(runtimeReceipt), identity, current);
        history?.Observe(address => address >= current.OfficerArray && (address - current.OfficerArray) % OfficerStride == 0
            && (address - current.OfficerArray) / OfficerStride < (ulong)records.Length
                ? records[(int)((address - current.OfficerArray) / OfficerStride)] : null);
        var save = settings.Traits9 || settings.Expanded ? GameSaveLocator.FindLoaded(records)?.Save : null;
        for (int slot = 1; slot < records.Length; slot++)
        {
            byte[] record = records[slot]!;
            if (settings.Growth && RuntimeRuleExperience.NeedsReapply(record, settings)) return true;
            ulong address = current.OfficerArray + (ulong)(slot * OfficerStride);
            if (save is not null && SaveRestore.Plan(record, address, save.Officers[slot], settings, history?.PreservesTraits(address, record) == true).Length != 0) return true;
        }
        return false;
    }

    private static byte[]?[] ReadOfficerRecords(GameIdentity identity, GameLayout layout)
    {
        IntPtr handle = OpenRead(identity.ProcessId);
        try
        {
            byte[] all = ReadBytes(handle, layout.OfficerArray, layout.OfficerCount * OfficerStride);
            var records = new byte[]?[layout.OfficerCount];
            for (int slot = 1; slot < layout.OfficerCount; slot++)
            {
                byte[] record = all.AsSpan(slot * OfficerStride, OfficerStride).ToArray();
                if (BitConverter.ToUInt64(record) != identity.Classes.Officer) throw new InvalidDataException(L.RuntimeErrors.OfficerTableChanged);
                records[slot] = record;
            }
            return records;
        }
        finally { CloseHandle(handle); }
    }

    // The Steam save the running game was loaded from, by its officer records; null when none matches.
    internal static GameSaveOfficers? FindLoadedSave(GameIdentity identity, GameLayout layout)
        => GameSaveLocator.FindLoaded(ReadOfficerRecords(identity, layout))?.Save;

    internal static RuntimeRuleApplyCounts ApplyRuntimeRules(GameIdentity identity, RuntimeRuleSettings wanted, string journalPath, bool convertExperience, bool reapply = false)
        => ApplyRuntimeRulesCore(identity, wanted, journalPath, convertExperience, reapply, beforeLoad: false)!;

    // Called only by the explicit fresh-process preparation flow, before the user loads a save.
    // This transaction changes rules, never officer data or installation files.
    internal static void PrepareRuntimeRules(GameIdentity identity, RuntimeRuleSettings wanted, string journalPath)
        => ApplyRuntimeRulesCore(identity, wanted, journalPath, false, false, beforeLoad: true);

    internal static bool TryPrepareRuntimeRulesAutomatically(GameIdentity identity, RuntimeRuleSettings wanted, string journalPath)
        => ApplyRuntimeRulesCore(identity, wanted, journalPath, false, false, beforeLoad: true, requireNoData: true) is not null;

    // Null only with requireNoData, when game data appeared before the write: the caller defers to the connected transaction.
    private static RuntimeRuleApplyCounts? ApplyRuntimeRulesCore(GameIdentity identity, RuntimeRuleSettings wanted, string journalPath, bool convertExperience, bool reapply, bool beforeLoad, bool requireNoData = false)
    {
        wanted.Validate();
        if (!RuntimeRuleBuildProfile.Supports(identity) || !IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        GameCapabilities.Require(identity, GameFeature.RuntimeAbilities);
        if (wanted.TraitList9) GameCapabilities.Require(identity, GameFeature.TraitList);
        var profile = RuntimeRuleBuildProfile.Find(identity);
        var traits = profile?.Traits;
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
            // Located before the freeze (file reads); every record is matched again under it before any write.
            GameSaveOfficers? save = !beforeLoad && (wanted.Traits9 || wanted.Expanded) ? FindLoadedSave(identity, layout!) : null;
            const uint access = ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation | 0x0800;
            IntPtr handle = profile is null ? OpenGameWrite(access, identity) : OpenProcess(access, false, identity.ProcessId);
            if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
            bool newListAllocation = false, retainListAllocation = false;
            try
            {
                var ranges = RuntimeRuleExecutingRanges(identity.ModuleBase, previous, wanted, profile);
                if (listAllocation != 0) ranges.Add((listAllocation, listAllocation + RuntimeTraitList.CodeSize));
                using var freeze = RuntimeProcessFreeze.Acquire(handle, identity.ProcessId,
                    ranges.ToArray());
                if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
                // Recheck under the same freeze as the write. If loading completed since
                // observation, defer to the connected transaction so XP is not bypassed.
                if (requireNoData && TryReadCurrentLayout(identity) is not null) return null;
                using var memory = new RuntimeProcessMemory(handle);
                RuntimeRulePatches.VerifyFull(memory.Read, identity.ModuleBase, previous, profile);
                ulong activeList = RuntimeTraitList.Detect(memory.Read, identity.ModuleBase, traits);
                if (activeList != 0 && activeList != listAllocation)
                    throw new InvalidDataException(L.RuntimeErrors.TraitOwnershipChanged);
                if (listAllocation != 0)
                {
                    RuntimeTraitList.VerifyAllocation(memory.Read, identity.ModuleBase, listAllocation, traits);
                }
                if (previous.TraitList9 || wanted.TraitList9)
                    RuntimeTraitList.VerifyOriginalTables(memory.Read, identity.ModuleBase, activeList, traits);
                GameLayout? current = null;
                if (!beforeLoad)
                {
                    current = ReadCurrentLayout(identity);
                    if (current is null || !layout!.SameStorage(current)) throw new InvalidOperationException(L.RuntimeErrors.Loading);
                    if (ReadPace(identity, current) != GamePace.Paused) throw new InvalidOperationException(L.RuntimeErrors.PaceChanged);
                }
                if (listAllocation != 0) ProtectRuntimeTraitList(handle, listAllocation);
                var writes = RuntimeRulePatches.Plan(identity.ModuleBase, previous, wanted, profile);
                if (profile is not null)
                {
                    RequireRuntimeRulePages(handle, identity, writes, previous, wanted, traits!);
                    if ((previous.Expanded || wanted.Expanded) && identity.DataProfile?.AbilityTable is { } abilityTable &&
                        !ReadOriginalAbilityTable(memory.Read, identity.ModuleBase, abilityTable, profile, previous))
                        throw new InvalidDataException(L.Compatibility.CurveUnknown);
                }
                int conversions = 0, restorations = 0, traitRestorations = 0;
                var history = current is null ? null : new SaveRestoreEdits(SaveRestoreEdits.PathFor(journalPath), identity, current);
                history?.Observe(address => memory.Read(address, OfficerStride));
                if (save is not null && save.Officers.Length != current!.OfficerCount) save = null;
                if (!beforeLoad && (wanted.Growth || previous.Growth || save is not null))
                {
                    // Include every actual officer record, including unnamed/not-yet-appeared ones.
                    for (int slot = 1; slot < current!.OfficerCount; slot++)
                    {
                        ulong address = current.OfficerArray + (ulong)(slot * OfficerStride);
                        byte[] record = memory.Read(address, OfficerStride);
                        if (BitConverter.ToUInt64(record) != identity.Classes.Officer) throw new InvalidDataException(L.RuntimeErrors.OfficerTableChanged);
                        var fromSave = save is null ? [] : SaveRestore.Plan(record, address, save.Officers[slot], wanted, history?.PreservesTraits(address, record) == true);
                        var converted = RuntimeRuleExperience.Plan(record, address, previous, wanted, reapply, convertExperience)
                            .Where(w => !fromSave.Any(s => s.Address == w.Address)).ToArray();
                        writes.AddRange(converted); writes.AddRange(fromSave);
                        int levels = converted.Count(RuntimeRuleExperience.IsLevelRestore) + fromSave.Count(w => !SaveRestore.IsTraitRestore(w));
                        restorations += levels; conversions += converted.Length - converted.Count(RuntimeRuleExperience.IsLevelRestore);
                        traitRestorations += fromSave.Count(SaveRestore.IsTraitRestore);
                    }
                }
                if (wanted.TraitList9 && listAllocation == 0)
                {
                    var columns = (traits?.Layout ?? RuntimeTraitList.Definition).Columns;
                    listAllocation = AllocateRuntimeTraitList(handle, identity.ModuleBase,
                        memory.Read(identity.ModuleBase + columns.Rva, columns.Bytes.Length / 2), traits);
                    newListAllocation = true;
                }
                if (previous.TraitList9 != wanted.TraitList9)
                    writes.Add(new(identity.ModuleBase + (traits?.Site ?? RuntimeTraitList.Site),
                        previous.TraitList9 ? RuntimeTraitList.Jump(listAllocation) : RuntimeTraitList.Original,
                        wanted.TraitList9 ? RuntimeTraitList.Jump(listAllocation) : RuntimeTraitList.Original, true));
                var transaction = new RuntimeRuleReceipt(identity.ProcessId, identity.StartTimeUtc, identity.ModuleBase,
                    identity.ExeHash, previous, wanted, "prepared", writes.ToArray(), listAllocation);
                try
                {
                    RuntimeRuleTransaction.Apply(memory, writes, () => { journal.Save(transaction); retainListAllocation = true; }, () =>
                    {
                        RuntimeRulePatches.VerifyFull(memory.Read, identity.ModuleBase, wanted, profile);
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
                return new(conversions, restorations, traitRestorations, restorations + traitRestorations > 0 && save is not null && writes.Any(w => SaveRestore.IsTraitRestore(w) || w.Before.Length == 1) ? save.Name : null);
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

    private static List<(ulong Start, ulong End)> RuntimeRuleExecutingRanges(ulong module, RuntimeRuleSettings previous,
        RuntimeRuleSettings wanted, RuntimeRuleBuildProfile? profile)
    {
        var ranges = (profile?.Patches ?? RuntimeRulePatches.All).Where(p =>
                !p.Bytes(previous).SequenceEqual(p.Bytes(wanted)) ||
                (previous.Growth || wanted.Growth) && p.CodeStart == (profile is null ? GameBuildFacts.Runtime.AbilityCodeStart : 0x2BC0D0))
            .Select(p => (module + p.CodeStart, module + p.CodeEnd)).Distinct().ToList();
        if (previous.TraitList9 || wanted.TraitList9)
            ranges.Add((module + (profile?.Traits.CodeStart ?? RuntimeTraitList.CodeStart),
                module + (profile?.Traits.CodeEnd ?? RuntimeTraitList.CodeEnd)));
        return ranges;
    }

    private static void RequireRuntimeRulePages(IntPtr handle, GameIdentity identity, IReadOnlyList<RuntimeRuleWrite> writes,
        RuntimeRuleSettings previous, RuntimeRuleSettings wanted, RuntimeTraitList.Profile traits)
    {
        foreach (var write in writes)
            RequireEditPage(handle, write.Address, write.Before.Length, write.Executable ? PageExecuteRead : 0x02, identity.ModuleBase);
        if (previous.TraitList9 != wanted.TraitList9)
            RequireEditPage(handle, identity.ModuleBase + traits.Site, RuntimeTraitList.Original.Length, PageExecuteRead, identity.ModuleBase);
        if (wanted.Growth && identity.DataProfile?.AbilityTable is { } table)
            foreach (var guard in table.Guards.Take(2))
            {
                ulong address = identity.ModuleBase + guard.Rva;
                RequireEditPage(handle, address, guard.Length, PageExecuteRead, identity.ModuleBase);
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ReadBytes(handle, address, guard.Length))) != guard.Sha256)
                    throw new InvalidDataException(L.Connection.EditCodeChanged);
            }
    }

    internal static ulong AllocateRuntimeTraitList(IntPtr handle, ulong module, byte[]? currentColumns = null,
        RuntimeTraitList.Profile? traits = null)
    {
        IntPtr allocation = VirtualAllocEx(handle, IntPtr.Zero, (UIntPtr)RuntimeTraitList.Size, MemCommit | MemReserve, PageReadWrite);
        if (allocation == IntPtr.Zero) throw AccessError(L.RuntimeErrors.AllocationFailed);
        ulong address = unchecked((ulong)allocation.ToInt64());
        try
        {
            byte[] bytes = RuntimeTraitList.Build(module, address, currentColumns, traits);
            WriteBytes(handle, address, bytes);
            if (!VirtualProtectEx(handle, allocation, (UIntPtr)RuntimeTraitList.Size, 0x02, out _)
                || !VirtualProtectEx(handle, allocation, (UIntPtr)RuntimeTraitList.CodeSize, PageExecuteRead, out _)
                || !FlushInstructionCache(handle, allocation, (UIntPtr)RuntimeTraitList.CodeSize))
                throw AccessError(L.RuntimeErrors.AllocationProtectionFailed);
            ProtectRuntimeTraitList(handle, address);
            RuntimeTraitList.VerifyAllocation((at, length) => ReadBytes(handle, at, length), module, address, traits);
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

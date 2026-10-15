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

    internal static int ApplyRuntimeRules(GameIdentity identity, RuntimeRuleSettings wanted, string journalPath, bool convertExperience, bool reapply = false)
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
            GameLayout layout = ReadCurrentLayout(identity);
            if (ReadPace(identity, layout) != GamePace.Paused) throw new InvalidOperationException("게임을 일시 정지한 뒤 적용하세요.");
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
                using var memory = new RuntimeProcessMemory(handle);
                RuntimeRulePatches.VerifyFull(memory.Read, identity.ModuleBase, previous);
                ulong activeList = RuntimeTraitList.Detect(memory.Read, identity.ModuleBase);
                if (activeList != 0 && activeList != listAllocation)
                    throw new InvalidDataException("개성 목록 메모리의 소유 기록이 일치하지 않습니다.");
                if (listAllocation != 0)
                {
                    RuntimeTraitList.VerifyAllocation(memory.Read, identity.ModuleBase, listAllocation);
                }
                if (previous.TraitList9 || wanted.TraitList9)
                    RuntimeTraitList.VerifyOriginalTables(memory.Read, identity.ModuleBase, activeList);
                ulong root = ReadPointers(handle, identity.ModuleBase + BasePointerRva, 1)[0];
                var current = root == 0 ? null : TryBuildLayout(handle, identity, VerifiedTables(root + BaseAdjust));
                if (current is null || !layout.SameStorage(current)) throw new InvalidOperationException("로드 중에는 규칙을 변경할 수 없습니다.");
                if (ReadPace(identity, current) != GamePace.Paused) throw new InvalidOperationException("게임 진행 상태가 바뀌었습니다. 일시정지 후 다시 적용하세요.");
                if (listAllocation != 0) ProtectRuntimeTraitList(handle, listAllocation);
                var writes = RuntimeRulePatches.All.Select(p => new RuntimeRuleWrite(identity.ModuleBase + p.Rva,
                    p.Bytes(previous), p.Bytes(wanted), p.Rva < GameBuildFacts.Runtime.ExecutableLimit))
                    .Where(p => !p.Before.SequenceEqual(p.After)).ToList();
                int conversions = 0;
                if (wanted.Growth || previous.Growth)
                {
                    // Include every actual officer record, including unnamed/not-yet-appeared ones.
                    for (int slot = 1; slot < current.OfficerCount; slot++)
                    {
                        ulong address = current.OfficerArray + (ulong)(slot * OfficerStride);
                        byte[] record = memory.Read(address, OfficerStride);
                        if (BitConverter.ToUInt64(record) != identity.Classes.Officer) throw new InvalidDataException("무장 테이블이 변경됐습니다.");
                        for (int ability = 0; ability < 5; ability++)
                        {
                            int level = record[0xEC + ability], offset = 0x104 + ability * 4;
                            if (level <= 100) continue;
                            int experience = BitConverter.ToInt32(record, offset);
                            bool fromGrowth = previous.Growth && OfficerFields.FitsExperience(record, OfficerFields.Abilities[ability], true);
                            int adjusted;
                            try { adjusted = reapply ? AbilityProgress.ReapplyExperience(level, experience, ability == 4, wanted.Growth)
                                : AbilityProgress.ConvertExperience(level, experience, ability == 4, fromGrowth, wanted.Growth); }
                            catch (Exception error) when (error is InvalidDataException or OverflowException)
                            {
                                throw new InvalidDataException($"무장 ID {BitConverter.ToUInt16(record, OfficerFields.IdOffset)} · {OfficerFields.Abilities[ability].Label}: {error.Message}", error);
                            }
                            if (adjusted == experience) continue;
                            if (!convertExperience) throw new InvalidOperationException("200 성장 곡선에 맞춘 경험치 변환을 선택하세요.");
                            writes.Add(new(address + (uint)offset, BitConverter.GetBytes(experience), BitConverter.GetBytes(adjusted), false));
                            conversions++;
                        }
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
                        throw AccessError("사용하지 않은 개성 목록 메모리 해제 실패");
                }
                finally { CloseHandle(handle); }
            }
        }
    }

    internal static ulong AllocateRuntimeTraitList(IntPtr handle, ulong module, byte[]? currentColumns = null)
    {
        IntPtr allocation = VirtualAllocEx(handle, IntPtr.Zero, (UIntPtr)RuntimeTraitList.Size, MemCommit | MemReserve, PageReadWrite);
        if (allocation == IntPtr.Zero) throw AccessError("개성 목록 메모리 할당 실패");
        ulong address = unchecked((ulong)allocation.ToInt64());
        try
        {
            byte[] bytes = RuntimeTraitList.Build(module, address, currentColumns);
            WriteBytes(handle, address, bytes);
            if (!VirtualProtectEx(handle, allocation, (UIntPtr)RuntimeTraitList.Size, 0x02, out _)
                || !VirtualProtectEx(handle, allocation, (UIntPtr)RuntimeTraitList.CodeSize, PageExecuteRead, out _)
                || !FlushInstructionCache(handle, allocation, (UIntPtr)RuntimeTraitList.CodeSize))
                throw AccessError("개성 목록 메모리 보호/캐시 갱신 실패");
            ProtectRuntimeTraitList(handle, address);
            RuntimeTraitList.VerifyAllocation((at, length) => ReadBytes(handle, at, length), module, address);
            return address;
        }
        catch
        {
            if (!VirtualFreeEx(handle, allocation, UIntPtr.Zero, MemRelease)) throw AccessError("개성 목록 초기화 실패 후 메모리 해제 실패");
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
                throw new InvalidDataException("개성 목록 할당 범위/소유권이 변경됐습니다.");
        }
        for (int offset = 0; offset < RuntimeTraitList.Size; offset += 4096)
        {
            uint protection = offset < RuntimeTraitList.CodeSize ? PageExecuteRead
                : RuntimeTraitList.WritablePages.Contains(offset) ? PageReadWrite : 0x02;
            IntPtr page = (IntPtr)unchecked((long)(address + (uint)offset));
            if (!VirtualProtectEx(handle, page, (UIntPtr)4096, protection, out _))
                throw AccessError("개성 목록 페이지 보호 설정 실패");
            byte[] info = new byte[48];
            if (VirtualQueryEx(handle, page, info, (UIntPtr)48) == UIntPtr.Zero || BitConverter.ToUInt32(info, 36) != protection)
                throw new InvalidDataException("개성 목록 페이지 보호 확인 실패");
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
                        throw AccessError("규칙 메모리 보호 변경 실패");
                    protections.TryAdd(page, original); // Keep the FIRST value even after a failed restore.
                }
                WriteBytes(handle, change.Address, bytes);
                if (change.Executable && !FlushInstructionCache(handle, address, (UIntPtr)bytes.Length))
                    throw AccessError("규칙 명령 캐시 갱신 실패");
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
                    errors.Add(AccessError($"규칙 메모리 보호 복원 실패: 0x{page:X}"));
            if (errors.Count != 0) throw new AggregateException(errors);
        }
        public void Dispose() => RestoreProtections();
    }
}

using System.Text.Json;

namespace San14Tenkazu;

// Addresses and instruction bytes are facts extracted from the verified Native build.
// Unlike the supplied mod: cap seeds and the seal special case are NOT changed. Two pure
// decode calls in GetAbility return 200 instead; existing and newly loaded caps stay intact.
internal sealed record RuntimePatch(uint Rva, string Before, uint GuardRva, string Guard,
    string Traits, string Cap, string Growth, uint CodeStart, uint CodeEnd)
{
    internal byte[] Bytes(RuntimeRuleSettings settings) => Convert.FromHexString(settings.Traits9 && Traits != Before
        ? Traits : settings.Abilities switch { AbilityRuleMode.Cap200 => Cap, AbilityRuleMode.Growth200 => Growth, _ => Before });
}

internal static class RuntimeRulePatches
{
    internal static void VerifyFull(Func<ulong, int, byte[]> read, ulong module, RuntimeRuleSettings settings)
    {
        Verify(read, module, settings);
        if ((RuntimeTraitList.Detect(read, module) != 0) != settings.TraitList9)
            throw new InvalidDataException("개성 목록 표시 규칙이 예상 상태와 다릅니다.");
    }
    internal static RuntimeRuleSettings? DetectFull(Func<ulong, int, byte[]> read, ulong module)
    {
        var state = Detect(read, module);
        if (state is null) return null;
        try
        {
            state = state with { TraitList9 = RuntimeTraitList.Detect(read, module) != 0 };
            state.Validate(); return state;
        }
        catch (InvalidDataException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
    internal static readonly RuntimePatch[] All = Load();
    private static RuntimePatch[] Load()
    {
        using var stream = typeof(RuntimeRulePatches).Assembly.GetManifestResourceStream("RuntimeRules.patches.json")!;
        return JsonSerializer.Deserialize<RuntimePatch[]>(stream)!;
    }

    internal static byte[] Guard(RuntimePatch site, RuntimeRuleSettings settings)
    {
        byte[] result = Convert.FromHexString(site.Guard);
        foreach (var patch in All)
        {
            byte[] bytes = patch.Bytes(settings);
            for (int i = 0; i < bytes.Length; i++)
            {
                long offset = (long)patch.Rva + i - site.GuardRva;
                if (offset >= 0 && offset < result.Length) result[offset] = bytes[i];
            }
        }
        return result;
    }

    internal static void Verify(Func<ulong, int, byte[]> read, ulong module, RuntimeRuleSettings settings)
    {
        settings.Validate();
        foreach (var site in All)
        {
            byte[] expected = Guard(site, settings);
            if (!read(module + site.GuardRva, expected.Length).SequenceEqual(expected))
                throw new InvalidDataException($"규칙 메모리 충돌: RVA 0x{site.Rva:X}. 원본 또는 천하도 소유 상태와 다릅니다.");
        }
    }

    internal static RuntimeRuleSettings? Detect(Func<ulong, int, byte[]> read, ulong module)
    {
        foreach (bool traits in new[] { false, true })
            foreach (var ability in Enum.GetValues<AbilityRuleMode>())
            {
                var settings = new RuntimeRuleSettings(traits, ability);
                try { Verify(read, module, settings); return settings; }
                catch (InvalidDataException) { }
            }
        return null;
    }
}

internal sealed record RuntimeRuleWrite(ulong Address, byte[] Before, byte[] After, bool Executable);

internal interface IRuntimeRuleMemory
{
    byte[] Read(ulong address, int length);
    // Must restore page protection, flush executable writes, and report any failure.
    void Write(RuntimeRuleWrite change, byte[] bytes);
}

internal sealed class RuntimeRuleRecoveryException(IEnumerable<Exception> errors)
    : AggregateException("규칙 적용 실패 및 복원 실패. 복구 기록을 확인하세요.", errors);

internal static class RuntimeRuleTransaction
{
    // The caller holds the process freeze for validation, journal persistence, writes and rollback.
    internal static void Apply(IRuntimeRuleMemory memory, IReadOnlyList<RuntimeRuleWrite> writes, Action prepare, Action commit)
    {
        foreach (var change in writes)
            if (!memory.Read(change.Address, change.Before.Length).SequenceEqual(change.Before))
                throw new InvalidDataException($"적용 전 메모리 변경: 0x{change.Address:X}");
        prepare();
        var attempted = new List<RuntimeRuleWrite>();
        try
        {
            foreach (var change in writes)
            {
                attempted.Add(change); // Include a partial/failed WriteProcessMemory in rollback.
                memory.Write(change, change.After);
                if (!memory.Read(change.Address, change.After.Length).SequenceEqual(change.After))
                    throw new IOException($"메모리 적용 검증 실패: 0x{change.Address:X}");
            }
            commit();
        }
        catch (Exception cause)
        {
            var failures = new List<Exception> { cause };
            foreach (var change in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    memory.Write(change, change.Before);
                    if (!memory.Read(change.Address, change.Before.Length).SequenceEqual(change.Before))
                        throw new IOException($"메모리 복원 검증 실패: 0x{change.Address:X}");
                }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count > 1) throw new RuntimeRuleRecoveryException(failures);
            throw;
        }
    }
}

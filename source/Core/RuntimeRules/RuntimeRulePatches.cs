using System.Text.Json;

namespace San14Tenkazu;

// Addresses and instruction bytes are facts extracted from the verified Native build.
// Unlike the supplied mod: cap seeds and the seal special case are NOT changed. Pure
// decode calls in GetAbility and the load normalizer return 200 instead; encoded caps stay intact.
internal sealed record RuntimePatch(uint Rva, string Before, uint GuardRva, string Guard,
    string Traits, string Cap, string Growth, uint CodeStart, uint CodeEnd)
{
    internal byte[] Bytes(RuntimeRuleSettings settings) => Convert.FromHexString(settings.Traits9 && Traits != Before
        ? Traits : settings.Abilities switch { AbilityRuleMode.Cap200 => Cap, AbilityRuleMode.Growth200 => Growth, _ => Before });
}

internal static class RuntimeRulePatches
{
    internal static void VerifyFull(Func<ulong, int, byte[]> read, ulong module, RuntimeRuleSettings settings, RuntimeRuleBuildProfile? profile = null)
    {
        Verify(read, module, settings, profile);
        if ((RuntimeTraitList.Detect(read, module, profile?.Traits) != 0) != settings.TraitList9)
            throw new InvalidDataException(L.RuntimeErrors.TraitRuleStateChanged);
    }
    internal static RuntimeRuleSettings? DetectFull(Func<ulong, int, byte[]> read, ulong module, RuntimeRuleBuildProfile? profile = null)
    {
        var state = Detect(read, module, profile);
        if (state is null) return null;
        try
        {
            state = state with { TraitList9 = RuntimeTraitList.Detect(read, module, profile?.Traits) != 0 };
            state.Validate(); return state;
        }
        catch (InvalidDataException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
    internal static readonly RuntimePatch[] All = Load();
    private static RuntimePatch[] Load()
    {
        return JsonSerializer.Deserialize<RuntimePatch[]>(GameBuildProfiles.Current.ReadResource("RuntimeRules.patches.json"))!;
    }

    internal static byte[] Guard(RuntimePatch site, RuntimeRuleSettings settings, RuntimeRuleBuildProfile? profile = null)
    {
        byte[] result = Convert.FromHexString(site.Guard);
        foreach (var patch in profile?.Patches ?? All)
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

    internal static void Verify(Func<ulong, int, byte[]> read, ulong module, RuntimeRuleSettings settings, RuntimeRuleBuildProfile? profile = null)
    {
        settings.Validate();
        foreach (var site in profile?.Patches ?? All)
        {
            byte[] expected = Guard(site, settings, profile);
            if (!read(module + site.GuardRva, expected.Length).SequenceEqual(expected))
                throw new InvalidDataException(L.RuntimeErrors.PatchConflict($"{site.Rva:X}"));
        }
    }

    internal static RuntimeRuleSettings? Detect(Func<ulong, int, byte[]> read, ulong module, RuntimeRuleBuildProfile? profile = null)
    {
        foreach (bool traits in new[] { false, true })
            foreach (var ability in Enum.GetValues<AbilityRuleMode>())
            {
                var settings = new RuntimeRuleSettings(traits, ability);
                try { Verify(read, module, settings, profile); return settings; }
                catch (InvalidDataException) { }
            }
        return null;
    }

    internal static List<RuntimeRuleWrite> Plan(ulong module, RuntimeRuleSettings previous, RuntimeRuleSettings wanted,
        RuntimeRuleBuildProfile? profile) => (profile?.Patches ?? All)
            .Select(p => new RuntimeRuleWrite(module + p.Rva, p.Bytes(previous), p.Bytes(wanted),
                p.Rva < (profile?.ExecutableLimit ?? GameBuildFacts.Runtime.ExecutableLimit)))
            .Where(p => !p.Before.SequenceEqual(p.After)).ToList();

    // Check the exact active instructions before hashing the full original function.
    internal static bool Normalize(ulong rva, byte[] bytes, RuntimeRuleBuildProfile profile, RuntimeRuleSettings settings)
    {
        foreach (var patch in profile.Patches)
        {
            int length = patch.Before.Length / 2;
            ulong start = Math.Max(rva, patch.Rva);
            ulong end = Math.Min(rva + (ulong)bytes.Length, patch.Rva + (ulong)length);
            if (start >= end) continue;
            int offset = (int)(start - patch.Rva), count = (int)(end - start);
            var observed = bytes.AsSpan((int)(start - rva), count);
            if (!observed.SequenceEqual(patch.Bytes(settings).AsSpan(offset, count))) return false;
            Convert.FromHexString(patch.Before).AsSpan(offset, count).CopyTo(observed);
        }
        return true;
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
    : AggregateException(L.RuntimeErrors.ApplyAndRestoreFailed, errors);

internal static class RuntimeRuleTransaction
{
    // The caller holds the process freeze for validation, journal persistence, writes and rollback.
    internal static void Apply(IRuntimeRuleMemory memory, IReadOnlyList<RuntimeRuleWrite> writes, Action prepare, Action commit)
    {
        foreach (var change in writes)
            if (!memory.Read(change.Address, change.Before.Length).SequenceEqual(change.Before))
                throw new InvalidDataException(L.RuntimeErrors.MemoryChangedBeforeApply($"{change.Address:X}"));
        prepare();
        var attempted = new List<RuntimeRuleWrite>();
        try
        {
            foreach (var change in writes)
            {
                attempted.Add(change); // Include a partial/failed WriteProcessMemory in rollback.
                memory.Write(change, change.After);
                if (!memory.Read(change.Address, change.After.Length).SequenceEqual(change.After))
                    throw new IOException(L.RuntimeErrors.WriteReadbackFailed($"{change.Address:X}"));
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
                        throw new IOException(L.RuntimeErrors.RestoreReadbackFailed($"{change.Address:X}"));
                }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count > 1) throw new RuntimeRuleRecoveryException(failures);
            throw;
        }
    }
}

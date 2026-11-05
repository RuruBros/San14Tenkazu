#if LOCAL_DEVELOPER
namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal static int RoundTripProfiledRuntimeRules(GameIdentity identity, GameLayout layout, GameSnapshot expected,
        Action<object> record)
    {
        var profile = RuntimeRuleBuildProfile.Find(identity) ?? throw new InvalidOperationException(L.Compatibility.Unavailable);
        var original = RuntimeRuleSettings.Original;
        var trials = (from ability in Enum.GetValues<AbilityRuleMode>()
                      from traits in new[] { (false, false), (true, false), (true, true) }
                      let settings = new RuntimeRuleSettings(traits.Item1, ability, traits.Item2)
                      where settings != original
                      select settings).ToArray();
        record(new { stage = "started", identity.ProcessId, identity.StartTimeUtc, identity.ExeHash, expected.Date, expected.Hour,
            expected.PlayerForce, modes = trials.Select(Mode).ToArray() });
        int passed = 0;
        foreach (var wanted in trials)
        {
            var mode = Mode(wanted);
            record(new { stage = "planned", mode });
            var receipt = WithProfiledEditSession(identity, layout, (handle, data) =>
            {
                if (expected.Session is not { IsPlaying: true, Entries.Length: >= 3 } previous ||
                    data.Session is not { IsPlaying: true, Entries.Length: >= 3 } current ||
                    current.Entries[2].Address != previous.Entries[2].Address ||
                    data.Date is null || data.Date != expected.Date ||
                    data.Hour != expected.Hour || data.PlayerForce <= 0 || data.PlayerForce != expected.PlayerForce)
                    throw new InvalidOperationException(L.Errors.StateMoved);
                using var memory = new RuntimeProcessMemory(handle);
                ulong module = identity.ModuleBase;
                var traits = profile.Traits;
                RuntimeRulePatches.VerifyFull(memory.Read, module, original, profile);
                RuntimeTraitList.VerifyOriginalTables(memory.Read, module, 0, traits);
                if (!data.OriginalAbilityTableVerified) throw new InvalidDataException(L.Compatibility.CurveUnknown);
                var writes = RuntimeRulePatches.Plan(module, original, wanted, profile);
                RequireRuntimeRulePages(handle, identity, writes, original, wanted, traits);
                ulong allocation = 0;
                bool keepAllocation = false;
                try
                {
                    if (wanted.TraitList9)
                    {
                        var columns = traits.Layout.Columns;
                        allocation = AllocateRuntimeTraitList(handle, module,
                            memory.Read(module + columns.Rva, columns.Bytes.Length / 2), traits);
                        writes.Add(new(module + traits.Site, RuntimeTraitList.Original, RuntimeTraitList.Jump(allocation), true));
                    }
                    try
                    {
                        RuntimeRuleTransaction.Apply(memory, writes, () => { }, () =>
                        {
                            RuntimeRulePatches.VerifyFull(memory.Read, module, wanted, profile);
                            if (RuntimeRulePatches.DetectFull(memory.Read, module, profile) != wanted)
                                throw new InvalidDataException(L.RuntimeErrors.TraitRuleStateChanged);
                            // Use the production failure recovery path while threads are still suspended.
                            throw new RuntimeTrialComplete();
                        });
                    }
                    catch (RuntimeTrialComplete) { }
                    catch (RuntimeRuleRecoveryException) { keepAllocation = true; throw; }
                    RuntimeRulePatches.VerifyFull(memory.Read, module, original, profile);
                    RuntimeTraitList.VerifyOriginalTables(memory.Read, module, 0, traits);
                    return writes.Select(w => new { rva = w.Address - module, length = w.Before.Length,
                        original = Hash(w.Before), applied = Hash(w.After), restored = Hash(memory.Read(w.Address, w.Before.Length)) }).ToArray();
                }
                finally
                {
                    if (!keepAllocation && allocation != 0 && !VirtualFreeEx(handle, (IntPtr)allocation, UIntPtr.Zero, MemRelease))
                        throw AccessError(L.RuntimeErrors.UnusedAllocationReleaseFailed);
                }
            }, RuntimeRuleExecutingRanges(identity.ModuleBase, original, wanted, profile).ToArray());
            record(new { stage = "restored-byte-exact", mode, ranges = receipt });
            passed++;
        }
        record(new { stage = "all-restored", passed });
        return passed;
    }

    private static object Mode(RuntimeRuleSettings settings) => new { ability = settings.Abilities.ToString(), settings.Traits9, settings.TraitList9 };
    private sealed class RuntimeTrialComplete : Exception;
}
#endif

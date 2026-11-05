using System.Security.Cryptography;

namespace San14Tenkazu;

internal static class ProfiledEdits
{
    internal static string? Block(GameDataProfile profile, IEnumerable<FieldDef> fields)
        => fields.FirstOrDefault(field => !profile.Supports(field)) is { } unsupported
            ? L.Connection.FieldNotVerified(unsupported.Label) : null;

    internal static void RequireFields(GameDataProfile profile, IReadOnlyList<RecordEdit> edits)
    {
        if (edits.Select(edit => edit.Address).Distinct().Count() != edits.Count)
            throw new InvalidDataException(L.Compatibility.Unavailable);
        foreach (var edit in edits)
        {
            if (edit.Changes.Count == 0 || edit.Changes.Any(c => c.Field.Record != edit.Kind) ||
                edit.Restarts is { Count: > 0 } || edit.MilitaryPoints is not null)
                throw new InvalidDataException(L.Compatibility.Unavailable);
            if (Block(profile, edit.Changes.Select(c => c.Field)) is { } unsupported)
                throw new InvalidDataException(unsupported);
        }
    }

    // Loyalty is a serialized byte; the ability-growth invariant applies to experience and abilities.
    internal static IEnumerable<string> CheckedGroups(IEnumerable<FieldDef> fields)
        => fields.Where(f => f != OfficerFields.Loyalty).Select(GameChecks.GroupOf).Distinct();

    internal static void RequireCurrentRecords(GameSnapshot data, IReadOnlyList<RecordEdit> edits)
    {
        foreach (var edit in edits)
        {
            if (edit.ExpectedRuntimeRules is { } rules && rules != data.RuntimeRules)
                throw new InvalidDataException(L.RuntimeErrors.RuntimeStateUnknown);
            var now = data.DataAt(edit.Kind, edit.Address);
            if (now is null || now.Length != edit.Expected.Length || !GameMemory.IsSameRecord(edit.Kind, now, edit.Expected))
                throw new InvalidOperationException(L.Errors.RecordMismatch(edit.Label));
            var moved = edit.Changes.Where(c => c.Field.Read(now) != c.Field.Read(edit.Expected))
                .Select(c => c.Field.Label).ToArray();
            if (moved.Length > 0) throw new StaleEditException(edit.Label, moved);
            if (edit.Kind != RecordKind.Officer) continue;
            RequireAbilityPairs(data, edit, now);
            foreach (var change in edit.Changes)
            {
                int slot = Array.IndexOf(OfficerFields.Personalities, change.Field);
                if (slot >= 0 && (data.RuntimeRules is not { } current || slot >= current.Slots || edit.ExpectedRuntimeRules != current))
                    throw new InvalidDataException(L.RuntimeErrors.RuntimeStateUnknown);
            }
        }
    }

    private static void RequireAbilityPairs(GameSnapshot data, RecordEdit edit, byte[] now)
    {
        var changes = edit.Changes.Where(c => OfficerFields.Abilities.Contains(c.Field) || OfficerFields.IsExperience(c.Field)).ToArray();
        if (changes.Length == 0) return;
        if (!data.OriginalAbilityTableVerified) throw new InvalidDataException(L.Compatibility.CurveUnknown);
        bool growth = data.RuntimeRules?.Growth == true;
        int cap = data.RuntimeRules?.Expanded == true ? 200 : 100;
        var after = (byte[])now.Clone();
        foreach (var change in changes) change.Field.Encode(change.Value).CopyTo(after, change.Field.Offset);
        foreach (var ability in OfficerFields.Abilities)
        {
            var experience = OfficerFields.ExperienceOf(ability);
            if (!changes.Any(c => c.Field == ability || c.Field == experience)) continue;
            if (ability.Read(now) != ability.Read(edit.Expected) || experience.Read(now) != experience.Read(edit.Expected))
                throw new StaleEditException(edit.Label, [ability.Label, experience.Label]);
            if (ability.Read(now) > cap || ability.Read(after) > cap ||
                !OfficerFields.FitsExperience(now, ability, growth) || !OfficerFields.FitsExperience(after, ability, growth))
                throw new InvalidDataException(L.Connection.EditOriginalAbility(ability.Label));
        }
    }
}

internal static partial class GameMemory
{
    private static T WithProfiledEditSession<T>(GameIdentity identity, GameLayout layout, Func<IntPtr, GameSnapshot, T> action,
        (ulong Start, ulong End)[]? executingRanges = null)
    {
        GameCapabilities.Require(identity, GameFeature.DataWrite);
        lock (RuntimeRuleLock)
        {
            if (!layout.SameStorage(ReadCurrentLayout(identity))) throw new InvalidOperationException(L.Errors.StateMoved);
            if (ReadPace(identity, layout) != GamePace.Paused) throw new InvalidOperationException(L.Connection.EditPauseRequired);
            uint access = ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation | 0x0800;
            IntPtr handle = OpenProcess(access, false, identity.ProcessId);
            if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
            try
            {
                using var freeze = RuntimeProcessFreeze.Acquire(handle, identity.ProcessId, executingRanges ?? []);
                if (!layout.SameStorage(ReadCurrentLayout(identity))) throw new InvalidOperationException(L.Errors.StateMoved);
                return action(handle, ReadSnapshot(identity, layout));
            }
            finally { CloseHandle(handle); }
        }
    }

    private static IReadOnlyList<string> ApplyProfiledEdits(IntPtr handle, GameIdentity identity, GameDataProfile profile,
        GameLayout layout, GameSnapshot data, IReadOnlyList<RecordEdit> edits)
    {
        ProfiledEdits.RequireFields(profile, edits);
        ValidateForceAgreementCalls(handle, identity, edits);
        ValidateProfiledOwnedItemCalls(handle, identity, edits);
        _ = AgreementPairs(layout.Forces, edits);
        ProfiledEdits.RequireCurrentRecords(data, edits);
        var fields = edits.SelectMany(edit => edit.Changes).Select(change => change.Field).ToArray();
        var catalog = ReadCatalog(identity, layout);
        if (GameChecks.WriteBlock(false, true, GameChecks.Run(data, catalog), ProfiledEdits.CheckedGroups(fields)) is { } blocked)
            throw new InvalidDataException(blocked);
        var broken = GameChecks.Broken(data, edits, catalog);
        if (broken.Count > 0) throw new InvalidDataException(L.Apply.BreaksTies(string.Join("\n", broken)));
        foreach (var guard in fields.Select(field => profile.Fields[field]).Distinct())
        {
            ulong address = checked(identity.ModuleBase + guard.Rva);
            RequireEditPage(handle, address, guard.Length, 0x20, identity.ModuleBase);
            byte[] code = ReadBytes(handle, address, guard.Length);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(code)), guard.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException(L.Connection.EditCodeChanged);
        }
        foreach (var edit in edits) RequireEditPage(handle, edit.Address, edit.Expected.Length, 0x04);
        return Apply(new ProcessMemory(handle), identity.Classes, edits, () =>
        {
            var after = RequirePlayingSession(identity);
            if (data.Session?.Entries[2].Address != after.Entries[2].Address)
                throw new InvalidOperationException(L.Errors.StateMoved);
            RequireSnapshotRoot(handle, identity, layout, profile, after);
        });
    }

    private static void RequireEditPage(IntPtr handle, ulong address, int length, uint protection, ulong allocation = 0)
    {
        byte[] info = new byte[48];
        if (VirtualQueryEx(handle, (IntPtr)address, info, (UIntPtr)info.Length).ToUInt64() != (ulong)info.Length)
            throw new InvalidOperationException(L.Connection.EditPageChanged);
        ulong start = BitConverter.ToUInt64(info, 0), size = BitConverter.ToUInt64(info, 24);
        if (start > address || size < (ulong)length || address - start > size - (ulong)length ||
            BitConverter.ToUInt32(info, 32) != MemCommit || BitConverter.ToUInt32(info, 36) != protection ||
            allocation != 0 && BitConverter.ToUInt64(info, 8) != allocation)
            throw new InvalidOperationException(L.Connection.EditPageChanged);
    }

#if LOCAL_DEVELOPER
    internal static void RoundTripProfiledCityGold(GameIdentity identity, GameLayout layout, int cityId, Action<object> record)
    {
        var profile = identity.DataProfile ?? throw new InvalidOperationException(L.Compatibility.Unavailable);
        WithProfiledEditSession(identity, layout, (handle, data) =>
        {
            if (!data.IsCity(cityId) || data.PlayerForce <= 0 || data.ForceOfCity(cityId) != data.PlayerForce)
                throw new InvalidOperationException(L.Errors.StateMoved);
            var city = data.Cities[cityId];
            long gold = city[CityFields.Gold];
            if (gold <= 0) throw new InvalidOperationException(L.Errors.StateMoved);
            var plan = CityEdits.Plan(data, cityId, new([new(CityFields.Gold, gold - 1)], [], null));
            if (plan.Refused) throw new InvalidOperationException(plan.Refusal);
            var edit = plan.Edits.Single();
            if (edit.Kind != RecordKind.City || edit.Address != city.Address || edit.Changes.Count != 1 ||
                edit.Changes[0] != new FieldChange(CityFields.Gold, gold - 1))
                throw new InvalidOperationException(L.Errors.StateMoved);
            var undo = Undo.Of(plan.Edits);
            byte[] ReadCity() => ReadBytes(handle, city.Address, city.Data.Length);
            void Receipt(string stage, byte[] bytes) => record(new
            {
                stage, identity.ProcessId, identity.ExeHash, cityId, city.Name, data.PlayerForce,
                address = city.Address, gold = CityFields.Gold.Read(bytes), sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            });
            byte[] before = ReadCity();
            if (!before.AsSpan().SequenceEqual(city.Data)) throw new InvalidOperationException(L.Errors.StateMoved);
            Receipt("before", before);
            ApplyProfiledEdits(handle, identity, profile, layout, data, plan.Edits);
            try
            {
                byte[] changed = ReadCity();
                if (!changed.AsSpan().SequenceEqual(undo.Single().Expected))
                    throw new InvalidDataException(L.Connection.EditCodeChanged);
                Receipt("changed-readback", changed);
            }
            finally
            {
                byte[] current = ReadCity();
                if (!current.AsSpan().SequenceEqual(undo.Single().Expected))
                    throw new InvalidOperationException(L.DeveloperUi.ProfiledEditRecoveryFailed);
                ApplyProfiledEdits(handle, identity, profile, layout, data.With(plan.Edits), undo);
                byte[] restored = ReadCity();
                if (!restored.AsSpan().SequenceEqual(before))
                    throw new InvalidOperationException(L.DeveloperUi.ProfiledEditRecoveryFailed);
                Receipt("restored-byte-exact", restored);
            }
            return true;
        });
    }
#endif
}

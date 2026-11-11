#if LOCAL_DEVELOPER
using System.Security.Cryptography;

namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal sealed record ProvocationGuard(ulong Address, string ExpectedHex);
    internal sealed record ProvocationPlan(int SchemaVersion, int ProcessId, DateTime StartTimeUtc, string ExeHash,
        ulong ModuleBase, int Year, int Month, int Day, int Hour, int PlayerForce, int Commander, int Slot, ulong Address,
        int SourceCommander, int SourceSlot, ulong SourceAddress, string SourceBeforeHex, string SourceName,
        string BeforeHex, string HeldHex, int ImmunityAll, int ImmunityProvocation, int ResistanceAll, int ResistanceProvocation,
        ProvocationGuard[] CodeGuards, ProvocationGuard[] InputGuards, bool NativeReplay, bool LiveNativeCall);
    internal sealed record ProvocationDigest(string Kind, ulong Address, string Label, string Sha256);
    internal sealed record ProvocationReceipt(GameIdentity Identity, GameLayout Layout, ProvocationPlan Plan,
        ProvocationDigest[] Records, Dictionary<string, string> Arrays);
    internal sealed record ProvocationFinishResult(int BeforeDuration, int DecreasedDuration, bool Restored, int SnapshotDifferences);
    private static readonly FieldDef ProvocationTargetKind = new(L.DeveloperUi.ProvocationTargetKind, RecordKind.Unit, 0x3C, FieldKind.Byte, 0, 255);
    private static readonly FieldDef ProvocationTargetNumber = new(L.DeveloperUi.ProvocationTargetNumber, RecordKind.Unit, 0x3E, FieldKind.UInt16, 0, ushort.MaxValue);
    private static FieldDef ProvocationDuration => UnitFields.Ailments[UnitFields.Provoked];
    private static FieldDef[] ProvocationFields => [ProvocationDuration, ProvocationTargetKind, ProvocationTargetNumber];
    private static string ProvocationHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static void ValidateProvocationPlan(ProvocationPlan plan)
    {
        byte[] before = Convert.FromHexString(plan.BeforeHex), held = Convert.FromHexString(plan.HeldHex);
        int[] allowed = [0x25, 0x3C, 0x3E, 0x3F];
        if (plan.SchemaVersion != 1 || !plan.ExeHash.Equals(GameNativeFactsNovember2026.ExeSha256, StringComparison.OrdinalIgnoreCase) ||
            !plan.NativeReplay || plan.LiveNativeCall || plan.ImmunityAll != 0 || plan.ImmunityProvocation != 0 ||
            before.Length != UnitStride || held.Length != UnitStride || before[0x25] != 0 || held[0x25] is < 2 or > 120 ||
            held[0x3C] != 27 || BitConverter.ToUInt16(held, 0x3E) != plan.SourceSlot || plan.SourceSlot is < 1 or > 500 ||
            BitConverter.ToUInt16(before, 0x12) != plan.Commander || plan.Address == plan.SourceAddress ||
            Enumerable.Range(0, before.Length).Any(i => !allowed.Contains(i) && before[i] != held[i]) ||
            plan.CodeGuards.Length == 0 || plan.InputGuards.Length == 0)
            throw new InvalidDataException(L.Errors.StateMoved);
    }

    private static void RequireProvocationWorld(GameIdentity identity, GameSnapshot data, ProvocationPlan plan, bool applying)
    {
        ValidateProvocationPlan(plan);
        if (identity.ProcessId != plan.ProcessId || identity.StartTimeUtc != plan.StartTimeUtc || identity.ModuleBase != plan.ModuleBase ||
            identity.ExeHash != plan.ExeHash || data.Session?.IsPlaying != true || data.Date != (plan.Year, plan.Month, plan.Day) ||
            data.Hour != plan.Hour || data.PlayerForce != plan.PlayerForce || data.RuntimeRules != new RuntimeRuleSettings() ||
            data.Unit(plan.Slot, plan.Commander) is not { } unit || unit.Address != plan.Address || data.ForceOfUnit(unit) != plan.PlayerForce ||
            data.Unit(plan.SourceSlot, plan.SourceCommander) is not { } source || source.Address != plan.SourceAddress ||
            data.ForceOfUnit(source) == plan.PlayerForce || !source.Data.AsSpan().SequenceEqual(Convert.FromHexString(plan.SourceBeforeHex)) ||
            applying && !unit.Data.AsSpan().SequenceEqual(Convert.FromHexString(plan.BeforeHex)))
            throw new InvalidOperationException(L.Errors.StateMoved);
    }

    private static void RequireProvocationCode(IntPtr handle, GameIdentity identity, ProvocationPlan plan)
    {
        foreach (var pin in new[] { (0x2F0E50UL, 1198, "D57A85B25E45D23FB3CF4EE9FDCE308405BB9C876FD37F6149DAFED9B9D91F7F"),
                                    (0x2CC790UL, 1231, "AAD088173EB2675FA0AF525AD46DF2C65627641FF2154F777D0218FE2BC88B59") })
        {
            ulong address = identity.ModuleBase + pin.Item1;
            RequireEditPage(handle, address, pin.Item2, 0x20, identity.ModuleBase);
            if (ProvocationHash(ReadBytes(handle, address, pin.Item2)) != pin.Item3) throw new InvalidDataException(L.Connection.EditCodeChanged);
        }
        foreach (var guard in plan.CodeGuards)
        {
            byte[] bytes = Convert.FromHexString(guard.ExpectedHex);
            RequireEditPage(handle, guard.Address, bytes.Length, 0x20, identity.ModuleBase);
            if (!ReadBytes(handle, guard.Address, bytes.Length).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException(L.Connection.EditCodeChanged);
        }
    }

    private static RecordEdit ProvocationEdit(ProvocationPlan plan, byte[] expected, byte[] after) =>
        new(RecordKind.Unit, plan.Address, expected, L.Terms.UnitOf(plan.Commander),
            ProvocationFields.Where(field => field.Read(expected) != field.Read(after)).Select(field => new FieldChange(field, field.Read(after))).ToArray());

    internal static void RestoreProvocationBytes(IRecordMemory memory, GameClasses classes, ProvocationPlan plan)
    {
        byte[] original = Convert.FromHexString(plan.BeforeHex), held = Convert.FromHexString(plan.HeldHex);
        byte[] current = memory.Read(plan.Address, original.Length);
        var mask = ProvocationFields.SelectMany(f => Enumerable.Range(f.Offset, f.Size)).ToHashSet();
        if (Enumerable.Range(0, original.Length).Any(i => !mask.Contains(i) && current[i] != original[i]) ||
            current[0x25] != original[0x25] && current[0x25] != held[0x25] && current[0x25] != held[0x25] - 1 ||
            current[0x3C] != original[0x3C] && current[0x3C] != held[0x3C] ||
            Enumerable.Range(0, 2).Any(i => current[0x3E + i] != original[0x3E + i] && current[0x3E + i] != held[0x3E + i]))
            throw new InvalidOperationException(L.DeveloperUi.ProfiledEditRecoveryFailed);
        var restore = ProvocationEdit(plan, current, original);
        if (restore.Changes.Count > 0) Apply(memory, classes, [restore]);
        if (!memory.Read(plan.Address, original.Length).AsSpan().SequenceEqual(original)) throw new IOException(L.DeveloperUi.ProfiledEditRecoveryFailed);
    }

    internal static void ApplyProvocationBytes(IRecordMemory memory, GameClasses classes, ProvocationPlan plan, Action<object> record)
    {
        ValidateProvocationPlan(plan);
        byte[] before = Convert.FromHexString(plan.BeforeHex), held = Convert.FromHexString(plan.HeldHex);
        if (!memory.Read(plan.Address, before.Length).AsSpan().SequenceEqual(before)) throw new InvalidOperationException(L.Errors.StateMoved);
        record(new { stage = "before-apply", plan.Address, actualHex = plan.BeforeHex, expectedHex = plan.BeforeHex });
        try
        {
            Apply(memory, classes, [ProvocationEdit(plan, before, held)]);
            byte[] actual = memory.Read(plan.Address, held.Length);
            record(new { stage = "held-readback", plan.Address, actualHex = Convert.ToHexString(actual), expectedHex = plan.HeldHex,
                nativeReplay = true, liveNativeCall = false, duration = actual[0x25], targetKind = actual[0x3C], targetNumber = BitConverter.ToUInt16(actual, 0x3E) });
            if (!actual.AsSpan().SequenceEqual(held)) throw new IOException(L.Connection.EditCodeChanged);
        }
        catch { RestoreProvocationBytes(memory, classes, plan); throw; }
    }

    internal static ProvocationReceipt BeginDeveloperProvocation(GameIdentity identity, GameLayout layout, ProvocationPlan plan,
        Action<ProvocationReceipt> persistBeforeWrite, Action<object> record) => WithProfiledEditSession(identity, layout, (handle, data) =>
    {
        RequireProvocationWorld(identity, data, plan, applying: true);
        RequireProvocationCode(handle, identity, plan);
        foreach (var guard in plan.InputGuards)
        {
            byte[] expected = Convert.FromHexString(guard.ExpectedHex);
            if (!ReadBytes(handle, guard.Address, expected.Length).AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException(L.Errors.RecordMismatch($"Native input 0x{guard.Address:X}"));
        }
        RequireEditPage(handle, plan.Address, UnitStride, 0x04);
        var receipt = new ProvocationReceipt(identity, layout, plan,
            CompleteSnapshotRecords(data).Select(r => new ProvocationDigest(r.Kind.ToString(), r.Address, r.Label, ProvocationHash(r.Before))).ToArray(),
            CompleteWorldArrays(data).ToDictionary(pair => pair.Key, pair => ProvocationHash(pair.Value)));
        persistBeforeWrite(receipt); // Durable recovery intent exists before any game write.
        ApplyProvocationBytes(new ProcessMemory(handle), identity.Classes, plan, record);
        return receipt;
    });

    internal static ProvocationFinishResult FinishDeveloperProvocation(ProvocationReceipt receipt, bool decrease, Action<object> record) =>
        WithProfiledEditSession<ProvocationFinishResult>(receipt.Identity, receipt.Layout, (handle, data) =>
    {
        var plan = receipt.Plan; var identity = receipt.Identity;
        RequireProvocationWorld(identity, data, plan, applying: false);
        RequireProvocationCode(handle, identity, plan);
        var memory = new ProcessMemory(handle);
        byte[] current = memory.Read(plan.Address, UnitStride), held = Convert.FromHexString(plan.HeldHex);
        int duration = held[0x25], reduced = duration;
        if (decrease && !current.AsSpan().SequenceEqual(held)) throw new InvalidOperationException(L.Errors.StateMoved);
        try
        {
            if (decrease)
            {
                var product = UnitEdits.Plan(data, plan.Slot, plan.Commander, [new(ProvocationDuration, duration - 1)]);
                if (product.Refused || product.Edits.Count != 1) throw new InvalidOperationException(product.Refusal);
                ApplyProfiledEdits(handle, identity, identity.DataProfile!, receipt.Layout, data, product.Edits);
                byte[] changed = memory.Read(plan.Address, UnitStride); reduced = changed[0x25];
                byte[] expected = (byte[])held.Clone(); expected[0x25]--;
                record(new { stage = "decreased-readback", plan.Address, actualHex = Convert.ToHexString(changed), expectedHex = Convert.ToHexString(expected) });
                if (!changed.AsSpan().SequenceEqual(expected)) throw new IOException(L.Connection.EditCodeChanged);
                ApplyProfiledEdits(handle, identity, identity.DataProfile!, receipt.Layout, data.With(product.Edits), Undo.Of(product.Edits));
                current = memory.Read(plan.Address, UnitStride);
                record(new { stage = "undo-readback", plan.Address, actualHex = Convert.ToHexString(current), expectedHex = plan.HeldHex });
                if (!current.AsSpan().SequenceEqual(held)) throw new IOException(L.Connection.EditCodeChanged);
            }
        }
        finally
        {
            RestoreProvocationBytes(memory, identity.Classes, plan);
            record(new { stage = "restored-readback", plan.Address, actualHex = Convert.ToHexString(memory.Read(plan.Address, UnitStride)), expectedHex = plan.BeforeHex });
        }
        var final = ReadSnapshot(identity, receipt.Layout);
        int differences = 0;
        var after = CompleteSnapshotRecords(final).ToDictionary(r => r.Address);
        foreach (var original in receipt.Records)
        {
            string? hash = after.TryGetValue(original.Address, out var now) ? ProvocationHash(now.Before) : null;
            bool same = hash == original.Sha256; if (!same) differences++;
            record(new { stage = "record-final", original.Address, original.Label, beforeSha256 = original.Sha256, afterSha256 = hash, matches = same });
        }
        foreach (var pair in CompleteWorldArrays(final))
        {
            string hash = ProvocationHash(pair.Value); bool same = receipt.Arrays[pair.Key] == hash; if (!same) differences++;
            record(new { stage = "array-final", name = pair.Key, beforeSha256 = receipt.Arrays[pair.Key], afterSha256 = hash, matches = same });
        }
        return new(duration, reduced, true, differences);
    });
}
#endif

namespace San14Tenkazu;

internal static partial class GameMemory
{
    private const ulong AgreementSetRva = 0x2EBCC0, AgreementBeginRva = 0x278270, AgreementEndRva = 0x292760;
    private const ulong AgreementOrdersRva = 0x2754A0, AgreementMapRva = 0x33CED0;
    private static readonly byte[] AgreementSetStart = Convert.FromHexString(
        "488BC441564883EC7048C740A8FEFFFFFF48895808488968104889701848897820418BE8488BF2488BF9488B02488BCA");
    private static readonly byte[] AgreementBeginStart = Convert.FromHexString(
        "48895C24204154415641574883EC20488B05925C50024C8BF94D8BE04C8BF2488B88D0000000");
    private static readonly byte[] AgreementEndStart = Convert.FromHexString(
        "488BC4554154415541564157488D68A14881EC9000000048C745E7FEFFFFFF488958084889701048897818458BE84C8BFA4C8BF1");
    private static readonly byte[] AgreementOrdersStart = Convert.FromHexString(
        "40534883EC20488B056B8A5002488B88900000004885C97426");
    private static readonly byte[] AgreementMapStart = Convert.FromHexString(
        "48895C24104889742418574881EC10080000488B05A7EC74014833C44889842400080000");

    // Refuse unsupported native code before any field is written. There is no raw-byte-only fallback.
    private static void ValidateForceAgreementCalls(IntPtr handle, GameIdentity identity, IReadOnlyList<RecordEdit> edits)
    {
        if (!edits.Any(e => e.Changes.Any(c => ForceFields.IsAgreement(c.Field)))) return;
        if (!identity.Verified) throw new InvalidOperationException(L.Force.AllianceNotRun(L.Calls.WhyUnverified));
        foreach ((ulong rva, byte[] start) in new (ulong, byte[])[]
        {
            (AppUpdateRva, AppUpdateStart), (AgreementSetRva, AgreementSetStart), (AgreementBeginRva, AgreementBeginStart),
            (AgreementEndRva, AgreementEndStart), (AgreementOrdersRva, AgreementOrdersStart), (AgreementMapRva, AgreementMapStart)
        })
            if (!CodeIs(handle, identity.ModuleBase + rva, start))
                throw new InvalidOperationException(L.Force.AllianceNotRun(L.Calls.WhyCode));
        if (ReadPointers(handle, identity.ModuleBase + AppUpdateSlotRva, 1)[0] != identity.ModuleBase + AppUpdateRva)
            throw new InvalidOperationException(L.Force.AllianceNotRun(L.Calls.WhyCode));
    }

    internal readonly record struct AgreementPair(int First, int Second, RecordEdit Own, FieldChange OwnChange,
        RecordEdit Other, FieldChange OtherChange)
    {
        internal int Before => (int)OwnChange.Field.Read(Own.Expected) & 0x7F;
        internal int Months => (int)OwnChange.Value & 0x7F;
    }

    // A pair must be present in both directions, including Undo. Do not synchronize a partial bilateral edit.
    internal static List<AgreementPair> AgreementPairs(ulong[] forces, IReadOnlyList<RecordEdit> edits)
    {
        var pairs = new List<AgreementPair>();
        var seen = new HashSet<(int, int)>();
        foreach (RecordEdit edit in edits.Where(e => e.Kind == RecordKind.Force))
            foreach (FieldChange change in edit.Changes.Where(c => ForceFields.IsAgreement(c.Field)))
            {
                int first = Array.IndexOf(forces, edit.Address), second = change.Field.Offset - ForceFields.AgreementOffset + 1;
                if (first == second || first is < 1 or > MaxPlayerForce || second is < 1 or > MaxPlayerForce)
                    throw new InvalidOperationException(L.Force.AllianceUnavailable);
                if (!seen.Add((Math.Min(first, second), Math.Max(first, second)))) continue;
                RecordEdit? other = edits.FirstOrDefault(e => e.Kind == RecordKind.Force && e.Address == forces[second]);
                FieldChange? counterpart = other?.Changes.Where(c => ForceFields.IsAgreement(c.Field)
                    && c.Field.Offset == ForceFields.AgreementOffset + first - 1).Select(c => (FieldChange?)c).SingleOrDefault();
                if (other is null || counterpart is not { } reverse || ((int)reverse.Value & 0x7F) != ((int)change.Value & 0x7F))
                    throw new InvalidOperationException(L.Force.AllianceUnavailable);
                if (((int)change.Value & 0x7F) > ForceFields.MaxAgreement
                    || !ForceFields.CanAlly(new ForceRecord(first, edit.Address, edit.Expected))
                    || !ForceFields.CanAlly(new ForceRecord(second, other.Address, other.Expected)))
                    throw new InvalidOperationException(L.Force.AllianceUnavailable);
                pairs.Add(new AgreementPair(first, second, edit, change, other, reverse));
            }
        return pairs;
    }

    // Native force editor 0x6228E9: set both periods, finish a new agreement with 0x278270, or end one with
    // 0x292760, then re-evaluate orders/map with 0x2754A0/0x33CED0. All calls run in the same game frame.
    // Command cleanup is the game's own effect; Undo restores the treaty bytes, not previously canceled commands.
    internal static IReadOnlyList<string> SyncForceAgreements(GameIdentity identity, GameLayout layout, IReadOnlyList<RecordEdit> edits)
    {
        List<AgreementPair> pairs = AgreementPairs(layout.Forces, edits);
        if (pairs.Count == 0) return [];
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try
        {
            ValidateForceAgreementCalls(handle, identity, edits);
            var log = new List<string>();
            foreach (AgreementPair pair in pairs)
            {
                void Check(RecordEdit edit, FieldChange change)
                {
                    byte[] fresh = ReadBytes(handle, edit.Address, edit.Expected.Length);
                    ValidateSameRecord(identity.Classes, edit, fresh);
                    if (change.Field.Read(fresh) != change.Value)
                        throw new InvalidOperationException(L.Errors.WriteMismatch(edit.Label, change.Field.Label, change.Field.Read(fresh)));
                }
                Check(pair.Own, pair.OwnChange);
                Check(pair.Other, pair.OtherChange);
                (string? why, _) = RunOnFrame(handle, identity, data =>
                {
                    ulong own = pair.Own.Address, other = pair.Other.Address, exe = identity.ModuleBase;
                    var after = new List<FrameFollowup>();
                    if (pair.Before == 0 && pair.Months > 0)
                        after.Add(new(new(exe + AgreementBeginRva, own, other, [data + StubArgs]), AgreementBeginStart));
                    else if (pair.Before > 0 && pair.Months == 0)
                        after.Add(new(new(exe + AgreementEndRva, own, other, [0]), AgreementEndStart));
                    after.Add(new(new(exe + AgreementOrdersRva, 0, 0), AgreementOrdersStart));
                    after.Add(new(new(exe + AgreementMapRva, 0, 0), AgreementMapStart));
                    return new FrameCall(exe + AgreementSetRva, AgreementSetStart, own, other, new byte[4], NeedsPlayer: false,
                        More: [(ulong)pair.Months], After: after.ToArray(), Stores:
                        [new(own + (ulong)pair.OwnChange.Field.Offset, (byte)pair.OwnChange.Value),
                         new(other + (ulong)pair.OtherChange.Field.Offset, (byte)pair.OtherChange.Value)]);
                });
                if (why is not null) throw new InvalidOperationException(L.Force.AllianceNotRun(why));
                Check(pair.Own, pair.OwnChange);
                Check(pair.Other, pair.OtherChange);
                log.Add(L.Force.AllianceSynced(pair.Own.Label, pair.Other.Label, pair.Months));
            }
            return log;
        }
        finally { CloseHandle(handle); }
    }
}

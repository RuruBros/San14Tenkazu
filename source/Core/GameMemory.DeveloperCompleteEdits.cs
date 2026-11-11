#if LOCAL_DEVELOPER
using System.Security.Cryptography;

namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal sealed record CompleteEditorPreparation(GameIdentity Identity, GameLayout Layout, GameSnapshot Snapshot,
        GameCatalog Catalog, CompleteEditPlan Plan);
    internal sealed record CompleteEditorResult(int Passed, int Missing, bool Cancelled, int SnapshotDifferences);

    internal static CompleteEditorPreparation PrepareCompleteEditorTrials(GameIdentity identity, GameLayout layout)
    {
        if (!identity.ExeHash.Equals(GameNativeFactsNovember2026.ExeSha256, StringComparison.OrdinalIgnoreCase) ||
            identity.DataProfile is null || !IsSameGame(identity) || !layout.SameStorage(ReadCurrentLayout(identity)) ||
            ReadPace(identity, layout) != GamePace.Paused) throw new InvalidOperationException(L.DeveloperUi.PauseRequired);
        var data = ReadSnapshot(identity, layout);
        if (data is not { PlayerForce: > 0, Date: not null, Session.IsPlaying: true } || data.Hour < 0)
            throw new InvalidOperationException(L.DeveloperUi.PauseRequired);
        var catalog = ReadCatalog(identity, layout);
        return new(identity, layout, data, catalog, PlanCompleteEditorTrials(data, catalog));
    }

    internal static object CompleteEditorManifest(CompleteEditorPreparation prepared, bool gameAccess = true) => new
    {
        schemaVersion = 1, stage = "target-manifest", gameAccess, utc = DateTime.UtcNow, prepared.Identity.ProcessId,
        prepared.Identity.StartTimeUtc, prepared.Identity.ExeHash, prepared.Snapshot.PlayerForce,
        prepared.Snapshot.Date, prepared.Snapshot.Hour, prepared.Snapshot.RuntimeRules,
        trials = prepared.Plan.Trials.Select(t => new { t.Key, t.Label, t.SeededProvocation, steps = t.Steps.Select(step => step.Select(e => new
        {
            kind = e.Kind.ToString(), e.Address, e.Label, changes = e.Changes.Select(c => new
                { field = c.Field.Label, c.Field.Offset, c.Field.Size, before = c.Field.Read(e.Expected), after = c.Value }),
            holders = e.Restarts
        })) }), missing = prepared.Plan.Missing
    };

    internal static CompleteEditorResult RoundTripCompleteEditorFields(CompleteEditorPreparation prepared,
        Action<object> record, CancellationToken cancellationToken)
    {
        var identity = prepared.Identity; var layout = prepared.Layout;
        var profile = identity.DataProfile ?? throw new InvalidOperationException(L.Compatibility.Unavailable);
        var baseline = prepared.Snapshot;
        int passed = 0;
        record(CompleteEditorManifest(prepared));
        foreach (var r in CompleteSnapshotRecords(baseline))
            record(new { stage = "suite-before", kind = r.Kind.ToString(), r.Address, r.Label,
                sha256 = Convert.ToHexString(SHA256.HashData(r.Before)) });
        var beforeArrays = CompleteWorldArrays(baseline);
        foreach (var pair in beforeArrays)
            record(new { stage = "suite-array-before", name = pair.Key, sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)) });
        foreach (var trial in prepared.Plan.Trials)
        {
            if (cancellationToken.IsCancellationRequested) break; // Finish recovery before honoring a stop.
            WithProfiledEditSession(identity, layout, (handle, data) =>
            {
                RequireCompleteTrialWorld(identity, layout, baseline, data);
                if (trial.SeededProvocation)
                {
                    var unit = data.Units.SingleOrDefault(u => u.Address == trial.Steps[0][0].Address);
                    if (unit is null || data.ForceOfUnit(unit) != data.PlayerForce || unit[UnitFields.Ailments[UnitFields.Provoked]] != 0)
                        throw new InvalidOperationException(L.Errors.StateMoved);
                }
                foreach (var step in trial.Steps)
                    ProfiledEdits.RequireFields(profile, step);
                // Use the planned targets, never silently substitute a different officer/unit.
                foreach (var edit in trial.Steps.SelectMany(s => s).DistinctBy(e => e.Address))
                {
                    var now = data.DataAt(edit.Kind, edit.Address);
                    if (now is null || !IsSameRecord(edit.Kind, now, edit.Expected) ||
                        edit.Changes.Select(c => c.Field).Concat(LinkedFields.Of(edit.Kind)).Distinct()
                            .Any(f => f.Read(now) != f.Read(edit.Expected)))
                        throw new InvalidOperationException(L.Errors.StateMoved);
                    if (edit.Restarts is not null && data.Personalities.Single(p => p.Address == edit.Address) is { } personality)
                    {
                        var fresh = PersonalityEdits.Plan(data, personality.Index, edit.Changes);
                        if (fresh.Refused || fresh.Edits.Count != 1 ||
                            !(fresh.Edits[0].Restarts ?? []).SequenceEqual(edit.Restarts))
                            throw new InvalidOperationException(L.Errors.StateMoved);
                    }
                }
                var scope = CompleteTrialRecords(data, trial);
                void Guard()
                {
                    if (!IsSameGame(identity) || !layout.SameStorage(ReadCurrentLayout(identity)))
                        throw new InvalidOperationException(L.Errors.ProcessChanged);
                    var current = RequirePlayingSession(identity);
                    if (current.Entries[2].Address != baseline.Session!.Entries[2].Address)
                        throw new InvalidOperationException(L.Errors.StateMoved);
                    RequireSnapshotRoot(handle, identity, layout, profile, current);
                    var fields = trial.Steps.SelectMany(s => s).SelectMany(e => e.Changes).Select(c => c.Field).ToArray();
                    ProfiledEdits.RequireCode(profile, [.. fields, OfficerFields.Loyalty], guard =>
                    {
                        ulong address = checked(identity.ModuleBase + guard.Rva);
                        RequireEditPage(handle, address, guard.Length, 0x20, identity.ModuleBase);
                        return ReadBytes(handle, address, guard.Length);
                    });
                    foreach (var r in scope.Where(r => r.Allowed.Length > 0)) RequireEditPage(handle, r.Address, r.Before.Length, 0x04);
                    foreach (var edit in trial.Steps.SelectMany(s => s).Where(e => e.MilitaryPoints is not null))
                    {
                        var military = edit.MilitaryPoints!;
                        if (military.Caps.Address != identity.ModuleBase + profile.MilitaryCapsRva ||
                            military.ForceAddress != layout.Forces[military.Force]) throw new InvalidOperationException(L.Force.MilitaryUnavailable);
                        RequireImageDataPage(handle, military.Caps.Address, MilitaryPointCaps.Size, identity.ModuleBase);
                        MilitaryIntelligence.Check(new ProcessMemory(handle), identity.Classes, edit, ReadBytes(handle, edit.Address, edit.Expected.Length));
                    }
                }
                record(new { stage = "trial-world", trial.Key, data.Date, data.Hour, data.PlayerForce, pace = "Paused before thread freeze",
                    session = data.Session?.Entries[2].Address });
                RunCompleteEditTrial(data, identity.Classes, trial, new ProcessMemory(handle),
                    (snapshot, edits, memory) => ApplyProfiledEdits(handle, identity, profile, layout, snapshot, edits, memory),
                    Guard, evidence => record(evidence));
                return true;
            });
            passed++;
        }
        int differences = WithProfiledEditSession(identity, layout, (_, data) =>
        {
            RequireCompleteTrialWorld(identity, layout, baseline, data);
            var beforeRecords = CompleteSnapshotRecords(baseline).ToDictionary(r => r.Address);
            var after = CompleteSnapshotRecords(data).ToDictionary(r => r.Address);
            int count = 0;
            foreach (var before in beforeRecords.Values)
            {
                bool same = after.TryGetValue(before.Address, out var now) && before.Before.AsSpan().SequenceEqual(now.Before);
                if (!same) count++;
                record(new { stage = "suite-after", kind = before.Kind.ToString(), before.Address, before.Label, matches = same,
                    beforeSha256 = Convert.ToHexString(SHA256.HashData(before.Before)),
                    afterSha256 = now is null ? null : Convert.ToHexString(SHA256.HashData(now.Before)),
                    beforeBytes = same ? null : Convert.ToHexString(before.Before), afterBytes = same || now is null ? null : Convert.ToHexString(now.Before) });
            }
            foreach (var extra in after.Values.Where(r => !beforeRecords.ContainsKey(r.Address)))
            { count++; record(new { stage = "suite-extra-record", extra.Address, extra.Label }); }
            foreach (var pair in CompleteWorldArrays(data))
            {
                var before = beforeArrays[pair.Key];
                bool same = before.AsSpan().SequenceEqual(pair.Value); if (!same) count++;
                record(new { stage = "suite-array-after", name = pair.Key, matches = same,
                    beforeSha256 = Convert.ToHexString(SHA256.HashData(before)), afterSha256 = Convert.ToHexString(SHA256.HashData(pair.Value)),
                    beforeBytes = same ? null : Convert.ToHexString(before), afterBytes = same ? null : Convert.ToHexString(pair.Value) });
            }
            return count;
        });
        bool cancelled = cancellationToken.IsCancellationRequested;
        record(new { stage = cancelled ? "cancelled-restored" : differences == 0 && prepared.Plan.Missing.Length == 0 ? "all-restored" : "incomplete",
            passed, missing = prepared.Plan.Missing.Length, cancelled, snapshotDifferences = differences });
        return new(passed, prepared.Plan.Missing.Length, cancelled, differences);
    }

    private static void RequireCompleteTrialWorld(GameIdentity identity, GameLayout layout, GameSnapshot baseline, GameSnapshot now)
    {
        if (now.Session?.IsPlaying != true || baseline.Session?.Entries[2].Address != now.Session.Entries[2].Address ||
            now.Date != baseline.Date || now.Hour != baseline.Hour || now.PlayerForce != baseline.PlayerForce ||
            now.RuntimeRules != baseline.RuntimeRules)
            throw new InvalidOperationException(L.Errors.StateMoved);
    }

    private static Dictionary<string, byte[]> CompleteWorldArrays(GameSnapshot data) => new()
    {
        ["tile-owners"] = data.TileOwners, ["tile-counties"] = data.TileCounties.SelectMany(BitConverter.GetBytes).ToArray(),
        ["corps-force"] = data.CorpsForce, ["corps-leaders"] = data.CorpsLeaders.SelectMany(BitConverter.GetBytes).ToArray()
    };
}
#endif

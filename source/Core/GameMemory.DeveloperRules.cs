#if LOCAL_DEVELOPER
using System.Security.Cryptography;

namespace San14Tenkazu;

internal static partial class GameMemory
{
    private const int ProfiledRuleTrialCount = 50;

    internal static int RoundTripProfiledRules(GameIdentity identity, GameLayout layout, GameSnapshot expected, Action<object> record)
    {
        if (identity.DataProfile is not { RuleGuards.IsDefaultOrEmpty: false } profile)
            throw new InvalidDataException(L.Rules.Incompatible);

        void RequireSameSession(GameSnapshot data)
        {
            if (expected.Session is not { IsPlaying: true, Entries.Length: >= 3 } session ||
                data.Session is not { IsPlaying: true, Entries.Length: >= 3 } current ||
                current.Entries[2].Address != session.Entries[2].Address ||
                data.Date is null || data.Date != expected.Date || data.Hour != expected.Hour ||
                data.PlayerForce <= 0 || data.PlayerForce != expected.PlayerForce)
                throw new InvalidOperationException(L.Errors.StateMoved);
        }

        RequireSameSession(ReadSnapshot(identity, layout));
        if (ReadPace(identity, layout) != GamePace.Paused) throw new InvalidOperationException(L.DeveloperUi.PauseRequired);
        var date = expected.Date!.Value;
        record(new { stage = "started", identity.ProcessId, identity.StartTimeUtc, identity.ExeHash,
            date = new { date.Year, date.Month, date.Day }, expected.Hour, expected.PlayerForce, registeredFields = ProfiledRuleTrialCount });
        // Resolve the whole suite before opening any write handle.
        var trials = PlanProfiledRuleTrials(identity, (feature, plan) => ReadRuleTable(identity, feature, plan));
        if (trials.Length != ProfiledRuleTrialCount) throw new InvalidDataException(L.Rules.Incompatible);
        foreach (var trial in trials)
        {
            object Receipt(string stage, RuleTrialRow row) => new
            {
                stage, trial.Key.Table, trial.Key.Record, trial.Key.Field, row.Name, row.Address, row.VectorAddress,
                recordSha256 = Hash(row.Data), vectorSha256 = Hash(row.Vector),
                values = trial.Changes.Select(c => new { c.Key.Field, value = row.Value(c.Key.Field), requested = c.After }).ToArray()
            };
            record(Receipt("planned", trial.Before));
            var result = WithProfiledRuleTable(identity, layout, profile, (memory, data) =>
            {
                RequireSameSession(data);
                return trial.Run(memory);
            });
            record(Receipt("changed-readback", result.Changed));
            record(Receipt("restored-byte-exact", result.Restored));
        }
        record(new { stage = "all-restored", passed = trials.Length });
        return trials.Length;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal sealed record RuleTrialRow(int Id, string Name, ulong Address, byte[] Data, ulong VectorAddress, byte[] Vector,
        Func<string, int> Value)
    {
        internal bool SameBytes(RuleTrialRow other) => Address == other.Address && VectorAddress == other.VectorAddress &&
            Data.AsSpan().SequenceEqual(other.Data) && Vector.AsSpan().SequenceEqual(other.Vector);
    }

    internal sealed record RuleTrial(RuleKey Key, RuleTrialRow Before, RuleChange[] Changes,
        Func<IRecordMemory, (RuleTrialRow Changed, RuleTrialRow Restored)> Run);

    internal static RuleTrial[] PlanProfiledRuleTrials(GameIdentity identity,
        Func<GameFeature, Func<IRecordMemory, RuleTrial[]>, RuleTrial[]> readTable)
    {
        RuleTrial[] Plan<T>(GameFeature feature, string table, IEnumerable<(string Id, FieldDef Input)> fields,
            Func<IRecordMemory, T> read, Func<IRecordMemory, T, RuleChange[], T> apply,
            Func<T, IEnumerable<RuleTrialRow>> rows) => readTable(feature, memory =>
        {
            T baseline = read(memory);
            var targets = rows(baseline).OrderBy(row => row.Id).ToArray();
            return fields.Select(field =>
            {
                var candidate = targets.Select(row => (Row: row, Changes: PlanRuleChange(table, row, field,
                        baseline is PolicySnapshot policies ? policies : null)))
                    .FirstOrDefault(candidate => candidate.Changes.Length != 0);
                if (candidate.Row is not { } before)
                    throw new InvalidDataException(L.DeveloperUi.ProfiledRuleTargetMissing(table, field.Id));
                RuleChange[] changes = candidate.Changes;
                var key = changes[0].Key;
                RuleTrialRow At(T snapshot) => rows(snapshot).SingleOrDefault(row => row.Id == key.Record)
                    ?? throw new InvalidOperationException(L.Rules.Stale);
                return new RuleTrial(key, before, changes, mem =>
                {
                    if (!before.SameBytes(At(read(mem)))) throw new InvalidOperationException(L.Rules.Stale);
                    T changed;
                    try { changed = apply(mem, baseline, changes); }
                    catch (Exception failure)
                    {
                        try { RequireRestored(At(read(mem))); }
                        catch (Exception recovery) { throw new AggregateException(L.Rules.RollbackFailed, failure, recovery); }
                        throw;
                    }
                    RuleTrialRow changedRow;
                    RuleTrialRow restored;
                    try
                    {
                        changedRow = At(read(mem));
                        if (!changedRow.SameBytes(At(changed)) || before.SameBytes(changedRow) ||
                            changes.Any(change => changedRow.Value(change.Key.Field) != change.After))
                            throw new IOException(L.Rules.WriteFailed);
                    }
                    finally
                    {
                        try
                        {
                            var reverse = changes.Select(change => new RuleChange(change.Key, change.After, change.Before)).ToArray();
                            apply(mem, changed, reverse);
                            restored = At(read(mem));
                            RequireRestored(restored);
                        }
                        catch (Exception recovery) { throw new IOException(L.DeveloperUi.ProfiledRuleRecoveryFailed, recovery); }
                    }
                    return (changedRow, restored);
                });
                void RequireRestored(RuleTrialRow restored)
                {
                    if (!before.SameBytes(restored)) throw new IOException(L.DeveloperUi.ProfiledRuleRecoveryFailed);
                }
            }).ToArray();
        });

        RuleTrial[] Policy(string table) => Plan(GameFeature.Policies, table, PolicyRules.Fields(table).Select(f => (f.Id, f.Input)),
            mem => PolicyMemory.Read(mem, identity), (mem, state, changes) => PolicyMemory.Apply(mem, identity, state, changes),
            state => state.Records.Where(r => r.Table == table && r.MirrorsAgree).Select(r =>
                new RuleTrialRow(r.Id, r.Name, r.Address, r.Data, r.VectorAddress, r.Vector, id => r.Value(PolicyRules.Field(new(table, r.Id, id))))));
        RuleTrial[] Plain(GameFeature feature, PlainRuleSpec spec) => Plan(feature, spec.Table,
            spec.Fields.Select(f => (f.Id, f.Input(spec.RecordKind))),
            mem => PlainRuleMemory.Read(mem, identity, spec), (mem, state, changes) => PlainRuleMemory.Apply(mem, identity, state, changes),
            state => state.Records.Select(r => new RuleTrialRow(r.Id, r.Name, r.Address, r.Data, 0, [], id => r.Value(spec.Field(id)))));

        return
        [
            .. Plan(GameFeature.RuleTables, "formation", FormationRules.Fields.Select(f => (f.Id, f.Input)),
                mem => FormationMemory.Read(mem, identity), (mem, state, changes) => FormationMemory.Apply(mem, identity, state, changes),
                state => state.Records.Where(r => r.MirrorsAgree).Select(r =>
                    new RuleTrialRow(r.Id, r.Name, r.Address, r.Data, r.VectorAddress, r.Vector, id => r.Value(FormationRules.Field(id))))),
            .. Plan(GameFeature.Tactics, "tactic", TacticRules.Fields.Select(f => (f.Id, f.Input)),
                mem => TacticMemory.Read(mem, identity), (mem, state, changes) => TacticMemory.Apply(mem, identity, state, changes),
                state => state.Records.Where(r => r.MirrorsAgree).Select(r =>
                    new RuleTrialRow(r.Id, r.Name, r.Address, r.Data, r.VectorAddress, r.Vector, id => r.Value(TacticRules.Field(id))))),
            .. Policy("policy"), .. Policy("policyeffect"),
            .. Plan(GameFeature.Offices, "office", OfficeRules.Fields.Select(f => (f.Id, f.Input)),
                mem => OfficeMemory.Read(mem, identity), (mem, state, changes) => OfficeMemory.Apply(mem, identity, state, changes),
                state => state.Records.Where(r => r.MirrorsAgree).Select(r =>
                    new RuleTrialRow(r.Id, r.Name, r.Address, r.Data, r.VectorAddress, r.Vector, id => r.Value(OfficeRules.Field(id))))),
            .. Plain(GameFeature.Ranks, RankRules.Spec), .. Plain(GameFeature.Doctrines, DoctrineRules.Spec)
        ];
    }

    private static RuleChange[] PlanRuleChange(string table, RuleTrialRow row, (string Id, FieldDef Input) field, PolicySnapshot? policies)
    {
        int before = row.Value(field.Id);
        int min = checked((int)field.Input.Min), max = checked((int)field.Input.Max);
        if (before < min || before > max || table == "doctrine" && !DoctrineRules.ValidCosts(row.Value)) return [];
        IEnumerable<int> values = table == "policy" && policies is not null
            ? policies.Records.Where(r => r.Table == "policyeffect").Select(r => r.Id).Append(0).OrderBy(id => Math.Abs(id - before))
            : new[] { before - 1, before + 1 };
        foreach (int value in values)
        {
            if (value == before || value < min || value > max ||
                table == "formation" && row.Vector.Length > 0 && (value == 0 || before == 0))
                continue;
            var changes = new List<RuleChange> { new(new(table, row.Id, field.Id), before, value) };
            if (table == "doctrine")
            {
                if (field.Id is "kind1" or "kind2" && value > 0)
                {
                    string cost = "cost" + field.Id[^1];
                    if (row.Value(cost) == 0) changes.Add(new(new(table, row.Id, cost), 0, 1));
                }
                if (!DoctrineRules.ValidCosts(id => changes.SingleOrDefault(c => c.Key.Field == id)?.After ?? row.Value(id))) continue;
            }
            return changes.ToArray();
        }
        return [];
    }
}
#endif

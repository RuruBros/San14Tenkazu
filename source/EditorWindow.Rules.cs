using WT = System.Windows.Threading;

namespace San14Tenkazu;

internal sealed record RuleUndo(RuleProfile Before, RuleProfile After, string Stamp, RuleChange[] Changes);
internal sealed record EditorUndo(string Summary, List<RecordEdit> Edits, RuleUndo? Rule = null)
{
    public void Deconstruct(out string summary, out List<RecordEdit> edits) { summary = Summary; edits = Edits; }
}

internal sealed record RuleGameAccess(
    Func<GameIdentity, FormationSnapshot> Read,
    Func<GameIdentity, GameLayout, GamePace> Pace,
    Func<GameIdentity, FormationSnapshot, RuleChange[], FormationSnapshot> Apply,
    Func<GameIdentity, TacticSnapshot>? ReadTactics = null,
    Func<GameIdentity, TacticSnapshot, RuleChange[], TacticSnapshot>? ApplyTactics = null,
    Func<GameIdentity, PolicySnapshot>? ReadPolicies = null,
    Func<GameIdentity, PolicySnapshot, RuleChange[], PolicySnapshot>? ApplyPolicies = null,
    Func<GameIdentity, OfficeSnapshot>? ReadOffices = null,
    Func<GameIdentity, OfficeSnapshot, RuleChange[], OfficeSnapshot>? ApplyOffices = null,
    Func<GameIdentity,PlainRuleSnapshot>? ReadRanks = null,
    Func<GameIdentity,PlainRuleSnapshot,RuleChange[],PlainRuleSnapshot>? ApplyRanks = null,
    Func<GameIdentity,PlainRuleSnapshot>? ReadDoctrines = null,
    Func<GameIdentity,PlainRuleSnapshot,RuleChange[],PlainRuleSnapshot>? ApplyDoctrines = null)
{
    internal static readonly RuleGameAccess Live = new(GameMemory.ReadFormations, GameMemory.ReadPace,
        (game, snapshot, changes) => GameMemory.ApplyFormations(game, snapshot, changes), GameMemory.ReadTactics,
        (game, snapshot, changes) => GameMemory.ApplyTactics(game, snapshot, changes), GameMemory.ReadPolicies,
        (game, snapshot, changes) => GameMemory.ApplyPolicies(game, snapshot, changes), GameMemory.ReadOffices,
        (game, snapshot, changes) => GameMemory.ApplyOffices(game, snapshot, changes),GameMemory.ReadRanks,
        (game,snapshot,changes) => GameMemory.ApplyPlainRules(game,snapshot,changes),GameMemory.ReadDoctrines,
        (game,snapshot,changes) => GameMemory.ApplyPlainRules(game,snapshot,changes));
}

internal sealed record RuleReadState(FormationSnapshot Formation, TacticSnapshot? Tactic, PolicySnapshot? Policy = null, OfficeSnapshot? Office = null, PlainRuleSnapshot? Rank = null, PlainRuleSnapshot? Doctrine = null)
{
    internal Dictionary<RuleKey, int> Values => Formation.Values.Concat(Tactic?.Values ?? []).Concat(Policy?.Values ?? []).Concat(Office?.Values ?? []).Concat(Rank?.Values ?? []).Concat(Doctrine?.Values ?? []).ToDictionary();
    internal string Name(RuleKey key) => key.Table switch { "formation" => Formation.Record(key.Record).Name, "tactic" => Tactic!.Record(key.Record).Name, "office" => Office!.Record(key.Record).Name, "rank" => Rank!.Record(key.Record).Name, "doctrine" => Doctrine!.Record(key.Record).Name, _ => Policy!.Record(key.Table, key.Record).Name };
    internal void Check(RuleProfile profile, string build)
    {
        Formation.Check(profile, build);
        if (profile.Table("tactic") is not null) (Tactic ?? throw new InvalidOperationException(L.Rules.NotReady)).Check(profile, build);
        if (profile.Table("policy") is not null) (Policy ?? throw new InvalidOperationException(L.Rules.NotReady)).Check(profile, build);
        if (profile.Table("office") is not null) (Office ?? throw new InvalidOperationException(L.Rules.NotReady)).Check(profile, build);
        if (profile.Table("rank") is not null) (Rank ?? throw new InvalidOperationException(L.Rules.NotReady)).Check(profile,build);
        if (profile.Table("doctrine") is not null) (Doctrine ?? throw new InvalidOperationException(L.Rules.NotReady)).Check(profile,build);
    }
    internal bool SameStorage(RuleReadState other) => Formation.Root == other.Formation.Root && Formation.State == other.Formation.State
        && Formation.Array == other.Formation.Array && Formation.Catalog == other.Formation.Catalog
        && Tactic?.Root == other.Tactic?.Root && Tactic?.State == other.Tactic?.State && Tactic?.Array == other.Tactic?.Array && Tactic?.Catalog == other.Tactic?.Catalog
        && (Policy is null ? other.Policy is null : other.Policy is not null && Policy.SameStorage(other.Policy))
        && (Office is null ? other.Office is null : other.Office is not null && Office.SameStorage(other.Office))
        && (Rank is null ? other.Rank is null : other.Rank is not null && Rank.SameStorage(other.Rank))
        && (Doctrine is null ? other.Doctrine is null : other.Doctrine is not null && Doctrine.SameStorage(other.Doctrine));
    internal bool SameValues(RuleReadState other)
    {
        if (!SameStorage(other)) return false;
        var before = Values; var after = other.Values;
        return before.Count == after.Count && before.All(v => after.TryGetValue(v.Key, out int n) && n == v.Value);
    }
}

internal sealed partial class EditorWindow
{
    private readonly RuleProfileFile ruleFile;
    private readonly RuleGameAccess ruleAccess;
    private RuleProfileRead? ruleKept;
    private FormationSnapshot? ruleSnapshot;
    private TacticSnapshot? tacticRuleSnapshot;
    private PolicySnapshot? policyRuleSnapshot;
    private OfficeSnapshot? officeRuleSnapshot;
    private PlainRuleSnapshot? rankRuleSnapshot;
    private PlainRuleSnapshot? doctrineRuleSnapshot;
    private readonly WT.DispatcherTimer ruleTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool rulePolling;
    private long ruleRevision;
    private string ruleNotice = "";

    private void WireRules()
    {
        foreach (var card in new[] {rankEditor,doctrineEditor}) {
            card.ApplyClicked += async () => await ApplyPlainRuleAsync(card);
            card.RestoreClicked += async () => await RestorePlainRuleAsync(card);
            card.AutoChanged += async enabled => {
                if (ruleKept?.Profile is not { } p || busy) return;
                await WriteRuleAsync([],p with {AutoApply=enabled},L.Rules.AutoApply);
            };
        }
        officeEditor.ApplyClicked += async () => await ApplyOfficeAsync();
        officeEditor.RestoreClicked += async () => await RestoreOfficeAsync();
        officeEditor.AutoChanged += async enabled =>
        {
            if (ruleKept?.Profile is not { } p || busy) return;
            await WriteRuleAsync([], p with { AutoApply = enabled }, L.Rules.AutoApply);
        };
        foreach (PolicyEditorCard card in new[] { policyEditor, policyEffectEditor })
        {
            card.ApplyClicked += async () => await ApplyPolicyAsync(card);
            card.RestoreClicked += async () => await RestorePolicyAsync(card);
            card.AutoChanged += async enabled =>
            {
                if (ruleKept?.Profile is not { } p || busy) return;
                await WriteRuleAsync([], p with { AutoApply = enabled }, L.Rules.AutoApply);
            };
        }
        formationEditor.ApplyClicked += async () => await ApplyFormationAsync();
        formationEditor.RestoreClicked += async () => await RestoreFormationAsync();
        tacticEditor.ApplyClicked += async () => await ApplyTacticAsync();
        tacticEditor.RestoreClicked += async () => await RestoreTacticAsync();
        tacticEditor.AutoChanged += async enabled =>
        {
            if (ruleKept?.Profile is not { } p || busy) return;
            await WriteRuleAsync([], p with { AutoApply = enabled }, L.Rules.AutoApply);
        };
        formationEditor.AutoChanged += async enabled =>
        {
            if (ruleKept?.Profile is not { } p || busy) return;
            await WriteRuleAsync([], p with { AutoApply = enabled }, L.Rules.AutoApply);
        };
        ruleTimer.Tick += async (_, _) => await PollRulesAsync();
        Closed += (_, _) => ruleTimer.Stop();
    }

    private void ResetRules()
    {
        ++ruleRevision;
        ruleTimer.Stop();
        ruleSnapshot = null;
        tacticRuleSnapshot = null;
        policyRuleSnapshot = null;
        officeRuleSnapshot = null;
        rankRuleSnapshot = null;
        doctrineRuleSnapshot = null;
        ruleKept = null;
        ruleNotice = "";
    }

    // Re-read semantic values regardless of date. A reload that reconstructs records and one that resets
    // values in place both reach this path; unchanged values never trigger another write.
    private async Task PollRulesAsync()
    {
        if (busy || refreshing || layoutRecovering || rulePolling || game is not { Verified: true } identity || layout is null) return;
        rulePolling = true;
        long revision = ruleRevision;
        GameLayout currentLayout = layout;
        bool writing = false;
        // Read-only polling leaves controls available. Any intervening write or reconnect invalidates
        // this read, including a write that starts and finishes before a background read returns.
        bool Current() => !busy && game == identity && ruleRevision == revision;
        void BeginWrite()
        {
            ++ruleRevision;
            writing = true;
            SetBusy(true, scanning: false);
        }
        try
        {
            RuleReadState state = await Task.Run(() => ReadRules(identity));
            FormationSnapshot fresh = state.Formation;
            if (!Current()) return;
            RuleProfileRead read = await Task.Run(ruleFile.Read);
            if (!Current()) return;
            if (read.Profile is null && ruleKept?.Profile is not null) throw new InvalidOperationException(L.Rules.ProfileMoved);
            bool addingTactics = state.Tactic is not null && read.Profile?.Table("tactic") is null;
            bool addingPolicies = state.Policy is not null && read.Profile?.Table("policy") is null;
            bool addingOffices = state.Office is not null && read.Profile?.Table("office") is null;
            bool addingRanks=state.Rank is not null && read.Profile?.Table("rank") is null;
            bool addingDoctrines=state.Doctrine is not null && read.Profile?.Table("doctrine") is null;
            if (read.Profile is null || addingTactics || addingPolicies || addingOffices || addingRanks || addingDoctrines)
            {
                GamePace pace = await Task.Run(() => ruleAccess.Pace(identity, currentLayout with { State = fresh.State }));
                if (!Current()) return;
                if (pace != GamePace.Paused)
                { ruleNotice = L.Rules.PauseFirst; FollowRuleRead(fresh, read, state.Tactic, state.Policy, state.Office, state.Rank, state.Doctrine); return; }
                // A second read must still agree before the first verified baseline is saved.
                RuleReadState checkedState = await Task.Run(() => ReadRules(identity));
                FormationSnapshot checkedValues = checkedState.Formation;
                if (!Current()) return;
                if (!checkedState.SameValues(state))
                    throw new InvalidOperationException(L.Rules.Stale);
                RuleProfile baseline = read.Profile ?? checkedValues.Baseline(identity.ExeHash);
                checkedValues.Check(baseline, identity.ExeHash);
                if (checkedState.Tactic is { } tactics) baseline = baseline.AddTactics(tactics);
                if (checkedState.Policy is { } policies) baseline = baseline.AddPolicies(policies);
                if (checkedState.Office is { } offices) baseline = baseline.AddOffices(offices);
                if (checkedState.Rank is { } ranks) baseline=baseline.AddPlain(ranks);
                if (checkedState.Doctrine is { } doctrines) baseline=baseline.AddPlain(doctrines);
                BeginWrite();
                string previousStamp = read.Stamp;
                read = await Task.Run(() => ruleFile.Save(baseline, read.Stamp));
                if ((addingTactics || addingPolicies || addingOffices || addingRanks || addingDoctrines) && ruleKept?.Stamp == previousStamp)
                {
                    int latest = undoHistory.FindLastIndex(h => h.Rule is not null);
                    for (int i = 0; i < undoHistory.Count; ++i)
                        if (undoHistory[i].Rule is { } undo && undo.Before.Build == baseline.Build && undo.After.Build == baseline.Build
                            && undo.Before.Catalog == baseline.Catalog && undo.After.Catalog == baseline.Catalog)
                            undoHistory[i] = undoHistory[i] with { Rule = undo with {
                                Before = AddNewBaselines(undo.Before, checkedState), After = AddNewBaselines(undo.After, checkedState),
                                Stamp = i == latest && undo.Stamp == previousStamp ? read.Stamp : undo.Stamp } };
                }
                fresh = checkedValues; state = checkedState;
                AddLog(addingDoctrines ? DoctrineRules.Spec.Initialized : addingRanks ? RankRules.Spec.Initialized : addingOffices ? L.Rules.OfficesInitialized : addingPolicies ? L.Rules.PoliciesInitialized : addingTactics ? L.Rules.TacticsInitialized : L.Rules.Initialized);
            }
            RuleProfile profile = read.Profile!;
            state.Check(profile, identity.ExeHash);
            var plan = profile.Reapply(state.Values);
            string notice = string.Join(",", plan.Conflicts.Select(k => $"{k.Table}:{k.Record}:{k.Field}:{state.Values.GetValueOrDefault(k)}"));
            if (profile.AutoApply && plan.Apply.Length > 0 && !RulesPending)
            {
                GamePace pace = await Task.Run(() => ruleAccess.Pace(identity, currentLayout with { State = fresh.State }));
                if (!Current()) return;
                // Inputs can change while the pause observation is in flight.
                if (pace == GamePace.Paused && !RulesPending)
                {
                    BeginWrite();
                    // Auto-reapply uses the same before/after file guard and recovery as an explicit edit.
                    RuleReadState? written = null;
                    read = await Task.Run(() => ruleFile.Commit(profile, read.Stamp,
                        () => written = ApplyRules(identity, state, plan.Apply),
                        () => ApplyRules(identity, written!, Reverse(plan.Apply))));
                    state = written!; fresh = state.Formation;
                    AddLog(L.Rules.AppliedCount(plan.Apply.Length));
                }
            }
            if (notice != ruleNotice)
                foreach (RuleKey key in plan.Conflicts.DistinctBy(k => (k.Table, k.Record))) AddLog(L.Rules.ConflictLog(state.Name(key)));
            ruleNotice = notice;
            FollowRuleRead(fresh, read, state.Tactic, state.Policy, state.Office, state.Rank, state.Doctrine);
        }
        catch (Exception ex)
        {
            if (!writing && !Current()) return;
            ruleKept = null; // Invalid/incompatible files do not remain writable through an old in-memory profile.
            if (ruleNotice != ex.Message) AddLog(ex.Message);
            ruleNotice = ex.Message;
            FillLists();
        }
        finally
        {
            rulePolling = false;
            if (writing) SetBusy(false, scanning: false);
#if LOCAL_DEVELOPER
            UpdateDeveloperControls();
#endif
        }
    }

    private void FollowRuleRead(FormationSnapshot fresh, RuleProfileRead read, TacticSnapshot? tactics = null, PolicySnapshot? policies = null, OfficeSnapshot? offices = null, PlainRuleSnapshot? ranks = null, PlainRuleSnapshot? doctrines = null)
    {
        if (ruleSnapshot is { } old && (old.Root != fresh.Root || old.State != fresh.State || old.Array != fresh.Array))
            undoHistory.RemoveAll(h => h.Rule is null); // Address-based history is invalid for a new table.
        ruleSnapshot = fresh;
        tacticRuleSnapshot = tactics;
        policyRuleSnapshot = policies;
        officeRuleSnapshot = offices;
        rankRuleSnapshot = ranks;
        doctrineRuleSnapshot = doctrines;
        FollowOfficeCatalog();
        ruleKept = read;
        FillLists();
        RefreshEditor();
    }

    private async Task<bool> ApplyFormationAsync()
    {
        if (busy || ruleKept?.Profile is not { } profile || ruleSnapshot is null) return false;
        RuleChange[]? changes = formationEditor.Changes(out string error);
        if (changes is null) { status.Text = error; return false; }
        return await WriteRuleAsync(changes, profile.WithChanges(changes), L.Rules.Edit(ruleSnapshot.Record(formationEditor.Formation).Name));
    }

    private async Task RestoreFormationAsync()
    {
        if (busy || ruleKept?.Profile is not { } profile || ruleSnapshot is null || formationEditor.Target is null) return;
        FormationRecord record = ruleSnapshot.Record(formationEditor.Formation);
        if (!Confirm(L.Rules.RestoreAsk(record.Name))) return;
        RuleChange[] changes = FormationRules.Fields.Select(f => new RuleChange(FormationRules.Key(record.Id, f), record.Value(f), profile.Default(FormationRules.Key(record.Id, f)))).ToArray();
        await WriteRuleAsync(changes, profile.WithChanges(changes, restoreDefaults: true), L.Rules.Restore);
    }

    private async Task<bool> WriteRuleAsync(RuleChange[] changes, RuleProfile after, string summary, bool undoable = true)
    {
        if (busy || game is not { Verified: true } identity || layout is null || ruleSnapshot is not { } expected || ruleKept?.Profile is not { } before) return false;
        string stamp = ruleKept.Stamp;
        if (changes.All(c => c.Before == c.After) && before.SameAs(after))
        { status.Text = L.Window.NothingChanged; ReopenEditor(); return true; }
        ++ruleRevision;
        SetBusy(true, scanning: false);
        try
        {
            var expectedState = new RuleReadState(expected, tacticRuleSnapshot, policyRuleSnapshot, officeRuleSnapshot, rankRuleSnapshot, doctrineRuleSnapshot);
            expectedState.Check(before, identity.ExeHash);
            after.Validate();
            if (changes.Any(c => c.Before != c.After) && await Task.Run(() => ruleAccess.Pace(identity, layout with { State = expected.State })) != GamePace.Paused)
                throw new InvalidOperationException(L.Rules.PauseFirst);
            RuleReadState? written = null;
            ruleKept = await Task.Run(() => ruleFile.Commit(after, stamp,
                () => written = ApplyRules(identity, expectedState, changes),
                () => ApplyRules(identity, written!, Reverse(changes))));
            ruleSnapshot = written!.Formation; tacticRuleSnapshot = written.Tactic; policyRuleSnapshot = written.Policy; officeRuleSnapshot = written.Office; rankRuleSnapshot=written.Rank; doctrineRuleSnapshot=written.Doctrine;
            FollowOfficeCatalog();
            if (undoable)
            {
                undoHistory.Add(new(summary, [], new(before, after, ruleKept.Stamp, changes)));
                if (undoHistory.Count > UndoLimit) undoHistory.RemoveAt(0);
            }
            foreach (RuleChange c in changes.Where(c => c.Before != c.After))
                AddLog(L.Rules.Change(expectedState.Name(c.Key), c.Key.Table == "formation" ? FormationRules.Field(c.Key.Field).Label
                    : c.Key.Table == "tactic" ? $"{TacticRules.Field(c.Key.Field).Label} ({TacticRules.Field(c.Key.Field).Slot + 1})"
                    : c.Key.Table == "office" ? OfficeRules.Field(c.Key.Field).Label : c.Key.Table=="rank" ? RankRules.Spec.Field(c.Key.Field).Label : c.Key.Table=="doctrine" ? DoctrineRules.Spec.Field(c.Key.Field).Label : PolicyRules.Field(c.Key).Label, c.Before, c.After));
            AddLog(summary);
            status.Text = summary;
            FillLists();
            if (changes.Length == 0) RefreshEditor(); else ReopenEditor();
            return true;
        }
        catch (Exception ex) { ReportError(ex); RefreshEditor(); return false; }
        finally { SetBusy(false, scanning: false); }
    }

    private async Task UndoRuleAsync()
    {
        EditorUndo entry = undoHistory[^1];
        RuleUndo undo = entry.Rule!;
        SetBusy(true, scanning: false);
        try
        {
            RuleProfileRead current = ruleFile.Read();
            if (current.Stamp != undo.Stamp) throw new InvalidOperationException(L.Rules.UndoMoved);
            RuleReadState fresh = await Task.Run(() => ReadRules(game!.Value));
            fresh.Check(undo.After, game!.Value.ExeHash);
            if (undo.Changes.Any(c => fresh.Values[c.Key] != c.After)) throw new InvalidOperationException(L.Rules.UndoMoved);
            if (!Confirm(L.Undo.Ask(entry.Summary))) return;
            ruleSnapshot = fresh.Formation; tacticRuleSnapshot = fresh.Tactic; policyRuleSnapshot = fresh.Policy; officeRuleSnapshot = fresh.Office; rankRuleSnapshot=fresh.Rank; doctrineRuleSnapshot=fresh.Doctrine;
            ruleKept = current;
            SetBusy(false, scanning: false);
            if (await WriteRuleAsync(undo.Changes.Select(c => c with { Before = c.After, After = c.Before }).ToArray(), undo.Before, L.Undo.Undoing(entry.Summary), undoable: false))
            {
                undoHistory.Remove(entry);
                // A second Undo can still follow our own successful persistence operation.
                int at = undoHistory.FindLastIndex(h => h.Rule is not null);
                if (at >= 0 && undoHistory[at].Rule is { } previous && previous.After.SameAs(undo.Before))
                    undoHistory[at] = undoHistory[at] with { Rule = previous with { Stamp = ruleKept!.Stamp } };
            }
        }
        catch (Exception ex) { ReportError(ex); }
        finally { SetBusy(false, scanning: false); }
        UpdateControls();
    }

    private List<GameTable.Row> FormationRows()
    {
        if (ruleSnapshot is null || ruleKept?.Profile is not { } profile) return [];
        string word = formationSearch.Text.Trim();
        return ruleSnapshot.Records.Where(r => word.Length == 0 || r.Name.Contains(word, StringComparison.Ordinal) || r.Id.ToString() == word)
            .Select(r => new GameTable.Row(r.Id, [$"{r.Name} · #{r.Id}", .. FormationRules.Fields.Select(f => (object?)(long)r.Value(f)),
                FormationRules.Fields.Any(f => r.Value(f) != profile.Desired(FormationRules.Key(r.Id, f)) && r.Value(f) != profile.Default(FormationRules.Key(r.Id, f))) ? L.Rules.Conflict
                    : FormationRules.Fields.Any(f => r.Value(f) != profile.Desired(FormationRules.Key(r.Id, f))) ? L.Rules.Waiting
                    : profile.Overrides.Any(o => o.Key.Table == "formation" && o.Key.Record == r.Id) ? L.Rules.Applied : L.Rules.Unchanged]))
            .ToList();
    }

    private RuleReadState ReadRules(GameIdentity identity)
    {
        FormationSnapshot formation = ruleAccess.Read(identity);
        TacticSnapshot? tactics = ruleAccess.ReadTactics?.Invoke(identity);
        if (tactics is not null && (tactics.Root != formation.Root || tactics.State != formation.State)) throw new InvalidOperationException(L.Rules.Stale);
        PolicySnapshot? policies = ruleAccess.ReadPolicies?.Invoke(identity);
        if (policies is not null && (policies.Root != formation.Root || policies.State != formation.State)) throw new InvalidOperationException(L.Rules.Stale);
        OfficeSnapshot? offices = ruleAccess.ReadOffices?.Invoke(identity);
        if (offices is not null && (offices.Root != formation.Root || offices.State != formation.State)) throw new InvalidOperationException(L.Rules.Stale);
        PlainRuleSnapshot? ranks=ruleAccess.ReadRanks?.Invoke(identity);
        if (ranks is not null && (ranks.Root!=formation.Root || ranks.State!=formation.State)) throw new InvalidOperationException(L.Rules.Stale);
        PlainRuleSnapshot? doctrines=ruleAccess.ReadDoctrines?.Invoke(identity);
        if (doctrines is not null && (doctrines.Root!=formation.Root || doctrines.State!=formation.State)) throw new InvalidOperationException(L.Rules.Stale);
        return new(formation, tactics, policies, offices, ranks, doctrines);
    }
    private bool RulesPending => formationEditor.Pending != 0 || tacticEditor.Pending != 0 || policyEditor.Pending != 0 || policyEffectEditor.Pending != 0 || officeEditor.Pending != 0 || rankEditor.Pending != 0 || doctrineEditor.Pending != 0;
    private static RuleProfile AddNewBaselines(RuleProfile profile, RuleReadState state)
    {
        if (state.Tactic is { } tactics) profile = profile.AddTactics(tactics);
        if (state.Policy is { } policies) profile = profile.AddPolicies(policies);
        if (state.Office is { } offices) profile = profile.AddOffices(offices);
        if (state.Rank is { } ranks) profile=profile.AddPlain(ranks);
        if (state.Doctrine is { } doctrines) profile=profile.AddPlain(doctrines);
        return profile;
    }
    private async Task<bool> ApplyPolicyAsync(PolicyEditorCard card)
    {
        if (busy || ruleKept?.Profile is not { } profile || policyRuleSnapshot is null) return false;
        RuleChange[]? changes = card.Changes(out string error);
        if (changes is null) { status.Text = error; return false; }
        return await WriteRuleAsync(changes, profile.WithChanges(changes), L.Rules.Edit(policyRuleSnapshot.Record(card.Table, card.RecordId).Name));
    }
    private PlainRuleSnapshot? PlainSnapshot(string table) => table switch {"rank" => rankRuleSnapshot,"doctrine" => doctrineRuleSnapshot,_ => null};
    private async Task<bool> ApplyPlainRuleAsync(PlainRuleEditorCard card)
    {
        if (busy || ruleKept?.Profile is not { } profile || PlainSnapshot(card.Table) is not { } state) return false;
        RuleChange[]? changes=card.Changes(out string error);
        if (changes is null) {status.Text=error; return false;}
        return await WriteRuleAsync(changes,profile.WithChanges(changes),L.Rules.Edit(state.Record(card.RecordId).Name));
    }
    private async Task RestorePlainRuleAsync(PlainRuleEditorCard card)
    {
        if (busy || ruleKept?.Profile is not { } profile || PlainSnapshot(card.Table) is not { } state || card.Target is null) return;
        var record=state.Record(card.RecordId); if (!Confirm(L.Rules.RestoreAsk(record.Name))) return;
        RuleChange[] changes=state.Spec.Fields.Select(f => new RuleChange(new(card.Table,record.Id,f.Id),record.Value(f),profile.Default(new(card.Table,record.Id,f.Id)))).ToArray();
        await WriteRuleAsync(changes,profile.WithChanges(changes,true),L.Rules.Restore);
    }
    private List<GameTable.Row> PlainRuleRows(PlainRuleSnapshot? state,SearchBox search)
    {
        if (state is null || ruleKept?.Profile is not { } profile || profile.Table(state.Spec.Table) is null) return [];
        string word=search.Text.Trim();
        return state.Records.Where(r => word.Length==0 || r.Name.Contains(word,StringComparison.Ordinal) || r.Id.ToString()==word)
            .Select(r => new GameTable.Row(r.Id,[$"{r.Name} · #{r.Id}",state.Spec.Table=="rank" ? (long)r.Value(state.Spec.Field("grade")) : (long)r.Value(state.Spec.Field("effect")),
                state.Spec.Table=="rank" ? (object)(long)r.Value(state.Spec.Field("capacity")) : DoctrineCostSummary(r),
                state.Spec.Fields.Any(f => r.Value(f)!=profile.Desired(new(state.Spec.Table,r.Id,f.Id)) && r.Value(f)!=profile.Default(new(state.Spec.Table,r.Id,f.Id))) ? L.Rules.Conflict
                : state.Spec.Fields.Any(f => r.Value(f)!=profile.Desired(new(state.Spec.Table,r.Id,f.Id))) ? L.Rules.Waiting
                : profile.Overrides.Any(o => o.Key.Table==state.Spec.Table && o.Key.Record==r.Id) ? L.Rules.Applied : L.Rules.Unchanged])).ToList();
    }
    private string DoctrineCostSummary(PlainRuleRecord record)
    {
        string Cost(int slot) {
            int kind=record.Value(DoctrineRules.Spec.Field("kind"+slot)),cost=record.Value(DoctrineRules.Spec.Field("cost"+slot));
            return kind==0 ? "" : L.Rules.DoctrineCostPair(catalog.Ideologies.FirstOrDefault(n => n.Id==kind).Name ?? $"#{kind}",cost);
        }
        string result=string.Join(" · ",new[] {Cost(1),Cost(2)}.Where(s => s.Length>0));
        return result.Length==0 ? L.Terms.None : result;
    }
    private async Task<bool> ApplyOfficeAsync()
    {
        if (busy || ruleKept?.Profile is not { } profile || officeRuleSnapshot is null) return false;
        RuleChange[]? changes = officeEditor.Changes(out string error);
        if (changes is null) { status.Text = error; return false; }
        return await WriteRuleAsync(changes, profile.WithChanges(changes), L.Rules.Edit(officeRuleSnapshot.Record(officeEditor.Office).Name));
    }
    private void FollowOfficeCatalog()
    {
        if (officeRuleSnapshot is not { } offices) return;
        GameName[] names = offices.Records.Select(r => new GameName(r.Id, r.Name,
            L.Terms.GradeAndCap(r.Value(OfficeRules.Field("grade")), r.Value(OfficeRules.Field("capacity"))))).ToArray();
        if (catalog.Offices.SequenceEqual(names)) return;
        catalog = catalog with { Offices = names };
        officerEditor.FollowOffices(names);
    }
    private async Task RestoreOfficeAsync()
    {
        if (busy || ruleKept?.Profile is not { } profile || officeRuleSnapshot is null || officeEditor.Target is null) return;
        OfficeRecord record = officeRuleSnapshot.Record(officeEditor.Office);
        if (!Confirm(L.Rules.RestoreAsk(record.Name))) return;
        RuleChange[] changes = OfficeRules.Fields.Select(f => new RuleChange(OfficeRules.Key(record.Id, f), record.Value(f), profile.Default(OfficeRules.Key(record.Id, f)))).ToArray();
        await WriteRuleAsync(changes, profile.WithChanges(changes, restoreDefaults: true), L.Rules.Restore);
    }
    private List<GameTable.Row> OfficeRows()
    {
        if (officeRuleSnapshot is not { } snapshot || ruleKept?.Profile is not { } profile || profile.Table("office") is null) return [];
        string word = officeSearch.Text.Trim();
        return snapshot.Records.Where(r => word.Length == 0 || r.Name.Contains(word, StringComparison.Ordinal) || r.Id.ToString() == word)
            .Select(r => new GameTable.Row(r.Id, [$"{r.Name} · #{r.Id}", (long)r.Value(OfficeRules.Field("grade")), (long)r.Value(OfficeRules.Field("capacity")),
                OfficeRules.Fields.Any(f => r.Value(f) != profile.Desired(OfficeRules.Key(r.Id, f)) && r.Value(f) != profile.Default(OfficeRules.Key(r.Id, f))) ? L.Rules.Conflict
                    : OfficeRules.Fields.Any(f => r.Value(f) != profile.Desired(OfficeRules.Key(r.Id, f))) ? L.Rules.Waiting
                    : profile.Overrides.Any(o => o.Key.Table == "office" && o.Key.Record == r.Id) ? L.Rules.Applied : L.Rules.Unchanged])).ToList();
    }
    private async Task RestorePolicyAsync(PolicyEditorCard card)
    {
        if (busy || ruleKept?.Profile is not { } profile || policyRuleSnapshot is null || card.Target is null) return;
        PolicyRecord record = policyRuleSnapshot.Record(card.Table, card.RecordId);
        if (!Confirm(L.Rules.RestoreAsk(record.Name))) return;
        RuleChange[] changes = PolicyRules.Fields(record.Table).Select(f => new RuleChange(PolicyRules.Key(record.Id, f), record.Value(f), profile.Default(PolicyRules.Key(record.Id, f)))).ToArray();
        await WriteRuleAsync(changes, profile.WithChanges(changes, restoreDefaults: true), L.Rules.Restore);
    }
    private List<GameTable.Row> PolicyRows(string table, SearchBox search)
    {
        if (policyRuleSnapshot is not { } snapshot || ruleKept?.Profile is not { } profile || profile.Table(table) is null) return [];
        string word = search.Text.Trim();
        return snapshot.Records.Where(r => r.Table == table && (word.Length == 0 || r.Name.Contains(word, StringComparison.Ordinal) || r.Id.ToString() == word))
            .Select(r => new GameTable.Row(r.Id, [ $"{r.Name} · #{r.Id}", table == "policy"
                    ? string.Join(", ", PolicyRules.Effects.Select(f => r.Value(f)).Where(id => id > 0).Select(id => snapshot.Records.FirstOrDefault(e => e.Table == "policyeffect" && e.Id == id)?.Name ?? $"#{id}"))
                    : $"{r.Value(PolicyRules.Levels[0])} → {r.Value(PolicyRules.Levels[9])}",
                PolicyRules.Fields(table).Any(f => r.Value(f) != profile.Desired(PolicyRules.Key(r.Id, f)) && r.Value(f) != profile.Default(PolicyRules.Key(r.Id, f))) ? L.Rules.Conflict
                    : PolicyRules.Fields(table).Any(f => r.Value(f) != profile.Desired(PolicyRules.Key(r.Id, f))) ? L.Rules.Waiting
                    : profile.Overrides.Any(o => o.Key.Table == table && o.Key.Record == r.Id) ? L.Rules.Applied : L.Rules.Unchanged ])).ToList();
    }
    private static RuleChange[] Reverse(RuleChange[] changes) => changes.Select(c => c with { Before = c.After, After = c.Before }).ToArray();
    private RuleReadState ApplyRules(GameIdentity identity, RuleReadState expected, RuleChange[] changes)
    {
        RuleChange[] formations = changes.Where(c => c.Key.Table == "formation").ToArray(), tactics = changes.Where(c => c.Key.Table == "tactic").ToArray(),
            policies = changes.Where(c => c.Key.Table is "policy" or "policyeffect").ToArray(), offices = changes.Where(c => c.Key.Table == "office").ToArray();
        RuleChange[] ranks=changes.Where(c => c.Key.Table=="rank").ToArray();
        RuleChange[] doctrines=changes.Where(c => c.Key.Table=="doctrine").ToArray();
        if (formations.Length + tactics.Length + policies.Length + offices.Length + ranks.Length + doctrines.Length != changes.Length) throw new InvalidOperationException(L.Rules.BadProfile);
        FormationSnapshot? writtenFormation = null; TacticSnapshot? writtenTactic = null; PolicySnapshot? writtenPolicy = null; OfficeSnapshot? writtenOffice = null;
        PlainRuleSnapshot? writtenRank=null,writtenDoctrine=null;
        try
        {
            if (formations.Length > 0 || tactics.Length + policies.Length + offices.Length + ranks.Length + doctrines.Length == 0) writtenFormation = ruleAccess.Apply(identity, expected.Formation, formations);
            if (tactics.Length > 0) writtenTactic = (ruleAccess.ApplyTactics ?? throw new InvalidOperationException(L.Rules.NotReady))(identity, expected.Tactic!, tactics);
            if (policies.Length > 0) writtenPolicy = (ruleAccess.ApplyPolicies ?? throw new InvalidOperationException(L.Rules.NotReady))(identity, expected.Policy!, policies);
            if (offices.Length > 0) writtenOffice = (ruleAccess.ApplyOffices ?? throw new InvalidOperationException(L.Rules.NotReady))(identity, expected.Office!, offices);
            if (ranks.Length>0) writtenRank=(ruleAccess.ApplyRanks ?? throw new InvalidOperationException(L.Rules.NotReady))(identity,expected.Rank!,ranks);
            if (doctrines.Length>0) writtenDoctrine=(ruleAccess.ApplyDoctrines ?? throw new InvalidOperationException(L.Rules.NotReady))(identity,expected.Doctrine!,doctrines);
            RuleReadState result = expected.Tactic is null && expected.Policy is null && expected.Office is null && expected.Rank is null && expected.Doctrine is null && writtenFormation is not null ? new(writtenFormation, null) : ReadRules(identity);
            if (!expected.SameStorage(result) || changes.Any(c => result.Values[c.Key] != c.After)) throw new InvalidOperationException(L.Rules.Stale);
            return result;
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            if (writtenDoctrine is not null && doctrines.Any(c => c.Before!=c.After))
                try {ruleAccess.ApplyDoctrines!(identity,writtenDoctrine,Reverse(doctrines));} catch (Exception rollback) {errors.Add(rollback);}
            if (writtenRank is not null && ranks.Any(c => c.Before!=c.After))
                try {ruleAccess.ApplyRanks!(identity,writtenRank,Reverse(ranks));} catch (Exception rollback) {errors.Add(rollback);}
            if (writtenOffice is not null && offices.Any(c => c.Before != c.After))
                try { ruleAccess.ApplyOffices!(identity, writtenOffice, Reverse(offices)); } catch (Exception rollback) { errors.Add(rollback); }
            if (writtenPolicy is not null && policies.Any(c => c.Before != c.After))
                try { ruleAccess.ApplyPolicies!(identity, writtenPolicy, Reverse(policies)); } catch (Exception rollback) { errors.Add(rollback); }
            if (writtenTactic is not null && tactics.Any(c => c.Before != c.After))
                try { ruleAccess.ApplyTactics!(identity, writtenTactic, Reverse(tactics)); } catch (Exception rollback) { errors.Add(rollback); }
            if (writtenFormation is not null && formations.Any(c => c.Before != c.After))
                try { ruleAccess.Apply(identity, writtenFormation, Reverse(formations)); } catch (Exception rollback) { errors.Add(rollback); }
            if (errors.Count > 1) throw new AggregateException(L.Rules.RollbackFailed, errors);
            throw;
        }
    }
    private async Task<bool> ApplyTacticAsync()
    {
        if (busy || ruleKept?.Profile is not { } profile || tacticRuleSnapshot is null) return false;
        RuleChange[]? changes = tacticEditor.Changes(out string error);
        if (changes is null) { status.Text = error; return false; }
        return await WriteRuleAsync(changes, profile.WithChanges(changes), L.Rules.Edit(tacticRuleSnapshot.Record(tacticEditor.Tactic).Name));
    }
    private async Task RestoreTacticAsync()
    {
        if (busy || ruleKept?.Profile is not { } profile || tacticRuleSnapshot is null || tacticEditor.Target is null) return;
        TacticRecord record = tacticRuleSnapshot.Record(tacticEditor.Tactic);
        if (!Confirm(L.Rules.RestoreAsk(record.Name))) return;
        RuleChange[] changes = TacticRules.Fields.Select(f => new RuleChange(TacticRules.Key(record.Id, f), record.Value(f), profile.Default(TacticRules.Key(record.Id, f)))).ToArray();
        await WriteRuleAsync(changes, profile.WithChanges(changes, restoreDefaults: true), L.Rules.Restore);
    }
    private List<GameTable.Row> TacticRows()
    {
        if (tacticRuleSnapshot is null || ruleKept?.Profile is not { } profile || profile.Table("tactic") is null) return [];
        string word = tacticSearch.Text.Trim();
        return tacticRuleSnapshot.Records.Where(r => word.Length == 0 || r.Name.Contains(word, StringComparison.Ordinal) || r.Id.ToString() == word)
            .Select(r => new GameTable.Row(r.Id, [$"{r.Name} · #{r.Id}", TacticRules.KindName(r.Value(TacticRules.Field("kind1"))), TacticRules.KindName(r.Value(TacticRules.Field("kind2"))),
                r.Value(TacticRules.Field("ability")) == 1 ? L.Rules.Martial : L.Rules.Intelligence,
                TacticRules.Fields.Any(f => r.Value(f) != profile.Desired(TacticRules.Key(r.Id, f)) && r.Value(f) != profile.Default(TacticRules.Key(r.Id, f))) ? L.Rules.Conflict
                    : TacticRules.Fields.Any(f => r.Value(f) != profile.Desired(TacticRules.Key(r.Id, f))) ? L.Rules.Waiting
                    : profile.Overrides.Any(o => o.Key.Table == "tactic" && o.Key.Record == r.Id) ? L.Rules.Applied : L.Rules.Unchanged])).ToList();
    }
}

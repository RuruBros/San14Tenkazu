using System.Drawing.Imaging;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace San14Tenkazu;

// The editor cards: which one shows (one at a time), the ◀ ▶ history through the cards opened before, what a
// card asks before it gives way while values typed into it wait, and each card's [변경 적용].
internal sealed partial class EditorWindow
{
    // The game's names for 개성, 전법, 정책, 주의, 관직 and 작위, read once per connection.
    private GameCatalog catalog = GameCatalog.Empty;
    private sealed record PaneVisit(EditorTarget? Target, ListKind List);
    private readonly List<PaneVisit> history = [];
    private int historyAt = -1;
    private bool navigatingHistory;
    private int portraitFace = -1;

    private EditorCard[] Editors => [forceEditor, cityEditor, gateEditor, facilityEditor, officerEditor, unitEditor, personalityEditor, itemEditor, formationEditor, tacticEditor, policyEditor, policyEffectEditor, officeEditor, rankEditor, doctrineEditor];

    private EditorCard? OpenEditor => Editors.FirstOrDefault(card => card.Shown);

    private void WireEditors()
    {
        lists.TabChanged += RememberLists;
        map.EditorResumed += RememberEditor;
        ConfigureMemberEditing(cityEditor.Officers);
        ConfigureMemberEditing(gateEditor.Officers);
        ConfigureMemberEditing(forceEditor.Members);
        ConfigureCellEditing(forceEditor.CitiesTable, tag => new CityTarget((int)tag));
        ConfigureCellEditing(forceEditor.GatesTable, tag => new GateTarget((int)tag));
        ConfigureCellEditing(forceEditor.UnitsTable, tag => { var (slot, commander) = ((int, int))tag; return new UnitTarget(slot, commander); });
        foreach (EditorCard card in Editors)
        {
            card.CloseClicked += () => _ = CloseEditorAsync();
            card.BackClicked += () => _ = StepAsync(-1);
            card.ForwardClicked += () => _ = StepAsync(1);
            card.LinkClicked += target => _ = FollowLinkAsync(target);
        }
        forceEditor.ApplyClicked += async () => await ApplyForceAsync();
        cityEditor.ApplyClicked += async () => await ApplyCityAsync();
        gateEditor.ApplyClicked += async () => await ApplyGateAsync();
        facilityEditor.ApplyClicked += async () => await ApplyFacilityAsync();
        officerEditor.ApplyClicked += async () => await ApplyOfficerAsync();
        unitEditor.ApplyClicked += async () => await ApplyUnitAsync();
        personalityEditor.ApplyClicked += async () => await ApplyPersonalityAsync();
        itemEditor.ApplyClicked += async () => await ApplyItemAsync();
        officerEditor.TransferClicked += async (kind, target) => await TransferAsync(kind, target);
        officerEditor.RevivalClicked += async request => await ReviveOfficerAsync(request);
        officerEditor.StatusChangeClicked += async request => await ChangeOfficerStatusAsync(request);
    }

    // Opens the card of a force, city, gate, officer or unit with the game's values; false when it did not (the user kept
    // the card that was open, or the target is gone).
    private async Task<bool> OpenAsync(EditorTarget target, bool remember = true)
    {
        if (busy) return false;
        if (snapshot is null)
        {
            status.Text = L.Window.ConnectForCards;
            return false;
        }
        if (remember && lists.Shown) RememberLists();
        if (OpenEditor is { } open)
        {
            if (Equals(open.Target, target))
            {
                map.ShowEditor(open);
                if (remember) RememberEditor();
                return true;
            }
            if (!await LeaveAsync(open)) return false;
        }
        if (!Show(target)) return false;
        if (remember) RememberEditor();
        UpdateHistory();
        return true;
    }

    private void RememberVisit(PaneVisit visit)
    {
        if (navigatingHistory || historyAt >= 0 && history[historyAt] == visit) return;
        if (historyAt + 1 < history.Count && history[historyAt + 1] == visit) ++historyAt;
        else
        {
            history.RemoveRange(historyAt + 1, history.Count - historyAt - 1);
            history.Add(visit);
            if (history.Count > 50) history.RemoveAt(0);
            historyAt = history.Count - 1;
        }
        UpdateHistory();
    }

    private void RememberLists()
    {
        if (navigatingHistory || !lists.Shown) return;
        var visit = new PaneVisit(null, (ListKind)lists.Tab);
        // Returning to the list that opened a detail is a backward move, preserving its forward entry.
        if (historyAt > 0 && history[historyAt].Target is not null && history[historyAt].List == visit.List
            && history[historyAt - 1] == visit)
        { --historyAt; UpdateHistory(); }
        else RememberVisit(visit);
    }

    private void RememberEditor()
    {
        if (OpenEditor?.Target is { } target) RememberVisit(new(target, map.EditorList));
    }

    // A name in a card opens its own card, and the map goes to it.
    private async Task FollowLinkAsync(EditorTarget target)
    {
        if (!await OpenAsync(target) || snapshot is not { } data) return;
        switch (target)
        {
            case CityTarget(int city):
                map.ShowCity(city);
                break;
            case GateTarget(int id) when data.Gates.FirstOrDefault(g => g.Id == id) is { Tile: >= 0 } gate:
                map.ShowTile(gate.Tile);
                break;
            case FacilityTarget facility when facility.Find(data) is { } found:
                map.ShowTile(found.Tile);
                break;
            case OfficerTarget(int id) when data.OfficerById.TryGetValue(id, out OfficerRecord? officer):
                GoToOfficer(officer);
                break;
            case UnitTarget(int slot, int commander):
                map.ShowUnit((slot, commander));
                break;
        }
    }

    // ◀ ▶ visit lists as well as details. A list hides the current card and preserves its draft.
    private async Task StepAsync(int step)
    {
        if (busy || !map.EditorShown || step is not (-1 or 1)) return;
        navigatingHistory = true;
        try
        {
            for (int at = historyAt + step; at >= 0 && at < history.Count; at += step)
            {
                PaneVisit visit = history[at];
                if (visit.Target is null) map.ShowList(visit.List);
                else if (OpenEditor is { } open && Equals(open.Target, visit.Target)) map.ShowEditor(open, visit.List);
                else
                {
                    if (OpenEditor is { } previous && !await LeaveAsync(previous)) return;
                    if (!ShowAtOrigin(visit.Target, visit.List)) continue;
                }
                historyAt = at;
                break;
            }
        }
        finally { navigatingHistory = false; UpdateHistory(); }
    }

    private void UpdateHistory()
    {
        foreach (EditorCard card in Editors) card.SetHistory(historyAt > 0, historyAt >= 0 && historyAt < history.Count - 1);
    }

    // A card with typed values asks before it gives way: write them, drop them, or stay. True when it may go.
    private async Task<bool> LeaveAsync(EditorCard card)
    {
        if (card.Pending == 0) return true;
        return Choose(L.Window.LeaveCard(card.Pending)) switch
        {
            true => await ApplyCardAsync(card),
            false => true,
            null => false
        };
    }

    private Task<bool> ApplyCardAsync(EditorCard card) => card switch
    {
        ForceEditorCard => ApplyForceAsync(),
        CityEditorCard => ApplyCityAsync(),
        GateEditorCard => ApplyGateAsync(),
        FacilityEditorCard => ApplyFacilityAsync(),
        OfficerEditorCard => ApplyOfficerAsync(),
        PersonalityEditorCard => ApplyPersonalityAsync(),
        ItemEditorCard => ApplyItemAsync(),
        FormationEditorCard => ApplyFormationAsync(),
        TacticEditorCard => ApplyTacticAsync(),
        PolicyEditorCard policyCard => ApplyPolicyAsync(policyCard),
        OfficeEditorCard => ApplyOfficeAsync(),
        PlainRuleEditorCard plainCard => ApplyPlainRuleAsync(plainCard),
        _ => ApplyUnitAsync()
    };

    private async Task CloseEditorAsync()
    {
        if (OpenEditor is not { } card || !await LeaveAsync(card)) return;
        map.ReturnToList();
        map.HideEditors();
        map.HighlightForce = 0;
        map.FocusMap();
    }

    // Shows the card of a target with the game's values (what was typed in it goes). False when it is not in the game.
    private bool Show(EditorTarget target) => ShowAtOrigin(target, null);

    private bool ShowAtOrigin(EditorTarget target, ListKind? origin)
    {
        if (snapshot is not { } data) return false;
        switch (target)
        {
            case PlainRuleTarget(string table,int id) when PlainSnapshot(table) is { } plain && ruleKept?.Profile is { } profile && profile.Table(table) is not null
                && plain.Records.Any(r => r.Id == id):
                PlainRuleEditorCard plainCard=table=="rank" ? rankEditor : doctrineEditor;
                if (table=="doctrine") plainCard.SetCostNames(catalog.Ideologies);
                plainCard.Open(new(plain.Record(id),profile)); map.ShowEditor(plainCard,origin); map.HighlightForce=0; return true;
            case OfficeTarget(int id) when officeRuleSnapshot is not null && ruleKept?.Profile is { } profile && profile.Table("office") is not null
                && officeRuleSnapshot.Records.Any(r => r.Id == id):
                officeEditor.Open(new(officeRuleSnapshot.Record(id), profile));
                map.ShowEditor(officeEditor, origin); map.HighlightForce = 0; return true;
            case PolicyTarget(string table, int id) when table is "policy" or "policyeffect" && policyRuleSnapshot is not null
                && ruleKept?.Profile is { } profile && profile.Table(table) is not null && policyRuleSnapshot.Records.Any(r => r.Table == table && r.Id == id):
                PolicyEditorCard policyCard = table == "policy" ? policyEditor : policyEffectEditor;
                policyCard.Open(new(policyRuleSnapshot.Record(table, id), policyRuleSnapshot, profile));
                map.ShowEditor(policyCard, origin); map.HighlightForce = 0; return true;
            case TacticTarget(int id) when tacticRuleSnapshot is not null && ruleKept?.Profile is { } profile && profile.Table("tactic") is not null:
                tacticEditor.Open(new(tacticRuleSnapshot.Record(id), profile));
                map.ShowEditor(tacticEditor, origin);
                map.HighlightForce = 0;
                return true;
            case FormationTarget(int id) when ruleSnapshot is not null && ruleKept?.Profile is { } profile:
                formationEditor.Open(new(ruleSnapshot.Record(id), profile));
                map.ShowEditor(formationEditor, origin);
                map.HighlightForce = 0;
                return true;
            case ForceTarget(int force) when data.ForceRecords.ContainsKey(force) && data.Forces.ContainsKey(force):
                forceEditor.Open(ForceView(data, force));
                map.ShowEditor(forceEditor, origin);
                map.HighlightForce = force;
                return true;
            case CityTarget(int city) when city is >= 1 and <= GameMemory.CityCount:
                cityEditor.Open(CityView(data, city));
                map.ShowEditor(cityEditor, origin);
                map.SelectedCity = city;
                map.HighlightForce = 0;
                return true;
            case GateTarget(int gate) when data.Gates.Any(g => g.Id == gate):
                gateEditor.Open(GateView(data, gate));
                map.ShowEditor(gateEditor, origin);
                map.SelectedGate = gate;
                map.HighlightForce = 0;
                return true;
            case FacilityTarget facility when facility.Find(data) is { } found:
                facilityEditor.Open(FacilityView(data, found));
                map.ShowEditor(facilityEditor, origin);
                map.SelectedFacility = facility;
                map.HighlightForce = 0;
                return true;
            case OfficerTarget(int id) when data.OfficerById.TryGetValue(id, out OfficerRecord? officer):
                ShowOfficer(data, officer);
                map.ShowEditor(officerEditor, origin);
                map.HighlightForce = 0;
                return true;
            case UnitTarget(int slot, int commander) when data.Unit(slot, commander) is { } unit:
                unitEditor.Open(UnitView(data, unit));
                map.ShowEditor(unitEditor, origin);
                map.SelectedUnit = (slot, commander);
                map.HighlightForce = 0;
                return true;
            case PersonalityTarget(int index) when data.Personalities.Any(p => p.Index == index):
                personalityEditor.Open(PersonalityView(data, index));
                map.ShowEditor(personalityEditor, origin);
                map.HighlightForce = 0;
                return true;
            case ItemTarget(int index) when data.Items.Any(i => i.Index == index):
                itemEditor.Open(ItemView(data, index));
                map.ShowEditor(itemEditor, origin);
                map.HighlightForce = 0;
                return true;
        }
        status.Text = L.Window.TargetGone;
        return false;
    }

    // The open card again with what the game now holds (after a write, an undo or reading the game again).
    private void ReopenEditor()
    {
        if (OpenEditor?.Target is { } target && !Show(target)) map.HideEditors();
    }

    // Live values into the open card: boxes nobody typed in follow the game. A unit that is gone closes its card.
    private void RefreshEditor()
    {
        if (snapshot is not { } data) return;
        switch (OpenEditor)
        {
            case PlainRuleEditorCard plainCard when PlainSnapshot(plainCard.Table) is { } plain && ruleKept?.Profile is { } profile && profile.Table(plainCard.Table) is not null:
                if (plainCard.Table=="doctrine") plainCard.SetCostNames(catalog.Ideologies);
                plainCard.Follow(new(plain.Record(plainCard.RecordId),profile)); break;
            case OfficeEditorCard when officeRuleSnapshot is not null && ruleKept?.Profile is { } profile && profile.Table("office") is not null:
                officeEditor.Follow(new(officeRuleSnapshot.Record(officeEditor.Office), profile));
                break;
            case PolicyEditorCard card when policyRuleSnapshot is not null && ruleKept?.Profile is { } profile && profile.Table(card.Table) is not null:
                card.Follow(new(policyRuleSnapshot.Record(card.Table, card.RecordId), policyRuleSnapshot, profile));
                break;
            case FormationEditorCard when ruleSnapshot is not null && ruleKept?.Profile is { } profile:
                formationEditor.Follow(new(ruleSnapshot.Record(formationEditor.Formation), profile));
                break;
            case TacticEditorCard when tacticRuleSnapshot is not null && ruleKept?.Profile is { } profile && profile.Table("tactic") is not null:
                tacticEditor.Follow(new(tacticRuleSnapshot.Record(tacticEditor.Tactic), profile));
                break;
            case ForceEditorCard when data.ForceRecords.ContainsKey(forceEditor.Force):
                forceEditor.Follow(ForceView(data, forceEditor.Force));
                break;
            case CityEditorCard:
                cityEditor.Follow(CityView(data, cityEditor.City));
                break;
            case GateEditorCard when data.Gates.Any(g => g.Id == gateEditor.Gate):
                gateEditor.Follow(GateView(data, gateEditor.Gate));
                break;
            case FacilityEditorCard:
                if (facilityEditor.Identity?.Find(data) is { } facility) facilityEditor.Follow(FacilityView(data, facility));
                else
                {
                    map.HideEditors();
                    map.SelectedFacility = null;
                    AddLog(facilityEditor.Pending > 0 ? L.Facility.ClosedDropped(facilityEditor.DisplayName) : L.Facility.Closed(facilityEditor.DisplayName));
                }
                break;
            case OfficerEditorCard when data.OfficerById.TryGetValue(officerEditor.OfficerId, out OfficerRecord? officer):
                officerEditor.Follow(officer, Standing(data, officer));
                officerEditor.FollowRevival(data, officer);
                officerEditor.FollowStatusChange(data, officer);
                break;
            case PersonalityEditorCard when data.Personalities.Any(p => p.Index == personalityEditor.Personality):
                personalityEditor.Follow(PersonalityView(data, personalityEditor.Personality));
                break;
            case ItemEditorCard when data.Items.Any(i => i.Index == itemEditor.Item):
                itemEditor.Follow(ItemView(data, itemEditor.Item));
                break;
            case UnitEditorCard:
                if (data.Unit(unitEditor.Unit.Slot, unitEditor.Unit.Commander) is { } unit) unitEditor.Follow(UnitView(data, unit));
                else
                {
                    map.HideEditors();
                    AddLog(unitEditor.Pending > 0 ? L.Unit.GoneDropped(data.CommanderName(unitEditor.Unit.Commander)) : L.Unit.Gone(data.CommanderName(unitEditor.Unit.Commander)));
                }
                break;
        }
    }

    // After a write: the lists and the open card show what the game now holds.
    private void AfterWrite()
    {
        MergeSnapshot();
        ReopenEditor();
    }

    // A force's chip under the map opens its card; the chip of the force whose card is open closes it.
    private async Task ToggleForceAsync(int force)
    {
        if (OpenEditor == forceEditor && forceEditor.Force == force) await CloseEditorAsync();
        else await OpenAsync(new ForceTarget(force));
    }

    // ---- What each card shows -------------------------------------------------------------------------

    private ForceEditorCard.View ForceView(GameSnapshot data, int force)
    {
        ForceRecord record = data.ForceRecords[force];
        int ruler = data.Forces[force].RulerId, strategist = (int)record[ForceFields.Strategist];
        var slots = Enumerable.Range(0, ForceFields.PolicyFields.Length)
            .Select(field => (IReadOnlyList<(int, int)>)Enumerable.Range(0, ForceFields.PolicySlots)
                .Select(slot => ((int)record[ForceFields.PolicyOfficer(field, slot)], (int)record[ForceFields.PolicyDays(field, slot)])).ToList())
            .ToArray();
        return new ForceEditorCard.View(force, data.ForceName(force), ForceSummary(data, force), MapView.ForceColor(force),
            data.OfficersOf(force).OrderBy(o => o.Name, StringComparer.CurrentCulture).Select(o => new GameName(o.Id, o.Name, "")).ToList(),
            (int)record[ForceFields.Country], (int)record[ForceFields.Color], strategist, (int)record[ForceFields.Rank], Relations(data, force),
            slots, PolicyHolders(data, force, slots, ruler, strategist), FixedPolicies(data, ruler, strategist),
            ForceCities(data, force), ForceGates(data, force), ForceMembers(data, force), ForceUnits(data, force), ForceMilitary(data, force), data.MilitaryCaps);
    }

    private static ForceEditorCard.MilitaryRow[] ForceMilitary(GameSnapshot data, int force) => data.CorpsRecords.Values
        .Where(c => c.Force == force && c.Number > 0).OrderBy(c => c.Number).ThenBy(c => c.Id)
        .Select(c => new ForceEditorCard.MilitaryRow(c.Id, c.Number, c.Leader, data.OfficerName(c.Leader), c.MilitaryPoints)).ToArray();

    // The force's cities as the cities' list shows them (ForceEditorCard.CityColumns).
    private static List<GameTable.Row> ForceCities(GameSnapshot data, int force)
    {
        var rows = new List<GameTable.Row>();
        foreach (int id in data.CitiesOf(force))
        {
            CityRecord city = data.Cities[id];
            rows.Add(new GameTable.Row(id, [CityNames.ById[id], CityFields.ScaleName(city[CityFields.Scale]), data.OfficerName(city.Governor),
                city[CityFields.Troops], city[CityFields.Gold], city[CityFields.Food]]));
        }
        return rows;
    }

    // The force's gates (ForceEditorCard.GateColumns).
    private static List<GameTable.Row> ForceGates(GameSnapshot data, int force) =>
        data.GatesOf(force).Select(g => new GameTable.Row(g.Id, [g.Name, data.OfficerName(g.Governor), g[GateFields.Troops], g[GateFields.Morale], (long)g.Durability]))
            .ToList();

    private static List<(string, GameTable.Row)> ForceMembers(GameSnapshot data, int force) =>
        OfficerTables.Order(data.OfficersOf(force)).Select(OfficerTables.RowOf).ToList();

    // The force's units, transports too, as the units' list shows them (ForceEditorCard.UnitColumns).
    private static List<GameTable.Row> ForceUnits(GameSnapshot data, int force) =>
        data.Units.Where(u => data.ForceOfUnit(u) == force).OrderBy(u => u.Kind).ThenByDescending(u => u[UnitFields.Troops])
            .Select(u => new GameTable.Row((u.Slot, u.Commander), [data.OfficerById.GetValueOrDefault(u.Commander)?.Face ?? -1, data.CommanderName(u.Commander),
                UnitFields.KindName(u.Kind), data.PlaceName(u), .. UnitFields.All.Select(f => (object?)u[f])]))
            .ToList();

    private static string ForceSummary(GameSnapshot data, int force)
    {
        int[] cities = data.CitiesOf(force).ToArray();
        int gates = data.GatesOf(force).Count();
        int officers = data.OfficersOf(force).Count();
        long troops = cities.Sum(id => data.Cities[id][CityFields.Troops]) + data.Units.Where(u => data.ForceOfUnit(u) == force).Sum(u => u[UnitFields.Troops]);
        string ruler = data.Forces.TryGetValue(force, out ForceInfo info) ? data.OfficerName(info.RulerId) : "";
        return gates > 0 ? L.Force.SummaryWithGates(ruler, cities.Length, gates, officers, troops) : L.Force.Summary(ruler, cities.Length, officers, troops);
    }

    // The other forces that still have a ruler, with this force's 친선 and the months left of an agreement with each.
    private static List<ForceEditorCard.Relation> Relations(GameSnapshot data, int force)
    {
        ForceRecord own = data.ForceRecords[force];
        return data.ForceRecords.Keys.Where(other => other != force && data.Forces.TryGetValue(other, out ForceInfo info) && info.RulerId is > 0 and < 0xFFFF)
            .OrderBy(other => other)
            .Select(other => new ForceEditorCard.Relation(other, data.ForceName(other), MapView.ForceColor(other), (int)own[ForceFields.Friendship(other)],
                ForceFields.AgreementMonths(own, other), ForceFields.CanAlly(own) && ForceFields.CanAlly(data.ForceRecords[other])))
            .ToList();
    }

    // The force's officers who can carry out a policy, and those already in its slots (to name them). The ruler and the
    // 군사 are not offered: their policies work without a slot.
    private List<ForceEditorCard.PolicyHolder> PolicyHolders(GameSnapshot data, int force, IReadOnlyList<(int Officer, int Days)>[] slots, int ruler, int strategist)
    {
        IEnumerable<OfficerRecord> members = data.OfficersOf(force)
            .Concat(slots.SelectMany(s => s).Where(s => s.Officer != 0).Select(s => data.OfficerById.GetValueOrDefault(s.Officer)).OfType<OfficerRecord>())
            .DistinctBy(o => o.Id);
        var holders = new List<ForceEditorCard.PolicyHolder>();
        foreach (OfficerRecord officer in members)
        {
            int policy = (int)officer[OfficerFields.Policy];
            int field = ForceFields.PolicyFieldOf(policy);
            if (field < 0) continue;
            GameName name = catalog.Policies.FirstOrDefault(p => p.Id == policy);
            holders.Add(new ForceEditorCard.PolicyHolder(officer.Id, officer.Id == ruler || officer.Id == strategist ? -1 : field,
                $"{officer.Name} · {name.Name ?? L.Terms.PolicyNumber(policy)}", L.Force.PolicyHolderTip(name.Description, officer[OfficerFields.PolicyLevel]).TrimStart()));
        }
        return holders;
    }

    // The policies that work without a slot: the ruler's and the 군사's own.
    private string FixedPolicies(GameSnapshot data, int ruler, int strategist)
    {
        string Own(int id) => data.OfficerById.TryGetValue(id, out OfficerRecord? officer)
            ? officer.Name + " " + (GameCatalog.NameOf(catalog.Policies, officer[OfficerFields.Policy]) is { Length: > 0 } policy ? policy : "-") : "";
        string text = string.Join(" · ", new[] { (L.Force.RulerLabel, Own(ruler)), (L.Force.StrategistLabel, strategist == 0 ? "" : Own(strategist)) }.Where(p => p.Item2.Length > 0).Select(p => $"{p.Item1} {p.Item2}"));
        return text.Length == 0 ? "" : L.Force.AlwaysInEffect(text);
    }

    private static CityEditorCard.View CityView(GameSnapshot data, int id)
    {
        CityRecord city = data.Cities[id];
        int force = data.ForceOfCity(id);
        var values = new Dictionary<FieldDef, long>();
        foreach (FieldDef field in CityFields.All)
        {
            if (field.Record != RecordKind.Building) values[field] = city[field];
            else if (data.CastleByCity.TryGetValue(id, out BuildingRecord? castle)) values[field] = castle[field];
        }
        CountyRecord[] owned = data.Counties.Where(c => c.HomeCity == id).ToArray();
        var counties = owned.Select(c => new CityEditorCard.County(c.Index, c.Name, c.Name == city.Name,
            c.CurrentCity == c.HomeCity || c.CurrentCity is < 1 or > GameMemory.CityCount ? "" : CityNames.ById[c.CurrentCity],
            CountyFields.All.ToDictionary(f => f, f => c[f]))).ToList();
        var officers = OfficerTables.Order(data.Officers.Where(o => o.BaseId == id && OfficerFields.IsActive(o.Status))).Select(OfficerTables.RowOf).ToList();
        // Who may govern: the force's 태수 and 일반 officers in the city now (and whoever governs it now).
        var governors = data.Officers.Where(o => o.Id == city.Governor || CityEdits.MayGovern(data, id, o.Id))
            .OrderByDescending(o => o[OfficerFields.Leadership] + o[OfficerFields.Politics])
            .Select(o => new GameName(o.Id, o.Name, L.Force.StrategistChoiceTip(o[OfficerFields.Leadership], o[OfficerFields.Politics]))).ToList();
        bool fixedGovernor = data.OfficerById.GetValueOrDefault(city.Governor) is { Status: 1 or 2 };
        return new CityEditorCard.View(id, CityNames.ById[id], force, data.ForceName(force), MapView.ForceColor(force), CityFields.ScaleName(city[CityFields.Scale]),
            city.Governor, data.OfficerName(city.Governor), values, counties, officers, governors, fixedGovernor, CityIndependence.Refusal(data, id));
    }

    private static GateEditorCard.View GateView(GameSnapshot data, int id)
    {
        GateRecord gate = data.Gates.Single(g => g.Id == id);
        int force = data.ForceOfGate(gate);
        var values = new Dictionary<FieldDef, long>();
        foreach (FieldDef field in GateFields.All)
        {
            if (field.Record != RecordKind.Building) values[field] = gate[field];
            else if (gate.Building is { } building) values[field] = building[field];
        }
        var officers = OfficerTables.Order(data.Officers.Where(o => o.BaseId == id && OfficerFields.IsActive(o.Status))).Select(OfficerTables.RowOf).ToList();
        // Who may govern: the force's 태수 and 일반 officers in the gate now (and whoever governs it now), the ones who
        // lead and fight best first.
        var governors = data.Officers.Where(o => o.Id == gate.Governor || GateEdits.MayGovern(data, gate, o.Id))
            .OrderByDescending(o => o[OfficerFields.Leadership] + o[OfficerFields.Strength])
            .Select(o => new GameName(o.Id, o.Name, L.Gate.GovernorChoiceTip(o[OfficerFields.Leadership], o[OfficerFields.Strength]))).ToList();
        bool fixedGovernor = data.OfficerById.GetValueOrDefault(gate.Governor) is { Status: 1 or 2 };
        return new GateEditorCard.View(id, gate.Name, force, data.ForceName(force), MapView.ForceColor(force), gate.Governor, data.OfficerName(gate.Governor),
            values, officers, governors, fixedGovernor);
    }

    private static FacilityEditorCard.View FacilityView(GameSnapshot data, Facility facility) =>
        new(facility, data.ForceName(facility.Force), MapView.ForceColor(facility.Force));

    private UnitEditorCard.View UnitView(GameSnapshot data, UnitRecord unit)
    {
        int force = data.ForceOfUnit(unit);
        string destination = data.Destination(unit) is { } tile
            ? $"{UnitFields.OrderName(unit.Order)} → {data.DestinationName(unit, tile)}" : "";
        // Each 진형 with its 기동 in the list itself ("추행 · 기동 26"), as that is what is picked for.
        List<GameName> formations = UnitFields.LandFormations.Where(f => UnitFields.MayUseFormation(unit.Kind, unit.Commander, f))
            .Select(catalog.Formation).Select(f => f.Description.Length > 0 ? new GameName(f.Id, $"{f.Name} · {f.Description}", "") : f).ToList();
        return new UnitEditorCard.View(unit.Slot, unit.Commander, data.CommanderName(unit.Commander), force, data.ForceName(force), MapView.ForceColor(force),
            UnitFields.KindName(unit.Kind), UnitFields.FormationName(unit.Formation), data.PlaceName(unit), destination,
            UnitFields.All.Concat(UnitFields.Timed).ToDictionary(f => f, f => unit[f]), unit.IsArmy, unit.Formation, formations, UnitConditions.Read(data, unit));
    }

    // A 개성 with its condition, how long it lasts, its effects, the kinds there are (named after the 개성 with each
    // alone, with whom each reaches; PersonalityEdits, PersonalityRules) and the officers who have it.
    private static PersonalityEditorCard.View PersonalityView(GameSnapshot data, int index)
    {
        PersonalityRecord personality = data.Personalities.Single(p => p.Index == index);
        Dictionary<int, string> names = PersonalityEdits.EffectNames(data);
        HashSet<int> switches = PersonalityEdits.Switches(data);
        var effects = Enumerable.Range(0, PersonalityFields.Values.Length)
            .Select(e => new PersonalityEditorCard.EffectShown(personality.Kind(e), personality.Value(e))).ToList();
        var kinds = names.OrderBy(n => n.Key)
            .Select(n => new GameName(n.Key, PersonalityEdits.KindName(names, n.Key) + (switches.Contains(n.Key) ? $" · {L.Personality.SwitchOnly}" : ""), ReachText(n.Key)))
            .ToList();
        var holders = PersonalityEdits.Holders(data, index).OrderBy(o => o.Name, StringComparer.CurrentCulture)
            .Select(o => (o.Id, o.Name, OfficerFields.IsActive(o.Status))).ToList();
        return new PersonalityEditorCard.View(index, personality.Name, personality.Description, personality.Condition, personality.ConditionValue,
            personality.Filter, personality.Timing, personality.Days, effects, kinds, switches, holders);
    }

    // Whom an effect kind reaches, as the game reads it.
    private static string ReachText(int kind) => PersonalityRules.ReachOf(kind) switch
    {
        PersonalityRules.Reach.Unit => L.PersonalityRules.ReachUnit,
        PersonalityRules.Reach.Received => L.PersonalityRules.ReachReceived,
        PersonalityRules.Reach.Own => L.PersonalityRules.ReachOwn,
        PersonalityRules.Reach.OwnLit => L.PersonalityRules.ReachOwnLit,
        PersonalityRules.Reach.Officer => L.PersonalityRules.ReachOfficer,
        PersonalityRules.Reach.OfficerAlways => L.PersonalityRules.ReachOfficerAlways,
        PersonalityRules.Reach.Guard => L.PersonalityRules.ReachGuard,
        PersonalityRules.Reach.Event => L.PersonalityRules.ReachEvent,
        _ => L.PersonalityRules.NoReader
    };

    private static ItemEditorCard.View ItemView(GameSnapshot data, int index)
    {
        ItemRecord item = data.Items.Single(i => i.Index == index);
        return new ItemEditorCard.View(index, item.Name, item.Owner, data.OfficerName(item.Owner), ItemState(item), item.Ability, item.Bonus);
    }

    // Where a 명품 stands: held, not found yet, or (a state not known) by its number.
    private static string ItemState(ItemRecord item) => item.State switch
    {
        ItemFields.Found => item.Owner == 0 ? L.Item.NoOwner : L.Item.Found,
        ItemFields.Hidden => L.Item.Hidden,
        _ => L.Item.StateNumber(item.State)
    };

    private OfficerEditorCard.Standing Standing(GameSnapshot data, OfficerRecord officer)
    {
        int force = data.ForceOfCorps(officer.Corps);
        string office = GameCatalog.NameOf(catalog.Offices, officer[OfficerFields.Office]);
        // Where he is: in his base, leading a unit, or out (travelling, on a task).
        string place = data.Units.FirstOrDefault(u => u.Commander == officer.Id) is { } unit ? L.Officer.OnCampaign(data.PlaceName(unit))
            : officer[OfficerFields.PlaceField] == officer.BaseId ? L.Officer.InBase
            : OfficerFields.IsActive(officer.Status) ? L.Officer.OutsideBase : "";
        string corps = officer.Corps == 0 ? "" : L.Officer.CorpsNumber(officer.Corps) + (data.CorpsLeader(officer.Corps) is > 0 and var leader ? (" · " + L.Officer.CorpsLeader(data.OfficerName(leader))) : "");
        int lord = (int)officer[OfficerFields.FormerLord], months = (int)officer[OfficerFields.FormerLordMonths];
        string formerLord = lord is > 0 and < 10000 && data.OfficerName(lord) is { Length: > 0 } name ? name + (months > 0 ? (" " + L.Officer.NotServingMonths(months)) : "") : "";
        return new OfficerEditorCard.Standing(force, data.ForceName(force), MapView.ForceColor(force), OfficerFields.StatusName(officer.Status),
            officer.BaseId, CityNames.BaseName(officer.BaseId), office == "없음" ? "" : office, corps, place, formerLord,
            officer.Status is >= 1 and <= 4 ? Transfers.Blocker(data, officer, Transfer.Move) : "",
            officer.Status is >= 1 and <= 4 ? Transfers.Blocker(data, officer, Transfer.Defect) : "");
    }

    private void ShowOfficer(GameSnapshot data, OfficerRecord officer)
    {
        // Names that repeat (the game has a few) carry their number.
        var repeated = data.Officers.GroupBy(o => o.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var others = data.Officers.Where(o => o.Id != officer.Id).OrderBy(o => o.Name, StringComparer.CurrentCulture)
            .Select(o => new OfficerEditorCard.Other(o.Id, repeated.Contains(o.Name) ? $"{o.Name} (#{o.Id})" : o.Name, (int)o[OfficerFields.Gender]))
            .ToList();
        int group = (int)officer[OfficerFields.Sworn];
        List<int> sworn = group == 0 ? [] : data.Officers.Where(o => o[OfficerFields.Sworn] == group).Select(o => o.Id).ToList();
        int force = data.ForceOfCorps(officer.Corps);
        // 명품 that can change hands: found ones (held or not) and those still to be found.
        var items = data.Items.Where(i => i.State is ItemFields.Found or ItemFields.Hidden)
            .Select(i => new OfficerEditorCard.Treasure(i.Index, i.Name, i.Owner, Holder(data, i),
                ItemFields.AbilityName(i.Ability) is { Length: > 0 } ability && i.Bonus > 0 ? $"{ability} +{i.Bonus}" : ""))
            .ToList();
        // The offices the force's other officers hold: giving one takes it from them.
        var holders = data.OfficersOf(force).Where(o => o.Id != officer.Id && o[OfficerFields.Office] != OfficerFields.NoOffice)
            .GroupBy(o => (int)o[OfficerFields.Office]).ToDictionary(g => g.Key, g => g.First().Name);
        officerEditor.Open(officer, Standing(data, officer), others, sworn, items, holders, Transfers.Targets(data));
        officerEditor.FollowRevival(data, officer, opening: true);
        officerEditor.FollowStatusChange(data, officer, opening: true);
        LoadPortrait();
    }

    // Who holds a 명품: an officer's name, "미발견" for one still to be found, "" for none.
    private static string Holder(GameSnapshot data, ItemRecord item) =>
        item.Owner == 0 ? (item.State == ItemFields.Hidden ? L.Item.Hidden : "")
        : data.OfficerName(item.Owner) is { Length: > 0 } holder ? holder : L.Terms.OfficerNumber(item.Owner);

    // The officer card's full portrait, read in the background.
    private void LoadPortrait()
    {
        int face = snapshot?.OfficerById.GetValueOrDefault(officerEditor.OfficerId)?.Face ?? -1;
        if (face == portraitFace) return;
        portraitFace = face;
        portraitLoad?.Cancel();
        officerEditor.SetPortrait(null);
        if (faces is null || face < 0) return;
        var cancel = portraitLoad = new CancellationTokenSource();
        FaceLibrary library = faces;
        Task.Run(() =>
        {
            using Bitmap? portrait = library.Portrait(face);
            return portrait is null ? null : ImageOf(portrait);
        }).ContinueWith(task => Dispatcher.InvokeAsync(() =>
        {
            if (!cancel.IsCancellationRequested && portraitFace == face && task.IsCompletedSuccessfully) officerEditor.SetPortrait(task.Result);
        }));
    }

    // A bitmap as a frozen WPF image (made on any thread).
    private static WMI.BitmapSource ImageOf(Bitmap bitmap)
    {
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var image = WMI.BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, WM.PixelFormats.Bgra32, null, data.Scan0, data.Stride * bitmap.Height, data.Stride);
            image.Freeze();
            return image;
        }
        finally { bitmap.UnlockBits(data); }
    }

    // ---- [변경 적용] ----------------------------------------------------------------------------------

    private bool CanWrite(out GameSnapshot data)
    {
        data = snapshot!;
        return !busy && !layoutRecovering && game is not null && layout is not null && snapshot is not null;
    }

    // Each card's [변경 적용]: the card says what it asks, the core plans the writes (with what changes along with
    // them), and the editor writes them.
    private async Task<bool> ApplyForceAsync()
    {
        if (!CanWrite(out GameSnapshot data)) return false;
        List<(int Force, int Value)> friendships = forceEditor.FriendshipChanges(out string error);
        if (error.Length > 0)
        {
            status.Text = error;
            return false;
        }
        List<(int Force, int Value)> alliances = forceEditor.AllianceChanges(out error);
        if (error.Length > 0)
        {
            status.Text = error;
            return false;
        }
        var military = forceEditor.MilitaryChanges(out error);
        if (military is null) { status.Text = error; return false; }
        return await WritePlanAsync(ForceEdits.Plan(data, forceEditor.Force, forceEditor.Changes(), friendships, alliances, military));
    }

    private async Task<bool> ApplyCityAsync()
    {
        if (!CanWrite(out GameSnapshot data)) return false;
        if (cityEditor.Changes(out string error) is not { } request)
        {
            status.Text = error;
            return false;
        }
        return await WritePlanAsync(CityEdits.Plan(data, cityEditor.City, request));
    }

    private async Task<bool> ApplyGateAsync()
    {
        if (!CanWrite(out GameSnapshot data)) return false;
        if (gateEditor.Changes(out string error) is not { } request)
        {
            status.Text = error;
            return false;
        }
        return await WritePlanAsync(GateEdits.Plan(data, gateEditor.Gate, request));
    }

    private async Task<bool> ApplyFacilityAsync()
    {
        if (!CanWrite(out GameSnapshot data) || facilityEditor.Identity is not { } target) return false;
        if (facilityEditor.Changes(out string error) is not { } durability)
        {
            status.Text = error;
            return false;
        }
        return await WritePlanAsync(FacilityEdits.Plan(data, target.Index, target.Kind, target.Tile, target.Force, durability));
    }

    private async Task<bool> ApplyOfficerAsync()
    {
        if (!CanWrite(out GameSnapshot data)) return false;
        if (officerEditor.Changes(out string error) is not { } request)
        {
            status.Text = error;
            return false;
        }
        return await WritePlanAsync(OfficerEdits.Plan(data, officerEditor.OfficerId, request));
    }

    private async Task<bool> ApplyUnitAsync()
    {
        if (!CanWrite(out GameSnapshot data)) return false;
        if (unitEditor.Changes(out string error) is not { } changes)
        {
            status.Text = error;
            return false;
        }
        (int slot, int commander) = unitEditor.Unit;
        return await WritePlanAsync(UnitEdits.Plan(data, slot, commander, changes));
    }

    // A 개성's values reach every officer who has it: the write is asked about with how many.
    private async Task<bool> ApplyPersonalityAsync()
    {
        if (!CanWrite(out GameSnapshot data)) return false;
        if (personalityEditor.Changes(out string error) is not { } changes)
        {
            status.Text = error;
            return false;
        }
        EditPlan plan = PersonalityEdits.Plan(data, personalityEditor.Personality, changes);
        if (!plan.Refused && plan.Edits.Count > 0 && !ConfirmWrite(plan.Summary)) return false;
        return await WritePlanAsync(plan);
    }

    private async Task<bool> ApplyItemAsync()
    {
        if (!CanWrite(out GameSnapshot data)) return false;
        if (itemEditor.Changes(out string error) is not { } changes)
        {
            status.Text = error;
            return false;
        }
        return await WritePlanAsync(ItemEdits.Plan(data, itemEditor.Item, changes));
    }

    private async Task<bool> WritePlanAsync(EditPlan plan)
    {
        if (!plan.Refused) return await WriteEditsAsync([.. plan.Edits]);
        status.Text = plan.Refusal;
        return false;
    }

    // Writes a card's edits; nothing to write counts as done (the card had only values equal to the game's).
    private async Task<bool> WriteEditsAsync(List<RecordEdit> edits)
    {
        edits = edits.Where(e => e.Changes.Count > 0).ToList();
        if (edits.Count == 0)
        {
            status.Text = L.Window.NothingChanged;
            ReopenEditor();
            return true;
        }
        if (!await WriteAsync(edits)) return false;
        AfterWrite();
        return true;
    }
}

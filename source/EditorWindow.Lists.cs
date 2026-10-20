using WC = System.Windows.Controls;

namespace San14Tenkazu;

// The right pane's lists (ListsCard): 세력, 도시, 장수, 부대, 개성, 명품 and 고대무장, each with a search box and filters.
// A number cell edits directly; its header's fill button writes one value to all eligible filtered rows.
// Other cells (or ↑↓) open the row's editor and show it on the map.
internal sealed partial class EditorWindow
{
    private const int ForceTab = (int)ListKind.Forces, CityTab = (int)ListKind.Cities, OfficerTab = (int)ListKind.Officers,
        UnitTab = (int)ListKind.Units, PersonalityTab = (int)ListKind.Personalities, ItemTab = (int)ListKind.Items, AncientTab = (int)ListKind.Ancients, FormationTab = (int)ListKind.Formations, TacticTab = (int)ListKind.Tactics;
    private readonly ListsCard lists;
    private readonly GameTable forceTable = new([new(L.Columns.Force, GameTable.Kind.Shield, 12), new(L.Columns.Ruler, GameTable.Kind.Text, 10), new(L.Columns.Rank, GameTable.Kind.Text, 9),
        new(L.Columns.Cities, GameTable.Kind.Number, 6), new(L.Columns.Officers, GameTable.Kind.Number, 6), new(L.Columns.Troops, GameTable.Kind.Number, 11)], 28);
    private readonly GameTable cityTable = new([new(L.Columns.City, GameTable.Kind.Text, 10), new(L.Columns.Force, GameTable.Kind.Shield, 12), new(L.Columns.Scale, GameTable.Kind.Text, 6),
        new(L.Columns.Governor, GameTable.Kind.Text, 10), new(L.Columns.Troops, GameTable.Kind.Number, 10, CityFields.Troops), new(L.Columns.Gold, GameTable.Kind.Number, 10, CityFields.Gold), new(L.Columns.Food, GameTable.Kind.Number, 10, CityFields.Food)], 28);
    // The officers' text cells hold two lines (the name over the 자, the force over the base); the years are on the
    // officer card, the list keeps what one compares officers by.
    private readonly GameTable officerTable = new([new("", GameTable.Kind.Face), new(L.Columns.NameCourtesy, GameTable.Kind.TwoLines, 13), new(L.Columns.ForceBase, GameTable.Kind.TwoLines, 17),
        new(L.Columns.Status, GameTable.Kind.Text, 8), .. ((FieldDef[])[OfficerFields.Loyalty, .. OfficerFields.Abilities]).Select(f => new GameTable.Column(f.Label, GameTable.Kind.Number, 8, f))], OfficerRowHeight);
    private readonly GameTable unitTable = new([new("", GameTable.Kind.Face), new(L.Columns.Commander, GameTable.Kind.Text, 13), new(L.Columns.Force, GameTable.Kind.Shield, 12), new(L.Columns.Kind, GameTable.Kind.Text, 7),
        new(L.Columns.Location, GameTable.Kind.Text, 16), .. UnitFields.All.Select(f => new GameTable.Column(f.Label, GameTable.Kind.Number, 10, f))], UnitRowHeight);
    // 개성 and 명품: the two tables of the game's that are saved with it (PersonalityEdits, ItemEdits).
    private readonly GameTable personalityTable = new([new(L.Columns.Personality, GameTable.Kind.Text, 8), new(L.Columns.Does, GameTable.Kind.Text, 26),
        new(L.Columns.Value1, GameTable.Kind.Number, 7, PersonalityFields.Values[0]), new(L.Columns.Value2, GameTable.Kind.Number, 7, PersonalityFields.Values[1]), new(L.Columns.Holders, GameTable.Kind.Number, 8)], 28);
    private readonly GameTable itemTable = new([new(L.Columns.Item, GameTable.Kind.Text, 10), new(L.Columns.Ability, GameTable.Kind.Text, 9), new(L.Columns.Owner, GameTable.Kind.Text, 10),
        new(L.Columns.State, GameTable.Kind.Text, 8)], 28);
    private readonly GameTable formationTable = new([new(L.Rules.Formation, GameTable.Kind.Text, 12), .. FormationRules.Fields.Select(f => new GameTable.Column(f.Label, GameTable.Kind.Number, 7)), new(L.Rules.State, GameTable.Kind.Text, 12)], 28);
    private readonly SearchBox formationSearch = new(L.Rules.Search);
    private readonly WC.TextBlock formationCount = new();
    private readonly GameTable tacticTable = new([new(L.Rules.TacticPage, GameTable.Kind.Text, 12), new(L.Personality.Effect1, GameTable.Kind.Text, 15),
        new(L.Personality.Effect2, GameTable.Kind.Text, 15), new(L.Rules.AbilityBasis, GameTable.Kind.Text, 8), new(L.Rules.State, GameTable.Kind.Text, 10)], 28);
    private readonly SearchBox tacticSearch = new(L.Rules.TacticSearch);
    private readonly WC.TextBlock tacticCount = new();
    private readonly GameTable policyTable = new([new(L.Rules.PolicyPage, GameTable.Kind.Text, 12), new(L.Rules.PolicyEffects, GameTable.Kind.Text, 25), new(L.Rules.State, GameTable.Kind.Text, 10)], 28);
    private readonly GameTable policyEffectTable = new([new(L.Rules.PolicyEffects, GameTable.Kind.Text, 14), new(L.Rules.PolicyLevelValues, GameTable.Kind.Text, 15), new(L.Rules.State, GameTable.Kind.Text, 10)], 28);
    private readonly SearchBox policySearch = new(L.Rules.PolicySearch), policyEffectSearch = new(L.Rules.PolicySearch);
    private readonly WC.TextBlock policyCount = new(), policyEffectCount = new();
    private readonly GameTable officeTable = new([new(L.Rules.OfficePage, GameTable.Kind.Text, 12), new(L.Rules.OfficeGrade, GameTable.Kind.Number, 6),
        new(L.Rules.OfficeCapacity, GameTable.Kind.Number, 10), new(L.Rules.State, GameTable.Kind.Text, 10)], 28);
    private readonly SearchBox officeSearch = new(L.Rules.OfficeSearch);
    private readonly WC.TextBlock officeCount = new();
    private readonly GameTable doctrineTable=new([new(L.Rules.DoctrinePage,GameTable.Kind.Text,14),new(L.Rules.DoctrineEffect,GameTable.Kind.Number,8),new(L.Rules.DoctrineCosts,GameTable.Kind.Text,18),new(L.Rules.State,GameTable.Kind.Text,10)],28);
    private readonly SearchBox doctrineSearch=new(L.Rules.DoctrineSearch);
    private readonly WC.TextBlock doctrineCount=new();
    private readonly GameTable rankTable=new([new(L.Rules.RankPage,GameTable.Kind.Text,12),new(L.Rules.OfficeGrade,GameTable.Kind.Number,6),new(L.Rules.OfficeCapacity,GameTable.Kind.Number,10),new(L.Rules.State,GameTable.Kind.Text,10)],28);
    private readonly SearchBox rankSearch=new(L.Rules.RankSearch);
    private readonly WC.TextBlock rankCount=new();
    // 고대무장: whether each may appear in a new game, and what lets him (AncientOfficers); their bits in the play
    // record are the player's, so [모두 해금] is the page's own, not a card's.
    private readonly GameTable ancientTable = new([new("", GameTable.Kind.Face), new(L.Columns.NameCourtesy, GameTable.Kind.TwoLines, 12),
        new(L.Columns.State, GameTable.Kind.Text, 7), new(L.Columns.Unlock, GameTable.Kind.Text, 17)], OfficerRowHeight);
    private readonly PlateButton unlockButton = new(L.Ancient.UnlockAll, icon: Icons.Unlock) { ToolTip = L.Ancient.UnlockAllTip };
    private readonly SearchBox forceSearch = new(L.Lists.SearchForces), citySearch = new(L.Lists.SearchCities), officerSearch = new(L.Lists.SearchOfficers), unitSearch = new(L.Lists.SearchUnits),
        personalitySearch = new(L.Lists.SearchPersonalities), itemSearch = new(L.Lists.SearchItems), ancientSearch = new(L.Lists.SearchAncients);
    private readonly WC.ComboBox cityForceBox = FilterBox(), officerForceBox = FilterBox(), officerStatusBox = FilterBox(), unitForceBox = FilterBox(), unitKindBox = FilterBox(), ancientLockBox = FilterBox();
    private readonly WC.TextBlock forceCount = new(), cityCount = new(), officerCount = new(), unitCount = new(), personalityCount = new(), itemCount = new(), ancientCount = new();
    private readonly PlateButton batchSurrenderButton = new(L.Surrender.BatchButton, icon: Icons.Force)
    { IsEnabled = false, ToolTip = L.Surrender.BatchButtonTip };
    // The forces the filters offer (index 0 is "모든 세력"), refreshed with the lists.
    private List<int> forceChoices = [];
    private bool filling;
    private readonly bool[] listDirty = new bool[Enum.GetValues<ListKind>().Length];
    private GameTable[] ListTables => [forceTable, cityTable, officerTable, unitTable, personalityTable, itemTable, ancientTable, formationTable, tacticTable, policyTable, policyEffectTable, officeTable, rankTable, doctrineTable];

    // Portraits for the lists and the officer card, read from the installed game.
    private FaceLibrary? faces;
    private bool faceLoading;
    private readonly Dictionary<int, IconCache.Icon> faceIcons = [];
    private CancellationTokenSource? iconLoad, portraitLoad;
    private Task? iconTask;
    private int iconSize;
    private bool iconCacheDirty;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(int Face, IconCache.Icon Icon)> decodedIcons = new();
    private readonly System.Windows.Threading.DispatcherTimer iconTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private static WC.ComboBox FilterBox() => EditorCard.Choice(160);

    // The pages of the lists, their filters and what a pick does.
    private void BuildLists()
    {
        lists.AddPage(L.Lists.ForcesTab, forceTable, [forceSearch, batchSurrenderButton], forceCount);
        lists.AddPage(L.Lists.CitiesTab, cityTable, [citySearch, cityForceBox], cityCount);
        lists.AddPage(L.Lists.OfficersTab, officerTable, [officerSearch, officerForceBox, officerStatusBox], officerCount);
        lists.AddPage(L.Lists.UnitsTab, unitTable, [unitSearch, unitForceBox, unitKindBox], unitCount);
        lists.AddPage(L.Lists.PersonalitiesTab, personalityTable, [personalitySearch], personalityCount, PaneSection.Data);
        lists.AddPage(L.Lists.ItemsTab, itemTable, [itemSearch], itemCount, PaneSection.Data);
        lists.AddPage(L.Lists.AncientTab, ancientTable, [ancientSearch, ancientLockBox, unlockButton], ancientCount, PaneSection.Ancients);
        lists.AddPage(L.Rules.Formation, formationTable, [formationSearch], formationCount, PaneSection.Data);
        lists.AddPage(L.Rules.TacticPage, tacticTable, [tacticSearch], tacticCount, PaneSection.Data);
        lists.AddPage(L.Rules.PolicyPage, policyTable, [policySearch], policyCount, PaneSection.Data);
        lists.AddPage(L.Rules.PolicyEffects, policyEffectTable, [policyEffectSearch], policyEffectCount, PaneSection.Data);
        lists.AddPage(L.Rules.OfficePage, officeTable, [officeSearch], officeCount, PaneSection.Data);
        lists.AddPage(L.Rules.RankPage,rankTable,[rankSearch],rankCount,PaneSection.Data);
        lists.AddPage(L.Rules.DoctrinePage,doctrineTable,[doctrineSearch],doctrineCount,PaneSection.Data);
        unlockButton.Click += () => _ = UnlockAncientsAsync();
        lists.PreparingTab += EnsureList;
        lists.TabChanged += FocusSearch;
        lists.ApplyClicked += async () => await ApplyListDraftsAsync();

        foreach (GameTable table in ListTables)
        {
            table.Faces = map.FaceBrush;
            // A click on a row (or ↑↓ through the list) opens its editor card and shows it on the map.
            table.RowPicked += tag => _ = OpenPickedAsync(table, tag);
        }
        ConfigureCellEditing(cityTable, tag => new CityTarget((int)tag));
        ConfigureCellEditing(officerTable, tag => new OfficerTarget((int)tag));
        ConfigureCellEditing(unitTable, tag => { var (slot, commander) = ((int, int))tag; return new UnitTarget(slot, commander); });
        ConfigureCellEditing(personalityTable, tag => new PersonalityTarget((int)tag));

        foreach (string choice in new[] { L.Lists.ActiveOfficers, L.Lists.AllOfficers }) officerStatusBox.Items.Add(choice);
        officerStatusBox.SelectedIndex = 0;
        foreach (string choice in new[] { L.Lists.ArmyUnits, L.Lists.AllUnits }) unitKindBox.Items.Add(choice);
        unitKindBox.SelectedIndex = 0;
        foreach (string choice in new[] { L.Lists.AllAncients, L.Lists.LockedAncients }) ancientLockBox.Items.Add(choice);
        ancientLockBox.SelectedIndex = 0;
        foreach (WC.ComboBox box in new[] { cityForceBox, officerForceBox, unitForceBox })
        {
            box.Items.Add(L.Lists.AllForces);
            box.SelectedIndex = 0;
        }
        foreach (var (kind, box) in new (ListKind, WC.ComboBox)[] {
            (ListKind.Cities, cityForceBox), (ListKind.Officers, officerForceBox), (ListKind.Officers, officerStatusBox),
            (ListKind.Units, unitForceBox), (ListKind.Units, unitKindBox), (ListKind.Ancients, ancientLockBox) })
            box.SelectionChanged += (_, _) =>
            {
                if (filling) return;
                listDirty[(int)kind] = true;
                if (lists.Tab == (int)kind) RefreshLists(currentOnly: true);
            };
        foreach (var (kind, box) in new (ListKind, SearchBox)[] {
            (ListKind.Forces, forceSearch), (ListKind.Cities, citySearch), (ListKind.Officers, officerSearch),
            (ListKind.Units, unitSearch), (ListKind.Personalities, personalitySearch), (ListKind.Items, itemSearch),
            (ListKind.Ancients, ancientSearch), (ListKind.Formations, formationSearch), (ListKind.Tactics, tacticSearch),
            (ListKind.Policies, policySearch), (ListKind.PolicyEffects, policyEffectSearch), (ListKind.Offices, officeSearch),
            (ListKind.Ranks, rankSearch), (ListKind.Doctrines, doctrineSearch) })
            box.Box.TextChanged += (_, _) =>
            {
                listDirty[(int)kind] = true;
                if (lists.Tab != (int)kind) return;
                searchTimer.Stop(); searchTimer.Start();
            };

    }

    private void FocusSearch()
    {
        SearchBox box = lists.Tab switch
        {
            ForceTab => forceSearch, CityTab => citySearch, OfficerTab => officerSearch, PersonalityTab => personalitySearch, ItemTab => itemSearch,
            AncientTab => ancientSearch, FormationTab => formationSearch, TacticTab => tacticSearch,
            (int)ListKind.Policies => policySearch, (int)ListKind.PolicyEffects => policyEffectSearch, (int)ListKind.Offices => officeSearch, (int)ListKind.Ranks => rankSearch, (int)ListKind.Doctrines => doctrineSearch, _ => unitSearch
        };
        box.FocusAll();
    }

    // ---- Filling the lists ---------------------------------------------------------------------------

    private void FillLists() => RefreshLists(currentOnly: false);

    private void RefreshLists(bool currentOnly)
    {
        if (!currentOnly)
        {
            Array.Fill(listDirty, true);
            // Drafts follow live values even when their rows are filtered out or their tab is hidden.
            if (snapshot is { } data) lists.TableDrafts.Follow((target, field) => target switch
            {
                CityTarget(int id) when id >= 1 && id < data.Cities.Length => data.Cities[id][field],
                OfficerTarget(int id) when data.OfficerById.TryGetValue(id, out var officer) => officer[field],
                UnitTarget(int slot, int commander) => data.Unit(slot, commander)?[field],
                PersonalityTarget(int id) => data.Personalities.FirstOrDefault(p => p.Index == id)?[field],
                _ => null
            });
            filling = true;
            try
            {
                FillForceChoices();
                if (snapshot is null)
                    foreach (GameTable table in ListTables) table.SetRows([]);
            }
            finally { filling = false; }
            LoadOfficerIcons();
        }
        else listDirty[lists.Tab] = true;
        if (lists.Shown) EnsureList(lists.Tab);
        UpdateControls();
    }

    // Called before a tab is exposed, including reopening the same tab after the pane was hidden.
    private void EnsureList(int tab)
    {
        if (!listDirty[tab]) return;
        bool Include(ListKind kind) => tab == (int)kind;
        filling = true;
        try
        {
            if (Include(ListKind.Forces)) FillTable(forceTable, ForceRows(), forceCount, L.Lists.ForceCount);
            if (Include(ListKind.Cities)) FillTable(cityTable, CityRows(), cityCount, L.Lists.CityCount);
            if (Include(ListKind.Officers)) FillTable(officerTable, OfficerRows(), officerCount, L.Lists.OfficerCount);
            if (Include(ListKind.Units)) FillTable(unitTable, UnitRows(), unitCount, L.Lists.UnitCount);
            if (Include(ListKind.Personalities)) FillTable(personalityTable, PersonalityRows(), personalityCount, L.Lists.PersonalityCount);
            if (Include(ListKind.Items)) FillTable(itemTable, ItemRows(), itemCount, L.Lists.ItemCount);
            if (Include(ListKind.Formations)) FillTable(formationTable, FormationRows(), formationCount, L.Rules.Count);
            if (Include(ListKind.Tactics)) FillTable(tacticTable, TacticRows(), tacticCount, L.Rules.TacticCount);
            if (Include(ListKind.Policies)) FillTable(policyTable, PolicyRows("policy", policySearch), policyCount, L.Rules.PolicyCount);
            if (Include(ListKind.PolicyEffects)) FillTable(policyEffectTable, PolicyRows("policyeffect", policyEffectSearch), policyEffectCount, L.Rules.PolicyEffectCount);
            if (Include(ListKind.Offices)) FillTable(officeTable, OfficeRows(), officeCount, L.Rules.OfficeCount);
            if (Include(ListKind.Doctrines)) FillTable(doctrineTable,PlainRuleRows(doctrineRuleSnapshot,doctrineSearch),doctrineCount,L.Rules.DoctrineCount);
            if (snapshot is not null && ruleKept?.Profile?.Table("doctrine") is null) doctrineCount.Text=ruleNotice.Length>0 ? ruleNotice : L.Rules.NotReady;
            if (Include(ListKind.Ranks)) FillTable(rankTable,PlainRuleRows(rankRuleSnapshot,rankSearch),rankCount,L.Rules.RankCount);
            if (snapshot is not null && ruleKept?.Profile?.Table("rank") is null) rankCount.Text=ruleNotice.Length>0 ? ruleNotice : L.Rules.NotReady;
            if (snapshot is not null && ruleKept?.Profile is null) formationCount.Text = ruleNotice.Length > 0 ? ruleNotice : L.Rules.NotReady;
            if (snapshot is not null && ruleKept?.Profile?.Table("tactic") is null) tacticCount.Text = ruleNotice.Length > 0 ? ruleNotice : L.Rules.NotReady;
            if (snapshot is not null && ruleKept?.Profile?.Table("policy") is null) policyCount.Text = policyEffectCount.Text = ruleNotice.Length > 0 ? ruleNotice : L.Rules.NotReady;
            if (snapshot is not null && ruleKept?.Profile?.Table("office") is null) officeCount.Text = ruleNotice.Length > 0 ? ruleNotice : L.Rules.NotReady;
            if (Include(ListKind.Ancients)) FillAncients();
            listDirty[tab] = false;
        }
        finally { filling = false; }
    }

    // The force filters offer the forces that have a ruler, by name; a pick is kept as long as its force exists.
    private void FillForceChoices()
    {
        List<int> forces = snapshot is not { } data ? []
            : data.Forces.Values.Where(f => f.RulerId is not (0 or 0xFFFF)).Select(f => f.Id).OrderBy(id => data.ForceName(id), StringComparer.CurrentCulture).ToList();
        if (forces.SequenceEqual(forceChoices)) return;
        foreach (WC.ComboBox box in new[] { cityForceBox, officerForceBox, unitForceBox })
        {
            int picked = box.SelectedIndex > 0 && box.SelectedIndex <= forceChoices.Count ? forceChoices[box.SelectedIndex - 1] : 0;
            box.Items.Clear();
            box.Items.Add(L.Lists.AllForces);
            foreach (int force in forces) box.Items.Add(snapshot!.ForceName(force));
            box.SelectedIndex = picked == 0 ? 0 : forces.IndexOf(picked) + 1;
        }
        forceChoices = forces;
    }

    private int PickedForce(WC.ComboBox box) => box.SelectedIndex > 0 && box.SelectedIndex <= forceChoices.Count ? forceChoices[box.SelectedIndex - 1] : 0;

    private void FillTable(GameTable table, List<GameTable.Row> rows, WC.TextBlock count, Func<object?, string> counted)
    {
        count.Text = snapshot is null ? L.Lists.ConnectToSee : counted(Count(rows.Count));
        table.SetRows(rows);
    }

    private List<GameTable.Row> ForceRows()
    {
        if (snapshot is not { } data) return [];
        string word = forceSearch.Text.Trim();
        var rows = new List<GameTable.Row>();
        foreach (ForceInfo info in data.Forces.Values.Where(f => f.RulerId is not (0 or 0xFFFF)))
        {
            string name = data.ForceName(info.Id), ruler = data.OfficerName(info.RulerId);
            if (word.Length > 0 && !name.Contains(word) && !ruler.Contains(word)) continue;
            int[] cities = data.CitiesOf(info.Id).ToArray();
            long troops = cities.Sum(id => data.Cities[id][CityFields.Troops]) + data.Units.Where(u => data.ForceOfUnit(u) == info.Id).Sum(u => u[UnitFields.Troops]);
            string rank = data.ForceRecords.TryGetValue(info.Id, out ForceRecord? record) ? GameCatalog.NameOf(catalog.Ranks, record[ForceFields.Rank]) : "";
            rows.Add(new GameTable.Row(info.Id, [new GameTable.Shield(name, MapView.ForceColor(info.Id)), ruler, rank, (long)cities.Length, (long)data.OfficersOf(info.Id).Count(), troops]));
        }
        return rows.OrderByDescending(r => (long)r.Cells[3]!).ThenBy(r => (int)r.Tag).ToList();
    }

    private List<GameTable.Row> CityRows()
    {
        if (snapshot is not { } data) return [];
        string word = citySearch.Text.Trim();
        int force = PickedForce(cityForceBox);
        var rows = new List<GameTable.Row>();
        foreach (int id in Enumerable.Range(1, GameMemory.CityCount))
        {
            CityRecord city = data.Cities[id];
            int owner = data.ForceOfCity(id);
            string name = CityNames.ById[id], governor = data.OfficerName(city.Governor), forceName = data.ForceName(owner);
            if (force != 0 && owner != force) continue;
            if (word.Length > 0 && !name.Contains(word) && !governor.Contains(word) && !forceName.Contains(word)) continue;
            rows.Add(new GameTable.Row(id, [name, new GameTable.Shield(forceName, MapView.ForceColor(owner)), CityFields.ScaleName(city[CityFields.Scale]), governor, city[CityFields.Troops], city[CityFields.Gold], city[CityFields.Food]]));
        }
        return rows;
    }

    private List<GameTable.Row> OfficerRows()
    {
        if (snapshot is not { } data) return [];
        string word = officerSearch.Text.Trim();
        int force = PickedForce(officerForceBox);
        IEnumerable<OfficerRecord> officers = data.Officers;
        if (officerStatusBox.SelectedIndex == 0) officers = officers.Where(o => OfficerFields.IsActive(o.Status));
        if (force != 0) officers = officers.Where(o => OfficerFields.IsMember(o.Status) && data.ForceOfCorps(o.Corps) == force);
        if (word.Length > 0) officers = officers.Where(o => Matches(o, word));
        return officers.OrderBy(o => o.Status).ThenByDescending(o => o[OfficerFields.Leadership] + o[OfficerFields.Strength] + o[OfficerFields.Intelligence])
            .Select(o => new GameTable.Row(o.Id, [o.Face, o.Name + "\n" + o.CourtesyName, ForceAndBase(data, o), OfficerFields.StatusName(o.Status),
                o[OfficerFields.Loyalty], o[OfficerFields.Leadership], o[OfficerFields.Strength], o[OfficerFields.Intelligence], o[OfficerFields.Politics],
                o[OfficerFields.Charm]]))
            .ToList();
    }

    // A search finds an officer by his name or 자, or by the name of one of his 개성 or 전법.
    private bool Matches(OfficerRecord officer, string word)
    {
        if (officer.Name.Contains(word, StringComparison.Ordinal) || officer.CourtesyName.Contains(word, StringComparison.Ordinal)) return true;
        bool Named(GameName[] names, long id) => id != 0 && GameCatalog.NameOf(names, id).Contains(word, StringComparison.Ordinal);
        return OfficerFields.Personalities.Any(f => Named(catalog.Personalities, officer[f]))
            || Named(catalog.Tactics, officer[OfficerFields.UniqueTactic])
            || OfficerFields.Tactics.Any(f => Named(catalog.Tactics, officer[f]));
    }

    // An officer's force over the base he belongs to, the two lines of one cell.
    private static string ForceAndBase(GameSnapshot data, OfficerRecord officer) =>
        data.ForceName(data.ForceOfCorps(officer.Corps)) + "\n" + CityNames.BaseName(officer.BaseId);

    private List<GameTable.Row> UnitRows()
    {
        if (snapshot is not { } data || layout?.UnitCount == 0) return [];
        string word = unitSearch.Text.Trim();
        int force = PickedForce(unitForceBox);
        IEnumerable<UnitRecord> units = data.Units;
        if (unitKindBox.SelectedIndex == 0) units = units.Where(u => u.IsArmy);
        if (force != 0) units = units.Where(u => data.ForceOfUnit(u) == force);
        if (word.Length > 0) units = units.Where(u => data.CommanderName(u.Commander).Contains(word, StringComparison.Ordinal));
        return units.OrderBy(data.ForceOfUnit).ThenBy(u => u.Kind).ThenByDescending(u => u[UnitFields.Troops])
            .Select(u => new GameTable.Row((u.Slot, u.Commander), [data.OfficerById.GetValueOrDefault(u.Commander)?.Face ?? -1, data.CommanderName(u.Commander), new GameTable.Shield(data.ForceName(data.ForceOfUnit(u)), MapView.ForceColor(data.ForceOfUnit(u))), UnitFields.KindName(u.Kind),
                data.PlaceName(u), u[UnitFields.Troops], u[UnitFields.Wounded], u[UnitFields.Morale]]))
            .ToList();
    }

    // 개성 by number, with what they do and how many officers in play have them.
    private List<GameTable.Row> PersonalityRows()
    {
        if (snapshot is not { } data) return [];
        string word = personalitySearch.Text.Trim();
        var holders = new Dictionary<long, int>();
        foreach (OfficerRecord officer in data.Officers.Where(o => OfficerFields.IsActive(o.Status)))
            foreach (FieldDef slot in OfficerFields.Personalities)
                if (officer[slot] != 0) holders[officer[slot]] = holders.GetValueOrDefault(officer[slot]) + 1;
        return data.Personalities.Where(p => word.Length == 0 || p.Name.Contains(word, StringComparison.Ordinal) || p.Description.Contains(word, StringComparison.Ordinal))
            .Select(p => new GameTable.Row(p.Index, [p.Name, p.Description, p.Kind(0) == 0 ? null : p.Value(0), p.Kind(1) == 0 ? null : p.Value(1), (long)holders.GetValueOrDefault(p.Index)]))
            .ToList();
    }

    // 명품 that are in play (found or still to be found), with what they add and who holds them.
    private List<GameTable.Row> ItemRows()
    {
        if (snapshot is not { } data) return [];
        string word = itemSearch.Text.Trim();
        return data.Items.Where(i => i.State is ItemFields.Found or ItemFields.Hidden)
            .Select(i => (Item: i, Owner: data.OfficerName(i.Owner)))
            .Where(i => word.Length == 0 || i.Item.Name.Contains(word, StringComparison.Ordinal) || i.Owner.Contains(word, StringComparison.Ordinal))
            .Select(i => new GameTable.Row(i.Item.Index, [i.Item.Name, i.Item.Ability == 0 ? "" : $"{ItemFields.AbilityName(i.Item.Ability)} +{i.Item.Bonus}", i.Owner, ItemState(i.Item)]))
            .ToList();
    }

    // 고대무장 by ID, each with whether he may appear now and what lets him; the count says how many wait still.
    private void FillAncients()
    {
        List<GameTable.Row> rows = AncientRows(out int locked);
        ancientTable.SetRows(rows);
        ancientCount.Text = snapshot is null ? L.Lists.ConnectToSee
            : snapshot.Play is null ? L.Ancient.NoPlayRecord
            : L.Lists.AncientCount(Count(rows.Count), Count(locked));
    }

    private List<GameTable.Row> AncientRows(out int locked)
    {
        locked = 0;
        if (snapshot is not { Play: { } play } data) return [];
        string word = ancientSearch.Text.Trim();
        var rows = new List<GameTable.Row>();
        foreach (OfficerRecord officer in AncientOfficers.In(data))
        {
            AncientOfficers.Access access = AncientOfficers.AccessOf(officer.Id, play.Unlocks, data.DlcOfficers);
            if (access == AncientOfficers.Access.Locked) locked++;
            string condition = AncientOfficers.Condition(officer.Id);
            if (ancientLockBox.SelectedIndex == 1 && access is not (AncientOfficers.Access.Locked or AncientOfficers.Access.DlcMissing)) continue;
            if (word.Length > 0 && !officer.Name.Contains(word, StringComparison.Ordinal) && !officer.CourtesyName.Contains(word, StringComparison.Ordinal)
                && !condition.Contains(word, StringComparison.Ordinal)) continue;
            rows.Add(new GameTable.Row(officer.Id, [officer.Face, officer.Name + "\n" + officer.CourtesyName, AncientOfficers.AccessName(access), condition]));
        }
        return rows;
    }

    // [모두 해금]: every 고대무장 who still waits for his bit, after asking. Undone as any write. The bits stay in the
    // game's memory until it saves, so the player is told to save once it is written.
    private async Task UnlockAncientsAsync()
    {
        if (busy || snapshot is not { } data) return;
        EditPlan plan = AncientOfficers.Unlock(data, Enumerable.Range(AncientOfficers.First, AncientOfficers.Last - AncientOfficers.First + 1));
        if (plan.Refused)
        {
            ReportError(new InvalidOperationException(plan.Refusal));
            return;
        }
        if (plan.Edits.Count == 0)
        {
            status.Text = plan.Summary;
            return;
        }
        if (!ConfirmWrite(plan.Summary)) return;
        AddLog(plan.Summary);
        if (!await WriteAsync([.. plan.Edits])) return;
        AfterWrite();
        Tell(L.Ancient.SaveInGame);
    }

    // ---- A row picked ---------------------------------------------------------------------------------

    private async Task OpenPickedAsync(GameTable table, object tag)
    {
        if (snapshot is not { } data) return;
        switch (tag)
        {
            case int force when table == forceTable:
                await OpenAsync(new ForceTarget(force));
                break;
            case int city when table == cityTable:
                if (await OpenAsync(new CityTarget(city))) map.ShowCity(city);
                break;
            case int officer when table == officerTable:
                if (await OpenAsync(new OfficerTarget(officer)) && data.OfficerById.TryGetValue(officer, out OfficerRecord? record)) GoToOfficer(record);
                break;
            case (int slot, int commander):
                if (await OpenAsync(new UnitTarget(slot, commander))) map.ShowUnit((slot, commander));
                break;
            case int personality when table == personalityTable:
                await OpenAsync(new PersonalityTarget(personality));
                break;
            case int item when table == itemTable:
                await OpenAsync(new ItemTarget(item));
                break;
            case int ancient when table == ancientTable:
                await OpenAsync(new OfficerTarget(ancient));
                break;
            case int formation when table == formationTable:
                await OpenAsync(new FormationTarget(formation));
                break;
            case int tactic when table == tacticTable:
                await OpenAsync(new TacticTarget(tactic));
                break;
            case int policy when table == policyTable:
                await OpenAsync(new PolicyTarget("policy", policy));
                break;
            case int effect when table == policyEffectTable:
                await OpenAsync(new PolicyTarget("policyeffect", effect));
                break;
            case int doctrine when table == doctrineTable:
                await OpenAsync(new PlainRuleTarget("doctrine",doctrine)); break;
            case int rank when table == rankTable:
                await OpenAsync(new PlainRuleTarget("rank",rank)); break;
            case int office when table == officeTable:
                await OpenAsync(new OfficeTarget(office));
                break;
        }
    }

    // An officer on the map: the unit he leads, or else his base.
    private void GoToOfficer(OfficerRecord officer)
    {
        if (snapshot is not { } data) return;
        if (data.Units.FirstOrDefault(u => u.Commander == officer.Id) is { } unit)
        {
            map.SelectedUnit = (unit.Slot, unit.Commander);
            map.ShowUnit((unit.Slot, unit.Commander));
        }
        else if (data.BaseTile(officer.BaseId) is { } tile) map.ShowTile(tile);
    }

    // A unit clicked on the map: its card opens, and in the 부대 list it is picked (the list widened when its
    // filters hide it).
    private async Task OpenUnitFromMapAsync(int slot, int commander)
    {
        if (!await OpenAsync(new UnitTarget(slot, commander))) return;
        if (lists.Tab != UnitTab) return;
        EnsureList(UnitTab);
        if (!unitTable.Contains((slot, commander)) && snapshot?.Unit(slot, commander) is { } unit)
        {
            unitSearch.Text = "";
            if (!unit.IsArmy) unitKindBox.SelectedIndex = 1;
            unitForceBox.SelectedIndex = 0;
            FillLists();
            EnsureList(UnitTab);
        }
        unitTable.Select((slot, commander));
    }

    // ---- Portraits ------------------------------------------------------------------------------------

    // Indexes the game's portrait resources in the background (read-only, cached per game version).
    private void StartFaceLibrary(string? exePath)
    {
        if (faces is not null || faceLoading) return;
        string? directory = FaceLibrary.FindGameDirectory(exePath);
        if (directory is null) return;
        faceLoading = true;
        int size = iconSize = Px(OfficerRowHeight - 2);
        Task.Run(() =>
        {
            FaceLibrary library = FaceLibrary.Load(directory);
            return (Library: library, Icons: IconCache.Load(library.Stamp, size));
        }).ContinueWith(task => Dispatcher.InvokeAsync(() =>
        {
            faceLoading = false;
            if (task.IsCompletedSuccessfully)
            {
                faces = task.Result.Library;
                foreach ((int face, IconCache.Icon icon) in task.Result.Icons) faceIcons.TryAdd(face, icon);
                map.RefreshFaces();
                AddLog(L.Lists.ArtFound(faces.Count));
                FaceLibrary art = faces;
                Task.Run(() =>
                {
                    GameSkin.Load(art);
                    MapArt.Load(art);
                });
                LoadOfficerIcons();
                LoadPortrait();
            }
            else AddLog(L.Lists.PortraitsFailed(task.Exception?.GetBaseException().Message));
        }));
    }

    // Portraits for the tables and the unit tokens. Icons prepared on an earlier run are read at start-up; missing
    // ones are decoded on a few background threads (the rest of the CPU is left for the game) and saved.
    private void LoadOfficerIcons()
    {
        iconLoad?.Cancel();
        if (faces is null || snapshot is null) return;
        map.RefreshFaces();
        // Listed officers first, then every other officer in the game, so later lists are ready at once.
        List<int> needed = officerTable.Rows.Select(r => r.Cells[0]).OfType<int>()
            .Concat(snapshot.Officers.Select(o => o.Face))
            .Where(f => f >= 0 && !faceIcons.ContainsKey(f)).Distinct().ToList();
        if (needed.Count == 0) return;
        var cancel = iconLoad = new CancellationTokenSource();
        FaceLibrary library = faces;
        int size = iconSize;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 6), CancellationToken = cancel.Token };
        iconTask = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(needed, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering), options,
                    face => decodedIcons.Enqueue((face, new IconCache.Icon(library.IconPixels(face, size), size))));
            }
            catch (OperationCanceledException) { }
        });
        iconTimer.Start();
    }

    // Decoded icons are applied in batches so the lists repaint a few times instead of once per face.
    private void ApplyDecodedIcons()
    {
        bool decoded = false;
        while (decodedIcons.TryDequeue(out var item))
        {
            decoded = true;
            if (faceIcons.TryAdd(item.Face, item.Icon)) iconCacheDirty |= item.Icon.Pixels is not null && item.Face < FaceLibrary.CustomFaceBase;
        }
        if (decoded)
        {
            map.RefreshFaces();
            return;
        }
        if (iconTask is { IsCompleted: false }) return;
        iconTimer.Stop();
        if (iconCacheDirty && faces is not null)
        {
            iconCacheDirty = false;
            string stamp = faces.Stamp;
            int size = iconSize;
            List<(int, byte[]?)> icons = faceIcons.Select(pair => (pair.Key, pair.Value.Pixels)).ToList();
            Task.Run(() => IconCache.Save(stamp, size, icons));
        }
    }
}

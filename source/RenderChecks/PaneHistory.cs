using System.Collections;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;

internal static partial class RenderChecks
{
    private static void CheckPaneHistory(string output)
    {
        Directory.CreateDirectory(output);
        using var journal = new StreamWriter(Path.Combine(output, "pane-history.jsonl")) { AutoFlush = true };
        static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Hidden)!.GetValue(o)!;
        static void Set(object o, string name, object? value) => o.GetType().GetField(name, Hidden)!.SetValue(o, value);
        static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, Hidden)!.Invoke(o, args);
        void Record(string scenario, bool passed, object details)
        {
            string row = JsonSerializer.Serialize(new { scenario, passed, details, gameAccess = false, browserUsed = false });
            journal.WriteLine(row); Console.WriteLine(row);
            if (!passed) throw new InvalidOperationException("Pane history check failed: " + scenario);
        }
        var doctrines=new PlainRuleFixture(DoctrineRules.Spec); var ranks=doctrines.Ranks!; var offices = ranks.Offices; var policies = offices.Policies; var fixture = policies.Tactics;
        string storage = Path.Combine(output, "storage-" + Guid.NewGuid().ToString("N"));
        var access = new RuleGameAccess(g => FormationMemory.Read(fixture, g), (_, _) => GamePace.Paused,
            (g, s, c) => FormationMemory.Apply(fixture, g, s, c), g => TacticMemory.Read(fixture, g),
            (g, s, c) => TacticMemory.Apply(fixture, g, s, c));
        var window = new EditorWindow(() => false, Path.Combine(storage, "seasons"), Path.Combine(storage, "ai"), storage, access);
        var map = Field<MapView>(window, "map");
        try
        {
            var data = new TestGame().Force(1, 11, 1).City(1, 1, 11).City(2, 1)
                .Officer(11, "A", 1, 1, 1).Officer(12, "B", 1, 1, 4).Officer(2001, "Ancient", 0, 1, 4)
                .Leading(11).Item(1, "Item", 11).Personality(1, "Personality", "Effect", (1, 10)).Gate(52, 1)
                .Facility(1, 3, 2000, 1, 500).Play(0).Build();
            Set(window, "snapshot", data);
            var formation = fixture.Formations.Snapshot(); var tactic = fixture.Snapshot();
            var profile = formation.Baseline(fixture.Identity.ExeHash).AddTactics(tactic).AddPolicies(policies.Snapshot()).AddOffices(offices.Snapshot()).AddPlain(ranks.Snapshot()).AddPlain(doctrines.Snapshot());
            Set(window, "ruleSnapshot", formation); Set(window, "tacticRuleSnapshot", tactic);
            Set(window, "policyRuleSnapshot", policies.Snapshot());
            Set(window, "officeRuleSnapshot", offices.Snapshot());
            Set(window,"rankRuleSnapshot",ranks.Snapshot());
            Set(window,"doctrineRuleSnapshot",doctrines.Snapshot());
            Set(window, "ruleKept", new RuleProfileRead(profile, "fixture"));
            Call(window, "FillLists");
            void List(ListKind kind)
            {
                map.ShowSection(kind is ListKind.Personalities or ListKind.Items or ListKind.Formations or ListKind.Tactics or ListKind.Policies or ListKind.PolicyEffects or ListKind.Offices or ListKind.Ranks or ListKind.Doctrines ? PaneSection.Data
                    : kind == ListKind.Ancients ? PaneSection.Ancients : PaneSection.Game);
                map.Lists.ShowTab((int)kind);
            }
            void Open(EditorTarget target) => ((Task<bool>)Call(window, "OpenAsync", target, true)!).GetAwaiter().GetResult();
            EditorCard Card() => (EditorCard)typeof(EditorWindow).GetProperty("OpenEditor", Hidden)!.GetValue(window)!;
            WC.TextBlock Mark(string name) => (WC.TextBlock)typeof(EditorCard).GetField(name, Hidden)!.GetValue(Card())!;
            void History(bool forward)
            {
                if (!Mark(forward ? "forward" : "back").IsEnabled) throw new InvalidOperationException("History control was disabled");
                ((Action)typeof(EditorCard).GetField(forward ? "ForwardClicked" : "BackClicked", Hidden)!.GetValue(Card())!).Invoke();
            }
            void Back() => History(false);
            void Resume() => ((Action)typeof(PlateButton).GetField("Click", Hidden)!.GetValue(Field<PlateButton>(map.Lists, "resume"))!).Invoke();
            void Link(EditorTarget target) => ((Action<EditorTarget>)typeof(EditorCard).GetField("LinkClicked", Hidden)!.GetValue(Card())!).Invoke(target);
            IList Visits() => Field<IList>(window, "history");
            bool AtList(ListKind kind) => map.Lists.Shown && !map.EditorShown && map.Lists.Tab == (int)kind && map.Section == map.Lists.Section;
            List(ListKind.Cities); Open(new CityTarget(1));
            List(ListKind.Tactics); Open(new TacticTarget(1)); Back();
            Record("city-then-tactic-back-returns-tactic-list", map.Lists.Shown && map.Lists.Tab == (int)ListKind.Tactics && map.Section == PaneSection.Data,
                new { map.EditorShown, map.Lists.Shown, tab = map.Lists.Tab, section = map.Section.ToString(), historyAt = Field<int>(window, "historyAt") });

            Resume();
            Record("resume-after-back-restores-history-position", map.EditorShown && Card().Target == new TacticTarget(1) && Mark("back").IsEnabled,
                new { historyAt = Field<int>(window, "historyAt") });
            Back();
            Record("resumed-detail-still-returns-to-list", AtList(ListKind.Tactics), new { map.Lists.Tab });

            (ListKind List, EditorTarget Target)[] routes =
            [
                (ListKind.Forces, new ForceTarget(1)), (ListKind.Cities, new CityTarget(1)),
                (ListKind.Officers, new OfficerTarget(11)), (ListKind.Units, new UnitTarget(data.Units[0].Slot, 11)),
                (ListKind.Personalities, new PersonalityTarget(1)), (ListKind.Items, new ItemTarget(1)),
                (ListKind.Ancients, new OfficerTarget(2001)), (ListKind.Formations, new FormationTarget(1)),
                (ListKind.Tactics, new TacticTarget(1)), (ListKind.Policies, new PolicyTarget("policy", 1)), (ListKind.PolicyEffects, new PolicyTarget("policyeffect", 1)),
                (ListKind.Offices, new OfficeTarget(1)), (ListKind.Ranks,new PlainRuleTarget("rank",1)), (ListKind.Doctrines,new PlainRuleTarget("doctrine",1))
            ];
            foreach (var route in routes)
            {
                List(route.List); Open(route.Target); Back();
                Record("list-detail-back-" + route.List, AtList(route.List), new { map.Lists.Tab, section = map.Section.ToString() });
            }

            List(ListKind.Items); Open(new ItemTarget(1)); Link(new OfficerTarget(11)); Back();
            Record("cross-section-link-back-restores-detail", map.EditorShown && Card().Target == new ItemTarget(1) && map.Section == PaneSection.Data,
                new { target = Card().Target?.ToString() });
            History(true);
            Record("cross-section-link-forward-restores-detail", Card().Target == new OfficerTarget(11) && map.Section == PaneSection.Game,
                new { target = Card().Target?.ToString() });
            Back(); Back();
            Record("linked-detail-origin-list-is-retained", AtList(ListKind.Items), new { map.Lists.Tab });

            List(ListKind.Forces); Open(new ForceTarget(1)); Link(new GateTarget(52)); Back();
            Record("gate-link-back-restores-force", Card().Target == new ForceTarget(1), new { target = Card().Target?.ToString() });
            Link(new GateTarget(52)); map.ReturnToList();
            Record("gate-parent-list-is-cities", AtList(ListKind.Cities), new { map.Lists.Tab });
            List(ListKind.Cities); Open(FacilityTarget.Of(data.Facilities.Single())); map.ReturnToList();
            Record("facility-parent-list-is-cities", AtList(ListKind.Cities), new { map.Lists.Tab });

            List(ListKind.Ancients); Open(new OfficerTarget(2001)); Back();
            List(ListKind.Officers); Open(new OfficerTarget(2001)); Back();
            Record("same-officer-different-list-origin", AtList(ListKind.Officers), new { map.Lists.Tab });

            List(ListKind.Tactics); Open(new TacticTarget(1));
            var draft = Field<Dictionary<TacticField, NumberField>>(map.TacticEditor, "fields")[TacticRules.Field("amount1")];
            draft.Set(11); Back(); Resume();
            Record("back-and-resume-preserve-draft", draft.Value == 11 && map.TacticEditor.Pending == 1 && fixture.Writes == 0,
                new { map.TacticEditor.Pending, fixture.Writes });
            Back();
            bool escaped = (bool)Call(window, "Escape")!;
            bool hidden = !map.PaneShown;
            Call(window, "TogglePanel");
            Record("escape-list-folds-pane-and-preserves-draft", escaped && hidden && AtList(ListKind.Tactics) && draft.Value == 11 && map.TacticEditor.Pending == 1,
                new { escaped, hidden, map.Lists.Tab, map.TacticEditor.Pending });
            Resume(); map.TacticEditor.Open(new(tactic.Record(1), profile));
            Call(window, "Escape");
            Record("escape-detail-returns-to-own-list", AtList(ListKind.Tactics) && Field<PlateButton>(map.Lists, "resume").Visibility == W.Visibility.Collapsed,
                new { map.Lists.Tab });

            var search = Field<SearchBox>(window, "tacticSearch"); search.Text = "T1"; Call(window, "FillLists");
            var table = Field<GameTable>(window, "tacticTable"); table.Select(1); Call(table, "SortBy", 0);
            Open(new TacticTarget(1)); Back();
            Record("list-return-keeps-search-sort-and-selection", AtList(ListKind.Tactics) && search.Text == "T1" && Field<int>(table, "sortColumn") == 0
                && Field<WC.ListBox>(table, "list").SelectedItem is GameTable.Row picked && Equals(picked.Tag, 1), new { search.Text, map.Lists.Tab });
            Resume(); int count = Visits().Count, at = Field<int>(window, "historyAt");
            for (int i = 0; i < 3; ++i) Call(window, "FillLists");
            Record("refresh-does-not-create-history", Visits().Count == count && Field<int>(window, "historyAt") == at && Card().Target == new TacticTarget(1), new { count, at });

            List(ListKind.Cities); Open(new CityTarget(1)); Link(new OfficerTarget(11)); Back(); Link(new CityTarget(2));
            Record("new-link-discards-forward-branch", Card().Target == new CityTarget(2) && !Mark("forward").IsEnabled,
                new { target = Card().Target?.ToString() });

            List(ListKind.Ancients); Open(new OfficerTarget(2001)); Link(new CityTarget(1)); Back(); Back();
            Record("ancient-origin-survives-linked-detail-history", AtList(ListKind.Ancients), new { map.Lists.Tab });

            List(ListKind.Units); Open(new UnitTarget(data.Units[0].Slot, 11)); Link(new CityTarget(1));
            Set(window, "snapshot", new TestGame().Force(1, 11, 1).City(1, 1, 11).Officer(11, "A", 1, 1, 1).Play(0).Build());
            Back();
            Record("removed-unit-history-skips-to-list", AtList(ListKind.Units), new { map.Lists.Tab });
            Set(window, "snapshot", data);

            Visits().Clear(); Set(window, "historyAt", -1); map.HideEditors();
            List(ListKind.Formations); Open(new FormationTarget(1)); Back();
            Record("first-detail-has-its-list-as-back-target", AtList(ListKind.Formations), new { count = Visits().Count });
            List(ListKind.Cities);
            for (int i = 0; i < 70; ++i) Open(new CityTarget(i % 2 + 1));
            int maximum = Visits().Count;
            while (Mark("back").IsEnabled) Back();
            Record("history-limit-and-first-position", maximum == 50 && Field<int>(window, "historyAt") == 0 && !Mark("back").IsEnabled,
                new { maximum, historyAt = Field<int>(window, "historyAt") });
        }
        finally { map.HideEditors(); window.Close(); }
    }
}

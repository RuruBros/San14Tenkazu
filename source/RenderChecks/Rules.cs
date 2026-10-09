using System.IO;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;

internal static partial class RenderChecks
{
    // Read-only live verification. The result is an explicit evidence file, never a profile/default capture.
    private static void ReadRuleGame(string output)
    {
        GameIdentity game = GameMemory.FindGame();
        FormationSnapshot first = GameMemory.ReadFormations(game);
        TacticSnapshot firstTactics = GameMemory.ReadTactics(game);
        PolicySnapshot firstPolicies = GameMemory.ReadPolicies(game);
        OfficeSnapshot firstOffices = GameMemory.ReadOffices(game);
        PlainRuleSnapshot firstRanks=GameMemory.ReadRanks(game),firstDoctrines=GameMemory.ReadDoctrines(game);
        GameLayout layout = GameMemory.ReadCurrentLayout(game);
        GamePace pace = GameMemory.ReadPace(game, layout with { State = first.State });
        FormationSnapshot second = GameMemory.ReadFormations(game);
        TacticSnapshot secondTactics = GameMemory.ReadTactics(game);
        PolicySnapshot secondPolicies = GameMemory.ReadPolicies(game);
        OfficeSnapshot secondOffices = GameMemory.ReadOffices(game);
        PlainRuleSnapshot secondRanks=GameMemory.ReadRanks(game),secondDoctrines=GameMemory.ReadDoctrines(game);
        RuleProfile baseline = second.Baseline(game.ExeHash).AddTactics(secondTactics).AddPolicies(secondPolicies).AddOffices(secondOffices).AddPlain(secondRanks).AddPlain(secondDoctrines);
        bool stable = first.Root == second.Root && first.Array == second.Array && first.State == second.State
            && first.Values.OrderBy(v => v.Key.Record).ThenBy(v => v.Key.Field).SequenceEqual(second.Values.OrderBy(v => v.Key.Record).ThenBy(v => v.Key.Field))
            && new RuleReadState(first, firstTactics, firstPolicies, firstOffices,firstRanks,firstDoctrines).SameValues(new(second, secondTactics, secondPolicies, secondOffices,secondRanks,secondDoctrines));
        var result = new { utc = DateTime.UtcNow, game.ProcessId, game.Verified, game.ExeHash, pace = pace.ToString(), stable,
            baseline.Schema, baseline.Catalog, currentLayoutChecked = true, memoryWritten = false, profileSaved = false,
            records = second.Records.Select(r => new { r.Id, r.Name, address = $"0x{r.Address:X}", vector = $"0x{r.VectorAddress:X}", vectorBytes = r.Vector.Length,
                r.MirrorsAgree, fields = FormationRules.Fields.Select(f => new { f.Id, f.Label, plain = (int)r.Data[f.Plain], decoded = r.Value(f),
                    effective = FormationRules.Effective(r.Value(f), f, r.Vector.Length == 32), f.Maximum }) }),
            tactics = secondTactics.Records.Select(r => new { r.Id, r.Name, address = $"0x{r.Address:X}", vector = $"0x{r.VectorAddress:X}", r.MirrorsAgree,
                fields = TacticRules.Fields.Select(f => new { f.Id, plain = (int)r.Data[f.Plain], decoded = r.Value(f), f.Maximum }) }), tacticCatalog = secondTactics.Catalog,
            policies = secondPolicies.Records.Select(r => new { r.Table, r.Id, r.Name, r.MirrorsAgree, address = $"0x{r.Address:X}", vector = $"0x{r.VectorAddress:X}",
                fields = PolicyRules.Fields(r.Table).Select(f => new { f.Id, value = r.Value(f), f.Maximum }) }), policyCatalogs = secondPolicies.Catalogs,
            offices = secondOffices.Records.Select(r => new { r.Id, r.Name, r.MirrorsAgree, address = $"0x{r.Address:X}", vector = $"0x{r.VectorAddress:X}",
                fields = OfficeRules.Fields.Select(f => new { f.Id, plain = r.Plain(f), value = r.Value(f), f.Minimum, f.Maximum }) }), officeCatalog = secondOffices.Catalog,
            ranks=secondRanks.Records.Select(r => new {r.Id,r.Name,address=$"0x{r.Address:X}",fields=RankRules.Spec.Fields.Select(f => new {f.Id,value=r.Value(f),f.Minimum,f.Maximum})}),rankCatalog=secondRanks.Catalog,
            doctrines=secondDoctrines.Records.Select(r => new {r.Id,r.Name,address=$"0x{r.Address:X}",fields=DoctrineRules.Spec.Fields.Select(f => new {f.Id,value=r.Value(f),f.Minimum,f.Maximum})}),doctrineCatalog=secondDoctrines.Catalog };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { game.ProcessId, game.Verified, pace = pace.ToString(), stable, records = second.Records.Length, fields = baseline.Defaults.Length, output = Path.GetFullPath(output) }));
        if (!stable || !game.Verified || pace != GamePace.Paused) throw new InvalidOperationException("Live formation read was not verified/stable/paused");
    }

    private static void CheckRulePane(string output)
    {
        Directory.CreateDirectory(output);
        static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Hidden)!.GetValue(obj)!;
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var fixture = new FormationFixture(); FormationSnapshot snapshot = fixture.Snapshot();
        RuleProfile profile = snapshot.Baseline(fixture.Identity.ExeHash);
        var window = new EditorWindow(() => false, Path.Combine(output, "seasons"), Path.Combine(output, "ai"), output);
        typeof(EditorWindow).GetField("snapshot", Hidden)!.SetValue(window, new TestGame().Build());
        typeof(EditorWindow).GetField("ruleSnapshot", Hidden)!.SetValue(window, snapshot);
        typeof(EditorWindow).GetField("ruleKept", Hidden)!.SetValue(window, new RuleProfileRead(profile, "fixture"));
        typeof(EditorWindow).GetMethod("FillLists", Hidden)!.Invoke(window, null);
        var map = Field<MapView>(window, "map");
        map.Lists.ShowTab((int)ListKind.Formations);
        Require(map.Lists.Section == PaneSection.Data, "Formation list must belong to Data");
        Require(typeof(EditorWindow).GetMethod("Show", Hidden)!.Invoke(window, [new FormationTarget(1)]) is true, "Formation target failed to open");
        FormationEditorCard card = map.FormationEditor;
        var fields = Field<Dictionary<FormationField, NumberField>>(card, "fields");
        fields[FormationRules.Fields[0]].Set(25);
        Require(card.Pending == 1 && card.Changes(out _)!.Single().After == 25, "Formation input did not produce a semantic change");
        card.Follow(new(snapshot.Record(1), profile));
        Require(card.Pending == 1 && fields[FormationRules.Fields[0]].Value == 25, "Refresh discarded pending input");
        map.ReturnToList(); map.ResumeEditor();
        Require(map.Section == PaneSection.Data && card.Pending == 1, "List navigation discarded input or changed task");
        Require(Field<WC.TextBlock>(map, "editorLocation").Text.StartsWith(L.Rules.Formation, StringComparison.Ordinal), "Formation breadcrumb category is incorrect");
        fields[FormationRules.Fields[0]].Box.Text = "31";
        Require(card.Changes(out string error) is null && error.Length > 0, "Native mobility cap was not enforced");
        card.Open(new(snapshot.Record(1), profile));
        Require(card.Pending == 0, "Open did not reset pending input");
        // Render at supported pane widths with real WPF controls. Pending/input checks above do not require a window.
        foreach (double width in new[] { 500.0, 600.0, 850.0 })
        {
            typeof(MapView).GetField("paneWidth", Hidden)!.SetValue(map, width);
            map.Measure(new W.Size(width + 306, 800)); map.Arrange(new W.Rect(0, 0, width + 306, 800)); map.UpdateLayout();
            Require(card.ActualWidth > 0 && fields.Values.All(f => f.Box.ActualWidth == 80), "Formation fields were clipped or failed to lay out");
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(width + 306), 800, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(map);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"formation-{width}.png")); png.Save(file);
        }
        File.WriteAllText(Path.Combine(output, "rules-pane.json"), JsonSerializer.Serialize(new { passed = true, fields = fields.Count, widths = new[] { 500, 600, 850 }, browserUsed = false, gameAccess = false }));
        Console.WriteLine("Formation WPF navigation, pending input, range validation and rendering passed");
        CheckRulePolling(output);
        CheckTacticRules(output);
    }
}

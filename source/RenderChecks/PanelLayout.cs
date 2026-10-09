using System.IO;
using System.Reflection;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WP = System.Windows.Controls.Primitives;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

internal static partial class RenderChecks
{
    // Render every pane page with fixture records. No window, browser or live game access.
    private static void CheckPanelLayout(string output, bool requireFluid)
    {
        Directory.CreateDirectory(output);
        static T Field<T>(object obj, string name, Type? type = null) =>
            (T)(type ?? obj.GetType()).GetField(name, Hidden)!.GetValue(obj)!;
        static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Hidden)!.Invoke(obj, args);
        static void Layout(W.FrameworkElement root, double width, double height)
        {
            root.Measure(new W.Size(width, height));
            root.Arrange(new W.Rect(0, 0, width, height));
            root.UpdateLayout();
        }
        static IEnumerable<W.FrameworkElement> Descendants(W.DependencyObject root)
        {
            for (int i = 0; i < WM.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                W.DependencyObject child = WM.VisualTreeHelper.GetChild(root, i);
                if (child is W.FrameworkElement element) yield return element;
                foreach (W.FrameworkElement nested in Descendants(child)) yield return nested;
            }
        }
        static void Image(W.FrameworkElement root, W.FrameworkElement pane, string path)
        {
            var bitmap = new WMI.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * 1.5),
                (int)Math.Ceiling(root.ActualHeight * 1.5), 144, 144, WM.PixelFormats.Pbgra32);
            bitmap.Render(root);
            W.Point origin = pane.TranslatePoint(new W.Point(), root);
            var crop = new WMI.CroppedBitmap(bitmap, new W.Int32Rect((int)Math.Round(origin.X * 1.5),
                (int)Math.Round(origin.Y * 1.5), (int)Math.Round(pane.ActualWidth * 1.5), (int)Math.Round(pane.ActualHeight * 1.5)));
            var encoder = new WMI.PngBitmapEncoder();
            encoder.Frames.Add(WMI.BitmapFrame.Create(crop));
            using var file = File.Create(path);
            encoder.Save(file);
        }
        var game = new TestGame().Force(1, 1, 1).Force(2, 4, 2).City(1, 1, 2).Castle(1, 6000).Gate(52, 1, 2)
            .Officer(1, "조조", 1, 1, 1).Officer(2, "순욱", 1, 1, 3).Officer(3, "하후돈", 1, 1, 4)
            .Officer(4, "유비", 2, 2, 1).Leading(3).Item(1, "의천검", 1, ability: 2, bonus: 10)
            .Personality(1, "신장", "아군 부대의 전투 능력을 높입니다. 조건과 효과를 확인한 뒤 값을 변경합니다.", (5, 20))
            .With(2, OfficerFields.Leadership, 88).With(2, OfficerFields.Intelligence, 97)
            .With(2, OfficerFields.Strength, 30).With(2, OfficerFields.Politics, 95).With(2, OfficerFields.Charm, 92)
            .With(2, OfficerFields.Birth, 163).With(2, OfficerFields.Debut, 183).With(2, OfficerFields.Death, 212).Play(0);
        GameSnapshot data = game.Build();
        var window = new EditorWindow();
        typeof(EditorWindow).GetField("snapshot", Hidden)!.SetValue(window, data);
        var doctrineFixture=new PlainRuleFixture(DoctrineRules.Spec); var rankFixture=doctrineFixture.Ranks!; var officeFixture = rankFixture.Offices; var policyFixture = officeFixture.Policies; var tacticFixture = policyFixture.Tactics;
        var formationFixture = tacticFixture.Formations;
        FormationSnapshot formations = formationFixture.Snapshot();
        typeof(EditorWindow).GetField("ruleSnapshot", Hidden)!.SetValue(window, formations);
        typeof(EditorWindow).GetField("ruleKept", Hidden)!.SetValue(window, new RuleProfileRead(formations.Baseline(formationFixture.Identity.ExeHash).AddTactics(tacticFixture.Snapshot()).AddPolicies(policyFixture.Snapshot()).AddOffices(officeFixture.Snapshot()).AddPlain(rankFixture.Snapshot()).AddPlain(doctrineFixture.Snapshot()), "fixture"));
        typeof(EditorWindow).GetField("tacticRuleSnapshot", Hidden)!.SetValue(window, tacticFixture.Snapshot());
        typeof(EditorWindow).GetField("policyRuleSnapshot", Hidden)!.SetValue(window, policyFixture.Snapshot());
        typeof(EditorWindow).GetField("officeRuleSnapshot", Hidden)!.SetValue(window, officeFixture.Snapshot());
        typeof(EditorWindow).GetField("rankRuleSnapshot",Hidden)!.SetValue(window,rankFixture.Snapshot());
        typeof(EditorWindow).GetField("doctrineRuleSnapshot",Hidden)!.SetValue(window,doctrineFixture.Snapshot());
        Call(window, "FillLists");
        var map = Field<MapView>(window, "map");
        var pane = Field<WC.Grid>(map, "pane");
        var records = new List<object>();
        int failures = 0, cases = 0;
        void Check(string name, W.FrameworkElement root, double width, double height, EditorCard? editor = null)
        {
            Layout(map, width + 306, height);
            var clipped = new List<string>();
            foreach (W.FrameworkElement control in Descendants(root))
            {
                if (control is not (WC.TextBox or WC.ComboBox or WC.Button or PlateButton) || control.ActualWidth <= 0) continue;
                bool visible = true;
                for (W.DependencyObject? parent = control; parent is not null; parent = WM.VisualTreeHelper.GetParent(parent))
                    if (parent is W.UIElement { Visibility: not W.Visibility.Visible }) { visible = false; break; }
                if (!visible) continue;
                // The scrollable body may be outside its viewport; chrome and its actions must stay reachable.
                bool scrolls = false;
                for (W.DependencyObject? parent = WM.VisualTreeHelper.GetParent(control); parent is not null; parent = WM.VisualTreeHelper.GetParent(parent))
                    if (parent is WC.ScrollViewer) { scrolls = true; break; }
                if (!scrolls)
                {
                    W.Rect bounds = control.TransformToAncestor(root).TransformBounds(new W.Rect(control.RenderSize));
                    if (bounds.Top < -0.75 || bounds.Bottom > root.ActualHeight + 0.75)
                        clipped.Add($"{control.GetType().Name}: fixed action {bounds} outside pane height {root.ActualHeight}");
                }
                for (W.DependencyObject? parent = WM.VisualTreeHelper.GetParent(control); parent is not null; parent = WM.VisualTreeHelper.GetParent(parent))
                {
                    if (parent is not W.FrameworkElement element) continue;
                    WM.Geometry? clip = WP.LayoutInformation.GetLayoutClip(element);
                    if (clip is null && !element.ClipToBounds) continue;
                    W.Rect boundary = clip?.Bounds ?? new W.Rect(element.RenderSize);
                    W.Rect bounds = control.TransformToAncestor(element).TransformBounds(new W.Rect(control.RenderSize));
                    if (bounds.Left < boundary.Left - 0.75 || bounds.Right > boundary.Right + 0.75)
                    {
                        clipped.Add($"{control.GetType().Name}: {bounds} outside {element.GetType().Name} {boundary}");
                        break;
                    }
                }
            }
            double? pageWidth = null, viewport = null;
            if (editor is not null)
            {
                pageWidth = Field<WC.Grid>(editor, "pages", typeof(EditorCard)).ActualWidth;
                viewport = Field<WC.ScrollViewer>(editor, "scroll", typeof(EditorCard)).ViewportWidth;
            }
            bool fluid = pageWidth is null || Math.Abs(pageWidth.Value - viewport!.Value) < 3;
            WC.ScrollViewer? body = editor is not null ? Field<WC.ScrollViewer>(editor, "scroll", typeof(EditorCard))
                : name.StartsWith("AI-") ? Field<WC.ScrollViewer>(Field<AiView>(map, "ai"), "guidePage") : null;
            PlateButton? action = editor is not null ? Field<PlateButton>(editor, "apply", typeof(EditorCard))
                : name.StartsWith("AI-") ? Field<PlateButton>(Field<AiView>(map, "ai"), "apply") : null;
            bool footerPinned = true;
            if (body is not null && action is not null)
            {
                W.Point before = action.TranslatePoint(new W.Point(), root);
                body.ScrollToEnd();
                Layout(map, width + 306, height);
                W.Point after = action.TranslatePoint(new W.Point(), root);
                footerPinned = Math.Abs(before.Y - after.Y) < 0.75;
                body.ScrollToTop();
                Layout(map, width + 306, height);
            }
            bool passed = clipped.Count == 0 && fluid && footerPinned;
            if (!passed) failures++;
            cases++;
            records.Add(new { name, width, height, paneWidth = pane.ActualWidth, pageWidth, viewport, fluid, footerPinned, clipped, passed });
            if (width is 500 or 850 && height == 800) Image(map, root, Path.Combine(output, $"{name}-{width}.png"));
        }
        EditorTarget[] targets = [new ForceTarget(1), new CityTarget(1), new GateTarget(52), new OfficerTarget(2),
            new UnitTarget(0, 3), new PersonalityTarget(1), new ItemTarget(1), new FormationTarget(1), new TacticTarget(1),
            new PolicyTarget("policy", 1), new PolicyTarget("policyeffect", 1), new OfficeTarget(1), new PlainRuleTarget("rank",1),new PlainRuleTarget("doctrine",1)];
        foreach (double width in new[] { 500.0, 600.0, 850.0 })
        foreach (double height in new[] { 520.0, 800.0 })
        {
            typeof(MapView).GetField("paneWidth", Hidden)!.SetValue(map, width);
            foreach (EditorTarget target in targets)
            {
                if (Call(window, "Show", target) is not true) throw new InvalidOperationException($"Cannot open {target}");
                var editor = (EditorCard)CallOpenEditor(window);
                if (editor is CityEditorCard city)
                {
                    var cityView = (CityEditorCard.View)typeof(EditorWindow).GetMethod("CityView", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [data, 1])!;
                    city.Open(cityView with { Counties = Enumerable.Range(0, 6).Select(i => new CityEditorCard.County(i,
                        i == 0 ? cityView.Name : $"군 {i}", i == 0, "", CountyFields.All.ToDictionary(f => f, f => f == CountyFields.Population ? 40000L : 80L))).ToArray() });
                }
                var pages = Field<WC.Grid>(editor, "pages", typeof(EditorCard));
                for (int page = 0; page < pages.Children.Count; page++)
                {
                    typeof(EditorCard).GetMethod("ShowPage", Hidden)!.Invoke(editor, [page]);
                    Check($"{editor.GetType().Name}-{(editor is PolicyEditorCard p ? p.Table + "-" : editor is PlainRuleEditorCard plain ? plain.Table+"-" : "")}{page}", pane, width, height, editor);
                }
            }
            map.ShowLists(true);
            for (int tab = 0; tab < Enum.GetValues<ListKind>().Length; tab++)
            {
                map.Lists.ShowTab(tab);
                Check($"Lists-{tab}", pane, width, height);
            }
            map.ShowAncients();
            Check("Ancients", pane, width, height);
            AiTree[] trees = Enum.GetValues<AiTreeKind>().Select(k => new AiTree(k, 0, 0, [new AiNode(0, 1, 0, null, -1, 0, [], null, null)])).ToArray();
            var ai = new AiView(trees, () => default(GameIdentity), (_, _) => throw new InvalidOperationException("No live writes"));
            if (AiGuide.Values.Any(v => v.Anchors.Any(a => a.Slot(trees) is not null))) throw new InvalidOperationException("Fixture must not access game memory");
            typeof(AiView).GetField("live", Hidden)!.SetValue(ai, Array.Empty<AiLiveTree>());
            map.ShowAi(ai);
            foreach (AiGuidePage page in AiGuide.Pages)
            {
                Call(ai, "ShowGuide", page);
                WC.Grid[] grids = Field<WC.StackPanel>(ai, "guide").Children.OfType<WC.Grid>().ToArray();
                for (int section = 0; section < grids.Length; section++)
                {
                    // Replace disconnected fixture placeholders before inserting production number controls.
                    foreach (WC.TextBlock cell in grids[section].Children.OfType<WC.TextBlock>().Where(c => WC.Grid.GetRow(c) > 0).ToArray())
                        if (WC.Grid.GetColumn(cell) == 2) grids[section].Children.Remove(cell);
                        else if (WC.Grid.GetColumn(cell) == 1)
                        {
                            AiGuideValue value = page.Sections[section].Values[WC.Grid.GetRow(cell) - 1];
                            cell.Text = AiText.Number(value.Shown(100)) + value.Unit;
                        }
                    for (int row = 0; row < page.Sections[section].Values.Length; row++)
                        Call(ai, "Box", grids[section], row + 1, page.Sections[section].Values[row], 100, (int?)100, false);
                }
                ai.ShowSettings();
                Field<WC.TextBlock>(ai, "status").Text = L.Ai.LiveSame;
                Check($"AI-{page.Decision}", pane, width, height);
            }
            ai.ShowTreeView();
            map.ShowAi(ai);
            foreach (AiTree tree in trees)
            {
                Call(ai, "ShowTree", tree);
                Check($"Tree-{tree.Kind}", ai, width, height);
            }
        }
        File.WriteAllText(Path.Combine(output, "layout.json"), JsonSerializer.Serialize(new { cases, failures, records }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { cases, failures, output = Path.GetFullPath(output) }));
        if (requireFluid && failures > 0) throw new InvalidOperationException($"{failures} panel layout cases failed");
    }

    private static object CallOpenEditor(EditorWindow window) => typeof(EditorWindow).GetProperty("OpenEditor", Hidden)!.GetValue(window)!;
}

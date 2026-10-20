using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Tests;
using W = System.Windows;
using WC = System.Windows.Controls;
using WM = System.Windows.Media;

internal static partial class RenderChecks
{
    private static void CheckRefreshCaching(string output)
    {
        Directory.CreateDirectory(output);
        using var log = new StreamWriter(Path.Combine(output, "checks.jsonl")) { AutoFlush = true };
        int cases = 0;
        void Check(string scenario, bool passed)
        {
            log.WriteLine(JsonSerializer.Serialize(new { scenario, passed, gameAccess = false }));
            Require(passed, scenario); cases++;
        }
        static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Hidden)!.GetValue(obj)!;
        static void Set(object obj, string name, object? value) => obj.GetType().GetField(name, Hidden)!.SetValue(obj, value);
        static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, Hidden)!.Invoke(obj, args);
        static void Layout(W.FrameworkElement root)
        {
            root.Measure(new W.Size(1280, 800)); root.Arrange(new W.Rect(0, 0, 1280, 800)); root.UpdateLayout();
        }
        static GameSnapshot Data(int leadership = 65, int tile = 24280, bool includeOfficer = true)
        {
            var builder = new TestGame().Force(1, 1, 1).Force(2, 2, 2).City(1, 1).City(2, 2).Gate(52, 1);
            for (int i = 1; i <= 120; i++)
                if (i != 11 || includeOfficer) builder.Officer(i, "장수" + i, 1, 1, 3);
            if (includeOfficer) builder.With(11, OfficerFields.Leadership, leadership).Leading(11);
            GameSnapshot data = builder.Build();
            if (includeOfficer) System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                data.Units[0].Data.AsSpan(UnitFields.TileOffset), (ushort)tile);
            return data;
        }
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        var window = OfflineEditor(() => false, ruleStorageRoot: Path.Combine(output, "settings"));
        var map = Field<MapView>(window, "map");
        var lists = Field<ListsCard>(window, "lists");
        var officers = Field<GameTable>(window, "officerTable");
        GameTable.Row OfficerRow() => officers.Rows.Single(r => (int)r.Tag == 11);
        void Snapshot(GameSnapshot? data) { Set(window, "snapshot", data); Call(window, "FillLists"); }
        var surface = new MapSurface { Width = 1280, Height = 800 };
        try
        {
            Snapshot(Data());
            Check("hidden-tabs-do-not-materialize-rows", officers.Rows.Count == 0 && Field<GameTable>(window, "forceTable").Rows.Count == 0);
            map.ShowList(ListKind.Officers); Layout(map);
            Check("opening-tab-materializes-current-data", officers.Rows.Count == 120 && (long)OfficerRow().Cells[5]! == 65);
            map.ShowList(ListKind.Forces);
            var forceFilter = Field<WC.ComboBox>(window, "officerForceBox");
            forceFilter.SelectedIndex = Field<List<int>>(window, "forceChoices").IndexOf(2) + 1;
            Check("hidden-combo-filter-does-not-materialize-rows", officers.Rows.Count == 120);
            map.ShowList(ListKind.Officers);
            Check("hidden-combo-filter-applies-before-exposing-tab", officers.Rows.Count == 0);
            forceFilter.SelectedIndex = 0; Layout(map);
            officers.Select(11); Call(officers, "SortBy", 5);
            var scroll = Field<WC.ScrollViewer?>(officers, "scroll") ?? throw new InvalidOperationException("Officer scroll viewer unavailable");
            scroll.ScrollToVerticalOffset(240); Layout(map);
            double scrollOffset = scroll.VerticalOffset;
            Check("scroll-fixture-has-overflow", scrollOffset > 0);
            object[] order = officers.Rows.Select(r => r.Tag).ToArray();
            GameTable.Row original = OfficerRow();
            NumberField draft = lists.TableDrafts.Get(new OfficerTarget(11), OfficerFields.Leadership, 65);
            draft.Set(95);
            NumberField invalid = lists.TableDrafts.Get(new OfficerTarget(11), OfficerFields.Strength, 0);
            invalid.Box.Text = "bad input";
            map.ShowList(ListKind.Forces);
            Snapshot(Data(75));
            Check("hidden-tab-retains-unmaterialized-rows", ReferenceEquals(OfficerRow(), original) && (long)original.Cells[5]! == 65);
            Check("hidden-draft-follows-live-value-without-losing-input", draft.Current == 75 && draft.Value == 95 && draft.Box.Text == "95");
            Check("hidden-invalid-text-survives-refresh", invalid.Box.Text == "bad input" && lists.TableDrafts.Changes(out _) is null);
            map.ShowList(ListKind.Officers); Layout(map);
            Check("returning-tab-updates-cells-and-preserves-selection-order", (long)OfficerRow().Cells[5]! == 75 &&
                Equals((Field<WC.ListBox>(officers, "list").SelectedItem as GameTable.Row)?.Tag, 11) && order.SequenceEqual(officers.Rows.Select(r => r.Tag)));
            Check("returning-tab-preserves-scroll", scroll.VerticalOffset == scrollOffset);
            Field<SearchBox>(window, "officerSearch").Text = "장수11";
            Call(window, "RefreshLists", true); Layout(map);
            map.ShowLists(false); Snapshot(Data(78));
            Check("collapsed-pane-defers-refresh", (long)OfficerRow().Cells[5]! == 75);
            map.ShowLists(true); Layout(map);
            Check("reopening-same-tab-updates-data-and-preserves-filter", (long)OfficerRow().Cells[5]! == 78 &&
                officers.Rows.All(r => ((string)r.Cells[1]!).StartsWith("장수11")) && Field<int>(officers, "sortColumn") == 5);
            map.ShowList(ListKind.Forces);
            lists.TableDrafts.Changes(out _); lists.FocusInvalidInput();
            Check("validation-prepares-hidden-invalid-tab", lists.Tab == (int)ListKind.Officers && officers.Contains(11) && invalid.Box.Text == "bad input");
            invalid.Show(0); Snapshot(Data(95));
            Check("matching-live-value-clears-pending-draft", draft.Current == 95 && !draft.IsChanged && lists.Pending == 0);
            draft.Set(96); Snapshot(Data(includeOfficer: false));
            var pending = lists.TableDrafts.Changes(out _)!;
            Check("removed-target-retains-draft-and-refuses-apply", draft.Box.Text == "96" &&
                EditorWindow.PlanCardEdits(Field<GameSnapshot>(window, "snapshot"), new([], "fixture"), pending).Refused);
            Snapshot(null);
            Check("disconnect-clears-deferred-rows", officers.Rows.Count == 0 && Field<GameTable>(window, "forceTable").Rows.Count == 0);
            lists.TableDrafts.Clear();

            Layout(surface); surface.SetSnapshot(Data());
            var retained = new MapDrawList();
            var reference = new MapDrawList();
            void DrawRetained()
            {
                retained.Reset(); retained.Clear(WM.Colors.Black);
                Call(surface, "DrawCitiesCached", retained);
            }
            void Warm()
            {
                for (int i = 0; i < 80; i++)
                {
                    DrawRetained();
                    if (Field<bool>(surface, "cityCommandsValid")) return;
                }
                throw new InvalidOperationException("City glyph preparation never completed");
            }
            var gpu = Field<GpuMap?>(surface, "gpu") ?? throw new InvalidOperationException("GPU unavailable");
            Check("gpu-available", true);
            void Compare(string scenario)
            {
                Warm(); DrawRetained();
                var rectangles = Field<Dictionary<int, W.Rect>>(surface, "bannerRects").ToArray();
                var spots = Field<IList>(surface, "spots").Cast<object>().ToArray();
                reference.Reset(); reference.Clear(WM.Colors.Black); Call(surface, "DrawCities", reference);
                Check(scenario + "-commands-and-hit-regions", retained.CompareCommands(reference) == "identical" &&
                    rectangles.SequenceEqual(Field<Dictionary<int, W.Rect>>(surface, "bannerRects")) &&
                    spots.SequenceEqual(Field<IList>(surface, "spots").Cast<object>()));
                double dpi = WM.VisualTreeHelper.GetDpi(surface).DpiScaleX;
                int width = (int)Math.Round(surface.ActualWidth * dpi), height = (int)Math.Round(surface.ActualHeight * dpi);
                gpu.Resize(width, height, (float)(96 * dpi));
                byte[] Pixels(MapDrawList frame)
                {
                    for (int i = 0; i < 80; i++)
                    {
                        Require(gpu.RenderAsync(frame), "City reference frame rejected"); Finish(gpu);
                        if (!gpu.Canvas.Deferred) return ReadPixels(gpu, width, height);
                    }
                    throw new InvalidOperationException("City GPU resources never completed");
                }
                Check(scenario + "-pixels", Pixels(retained).AsSpan().SequenceEqual(Pixels(reference)));
            }
            Compare("initial");
            int built = surface.CityLayoutBuilds;
            Call(surface, "UpdateGlides", Field<System.Diagnostics.Stopwatch>(surface, "clock").Elapsed.TotalSeconds - 2);
            surface.SetSnapshot(Data(tile: 24281)); Warm(); built = surface.CityLayoutBuilds;
            var beforeMove = (W.Point)Call(surface, "UnitPoint", Field<GameSnapshot>(surface, "snapshot").Units[0])!;
            System.Threading.Thread.Sleep(30);
            Call(Field<object>(surface, "units"), "Redraw"); DrawRetained();
            var afterMove = (W.Point)Call(surface, "UnitPoint", Field<GameSnapshot>(surface, "snapshot").Units[0])!;
            Check("interpolating-unit-reuses-city-layout", beforeMove != afterMove && surface.CityLayoutBuilds == built && surface.CityLayoutReuses > 0);
            Compare("moving-unit");
            void Change(string name, Action change)
            {
                int count = surface.CityLayoutBuilds; change(); DrawRetained();
                Check(name + "-invalidates", surface.CityLayoutBuilds > count); Compare(name);
            }
            Change("zoom", () => { Set(surface, "zoom", 4.0); Call(surface, "ApplyView"); });
            Change("pan", () => { Set(surface, "center", new W.Point(170, 190)); Call(surface, "ApplyView"); });
            Change("size", () => { surface.Width = 1200; surface.Measure(new W.Size(1200, 800)); surface.Arrange(new W.Rect(0, 0, 1200, 800)); });
            void ChangeDpi(string name, double dpi)
            {
                double before = WM.VisualTreeHelper.GetDpi(surface).DpiScaleX;
                double target = before == dpi ? dpi + 0.25 : dpi;
                Change(name, () =>
                {
                    WM.VisualTreeHelper.SetRootDpi(surface, new W.DpiScale(target, target));
                    log.WriteLine(JsonSerializer.Serialize(new { scenario = name + "-actual-dpi", before, target,
                        actual = WM.VisualTreeHelper.GetDpi(surface).DpiScaleX,
                        text = WM.VisualTreeHelper.GetDpi(Field<W.FrameworkElement>(surface, "cities")).DpiScaleX }));
                });
            }
            ChangeDpi("dpi", 1.5); ChangeDpi("dpi-192", 2); ChangeDpi("dpi-96", 1);
            Change("city-selection", () => surface.SelectedCity = 1);
            Change("gate-selection", () => surface.SelectedGate = 52);
            Change("city-hover", () => Call(surface, "SetHover", Activator.CreateInstance(
                typeof(MapSurface).GetNestedType("MapHit", BindingFlags.NonPublic)!, [1, null, null, 0, null])!));
            Change("unit-mode", () => Call(surface, "SetUnitMode", Enum.ToObject(typeof(MapSurface).GetNestedType("UnitMode", BindingFlags.NonPublic)!, 2)));
            Change("force-filter", () => surface.HighlightForce = 1);
            Change("models", () => surface.SetModels([], new Dictionary<(string, int, double), GameModels.Sprite?>()));
            Change("palette", surface.RedrawColors);
            Change("snapshot", () => surface.SetSnapshot(Data(77)));
            Change("disconnect", () => surface.SetSnapshot(null));
            Change("reconnect", () => surface.SetSnapshot(Data()));
            // Incomplete glyphs cannot be kept as the final city layer.
            ((IDictionary)Field<object>(surface, "outlines")).Clear();
            ((IDictionary)Field<object>(surface, "outlineSizes")).Clear();
            GameSnapshot troopLabels = Data();
            for (int city = 1; city <= GameMemory.CityCount; city++)
                CityFields.Troops.Encode(city * 1000).CopyTo(troopLabels.Cities[city].Data, CityFields.Troops.Offset);
            surface.SetSnapshot(troopLabels);
            Set(surface, "zoom", 1.8); Set(surface, "looping", true); Call(surface, "ApplyView"); DrawRetained();
            Check("pending-glyphs-do-not-become-valid-cache", Field<bool>(surface, "labelsPending") && !Field<bool>(surface, "cityCommandsValid"));
            Set(surface, "looping", false); Warm(); Compare("completed-glyphs");
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { cases, surface.CityLayoutBuilds, surface.CityLayoutReuses, gameAccess = false }));
            Console.WriteLine($"PASS refresh caching: {cases} checks; gameAccess=false");
        }
        finally
        {
            lists.TableDrafts.Clear(); map.HideEditors(); window.Close();
            Field<GpuMap?>(surface, "gpu")?.Dispose(); app.Shutdown();
        }
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using San14Tenkazu;
using W = System.Windows;
using WC = System.Windows.Controls;
using WMI = System.Windows.Media.Imaging;
using WM = System.Windows.Media;
using WI = System.Windows.Input;
using WT = System.Windows.Threading;

internal static partial class RenderChecks
{
    // Native WPF controls against an isolated registered-file fixture. No browser or live game writes.
    private static void CheckRegistered(string? output, string? original = null)
    {
        string folder = Path.GetFullPath(output ?? "source/RenderChecks/artifacts/registered");
        Directory.CreateDirectory(folder);
        using var log = new StreamWriter(Path.Combine(folder, "checks.jsonl"));
        void Record(string scenario, bool passed)
        {
            string row = JsonSerializer.Serialize(new { scenario, passed });
            log.WriteLine(row); log.Flush(); Console.WriteLine(row);
            Require(passed, "Registered officer check failed: " + scenario);
        }
        static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Hidden)!.GetValue(obj)!;
        static void AwaitState(Func<bool> ready)
        {
            var frame = new WT.DispatcherFrame();
            var deadline = Stopwatch.StartNew();
            var timer = new WT.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { if (ready() || deadline.Elapsed.TotalSeconds > 3) frame.Continue = false; };
            timer.Start();
            WT.Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        void Capture(W.FrameworkElement root, string name, double width = 1100, double height = 800)
        {
            root.Measure(new W.Size(width, height));
            root.Arrange(new W.Rect(0, 0, width, height));
            root.UpdateLayout();
            W.DpiScale dpi = WM.VisualTreeHelper.GetDpi(root);
            var bitmap = new WMI.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, WM.PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new WMI.PngBitmapEncoder(); png.Frames.Add(WMI.BitmapFrame.Create(bitmap));
            using Stream stream = File.Create(Path.Combine(folder, name + ".png")); png.Save(stream);
        }
        bool fixtureRunning = false;
        var data = new byte[RegisteredOfficerFields.Stride * RegisteredOfficerFields.Slots];
        for (int slot = 0; slot < RegisteredOfficerFields.Slots; slot++)
        {
            var record = new byte[RegisteredOfficerFields.Stride];
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), 4);
            if (slot < 11)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)(RegisteredOfficerFields.FirstId + slot));
                RegisteredOfficerFields.WriteText(record, RegisteredOfficerFields.Surname, "A");
                RegisteredOfficerFields.WriteText(record, RegisteredOfficerFields.GivenName, (slot + 1).ToString());
                RegisteredOfficerFields.Clan.Encode(3001 + slot).CopyTo(record, RegisteredOfficerFields.Clan.Offset);
                if (slot is 0 or 1 or 9) RegisteredOfficerFields.Sworn.Encode(3001).CopyTo(record, RegisteredOfficerFields.Sworn.Offset);
                if (slot == 0) BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x61), 0x80004000);
            }
            foreach (var field in RegisteredOfficerFields.Abilities) field.Encode(90).CopyTo(record, field.Offset);
            record.CopyTo(data, slot * RegisteredOfficerFields.Stride);
        }
        string fixture = Path.Combine(folder, "fixture.s14");
        File.WriteAllBytes(fixture, data);
        var window = new RegisteredOfficerWindow(GameCatalog.Empty, _ => { }, () => fixtureRunning);
        window.Load(fixture);
        var list = Field<WC.ListBox>(window, "list");
        var numbers = Field<Dictionary<FieldDef, NumberField>>(window, "numbers");
        var texts = Field<Dictionary<RegisteredOfficerFields.Text, WC.TextBox>>(window, "texts");
        var choices = Field<Dictionary<FieldDef, NameChoice>>(window, "choices");
        var relations = Field<Dictionary<string, RegisteredRelationList>>(window, "relationLists");
        var working = Field<Dictionary<int, RegisteredOfficer>>(window, "working");
        static int[] Members(RegisteredRelationList control) => control.Members.Children.OfType<WC.Border>().Select(c => (int)c.Tag).ToArray();
        void Add(string key, int id)
        {
            relations[key].Add.Pick(id);
            Record(key + "-add-button-enables-" + id, relations[key].Take.IsEnabled);
            Field<Action>(relations[key].Take, "Click")();
        }
        void Remove(string key, int id)
        {
            var chip = relations[key].Members.Children.OfType<WC.Border>().Single(c => Equals(c.Tag, id));
            var content = (WC.StackPanel)chip.Child;
            var remove = content.Children.OfType<WC.TextBlock>().Single(t => t.Text == "✕");
            remove.RaiseEvent(new WI.MouseButtonEventArgs(WI.Mouse.PrimaryDevice, 0, WI.MouseButton.Left)
                { RoutedEvent = W.UIElement.MouseLeftButtonUpEvent });
        }
        Record("sworn-list-shows-both-other-members", Members(relations["sworn"]).SequenceEqual(new[] { 3002, 3010 }));
        Record("relationship-candidates-exclude-self", relations.Values.All(r => !Field<List<int>>(r.Add, "values").Contains(3001))
            && RegisteredOfficerRelations.Direct.All(f => !Field<List<int>>(choices[f], "values").Contains(3001)));
        Record("registered-choice-lists-exclude-unavailable-items", !Field<List<int>>(choices[RegisteredOfficerFields.Personalities[0]], "values").Contains(79)
            && !Field<List<int>>(choices[RegisteredOfficerFields.Tactics[0]], "values").Contains(27)
            && Field<List<int>>(choices[RegisteredOfficerFields.Voice], "values").Max() == 13);
        Record("offline-file-loads-eleven-officers", list.Items.Count == 11 && !window.HasPending);
        numbers[RegisteredOfficerFields.Abilities[0]].Set(100);
        texts[RegisteredOfficerFields.StyleName].Text = "XY";
        texts[RegisteredOfficerFields.Biography].Text = "A biography with retained input.";
        choices[RegisteredOfficerFields.Personalities[0]].Pick(1);
        Record("all-field-groups-stage-edits", window.HasPending);
        list.SelectedIndex = 1;
        numbers[RegisteredOfficerFields.Abilities[1]].Set(100);
        list.SelectedIndex = 0;
        Record("switching-officers-keeps-pending-values", numbers[RegisteredOfficerFields.Abilities[0]].Value == 100
            && texts[RegisteredOfficerFields.StyleName].Text == "XY" && choices[RegisteredOfficerFields.Personalities[0]].Picked == 1
            && texts[RegisteredOfficerFields.Biography].Text.StartsWith("A biography") && window.HasPending);
        Record("staged-numbers-stay-highlighted", ReferenceEquals(numbers[RegisteredOfficerFields.Abilities[0]].Box.Background, CardSkin.Pending));
        Record("list-total-follows-staged-edits", ((WC.ListBoxItem)list.Items[0]).Content.ToString()!.Contains("460"));
        Record("pending-edits-do-not-write-source", File.ReadAllBytes(fixture).AsSpan().SequenceEqual(data));
        numbers[RegisteredOfficerFields.Abilities[0]].Set(90);
        list.SelectedIndex = 1;
        list.SelectedIndex = 0;
        Record("staged-number-can-return-to-original", numbers[RegisteredOfficerFields.Abilities[0]].Value == 90
            && ReferenceEquals(numbers[RegisteredOfficerFields.Abilities[0]].Box.Background, CardSkin.Paper)
            && ((WC.ListBoxItem)list.Items[0]).Content.ToString()!.Contains("450"));
        Record("duplicate-personality-is-disabled-in-another-slot", ((WC.ComboBoxItem)choices[RegisteredOfficerFields.Personalities[1]].Box.Items[1]).IsEnabled == false);
        foreach (var (slot, id) in new[] { (1, 2), (2, 3), (3, 4), (8, 5) }) choices[RegisteredOfficerFields.Personalities[slot]].Pick(id);
        bool TraitAllowed(int slot, int id)
        {
            NameChoice choice = choices[RegisteredOfficerFields.Personalities[slot]];
            int index = Field<List<int>>(choice, "values").IndexOf(id);
            return ((WC.ComboBoxItem)choice.Box.Items[index]).IsEnabled;
        }
        Record("sixth-personality-is-disabled-across-nine-storage-slots", !TraitAllowed(4, 6) && !TraitAllowed(5, 6));
        Record("one-of-five-personalities-can-be-replaced", TraitAllowed(0, 6));
        choices[RegisteredOfficerFields.Personalities[8]].Pick(0);
        Record("clearing-a-personality-allows-another-selection", TraitAllowed(4, 6));
        foreach (int slot in new[] { 1, 2, 3 }) choices[RegisteredOfficerFields.Personalities[slot]].Pick(0);
        Record("personality-and-tactic-use-limits-are-explained", L.Registered.PersonalityNote.Contains("5개") && L.Registered.TacticNote.Contains("10개")
            && numbers[RegisteredOfficerFields.Face].Box.ToolTip?.ToString()?.Contains(L.Registered.FaceNote) == true);
        Add("sworn", 3003);
        Record("sworn-add-updates-the-group-and-preserves-other-input", Members(relations["sworn"]).SequenceEqual(new[] { 3002, 3003, 3010 })
            && RegisteredOfficerFields.Sworn.Read(working[3003].Data) == 3001 && texts[RegisteredOfficerFields.StyleName].Text == "XY");
        Remove("sworn", 3002);
        Record("sworn-chip-removes-member-without-changing-root-number", Members(relations["sworn"]).SequenceEqual(new[] { 3003, 3010 })
            && RegisteredOfficerFields.Sworn.Read(working[3002].Data) == 0 && RegisteredOfficerFields.Sworn.Read(working[3001].Data) == 3001);
        Add("clan", 3004);
        Add("clan", 3005);
        Record("clan-supports-multiple-members", Members(relations["clan"]).SequenceEqual(new[] { 3004, 3005 })
            && RegisteredOfficerFields.Clan.Read(working[3004].Data) == 3001 && RegisteredOfficerFields.Clan.Read(working[3005].Data) == 3001);
        Add("likes", 3002);
        Add("likes", 3003);
        Record("likes-are-multiple-one-way-and-cannot-be-added-twice", Members(relations["likes"]).SequenceEqual(new[] { 3002, 3003 })
            && !Field<List<int>>(relations["likes"].Add, "values").Contains(3002) && RegisteredOfficerFields.Likes.All(f => f.Read(working[3002].Data) == 0));
        Remove("likes", 3002);
        Record("likes-remove-clears-the-vacated-slot", Members(relations["likes"]).SequenceEqual(new[] { 3003 })
            && RegisteredOfficerFields.Likes[0].Read(working[3001].Data) == 3003 && RegisteredOfficerFields.Likes[1].Read(working[3001].Data) == 0);
        Add("dislikes", 3011);
        Record("dislikes-use-an-independent-list", Members(relations["dislikes"]).SequenceEqual(new[] { 3011 }) && Members(relations["likes"]).SequenceEqual(new[] { 3003 }));
        choices[RegisteredOfficerFields.Spouse].Pick(3006);
        list.SelectedIndex = 5;
        Record("spouse-is-updated-on-both-registered-officers", choices[RegisteredOfficerFields.Spouse].Picked == 3001
            && RegisteredOfficerFields.Spouse.Read(working[3001].Data) == 3006 && RegisteredOfficerFields.Spouse.Read(working[3006].Data) == 3001);
        list.SelectedIndex = 0;
        choices[RegisteredOfficerFields.Spouse].Pick(0);
        list.SelectedIndex = 5;
        Record("clearing-spouse-clears-the-reciprocal-partner", choices[RegisteredOfficerFields.Spouse].Picked == 0);
        list.SelectedIndex = 0;
        Record("switching-keeps-all-relationship-lists", Members(relations["sworn"]).SequenceEqual(new[] { 3003, 3010 })
            && Members(relations["clan"]).SequenceEqual(new[] { 3004, 3005 }) && Members(relations["likes"]).SequenceEqual(new[] { 3003 }));
        for (int id = 3004; id <= 3010; id++) Add("likes", id);
        Record("eight-likes-disable-the-add-picker", Members(relations["likes"]).Length == 8 && !relations["likes"].Add.Box.IsEnabled);
        Remove("likes", 3010);
        Record("removal-reenables-the-add-picker", Members(relations["likes"]).Length == 7 && relations["likes"].Add.Box.IsEnabled);
        Record("relationship-edits-do-not-write-the-original", File.ReadAllBytes(fixture).AsSpan().SequenceEqual(data));
        texts[RegisteredOfficerFields.Surname].Text = "설";
        texts[RegisteredOfficerFields.GivenName].Text = "지";
        foreach (var (field, value) in new[] { (RegisteredOfficerFields.Face, 54321), (RegisteredOfficerFields.Birth, 189),
            (RegisteredOfficerFields.Death, 251), (RegisteredOfficerFields.Compatibility, 149), (RegisteredOfficerFields.PolicyLevel, 6) })
            numbers[field].Set(value);
        foreach (var (field, value) in new[] { (RegisteredOfficerFields.Gender, 1), (RegisteredOfficerFields.Ideology, 6),
            (RegisteredOfficerFields.Policy, 1), (RegisteredOfficerFields.Voice, 13), (RegisteredOfficerFields.Tactics[0], 1) })
            choices[field].Pick(value);
        Field<Dictionary<int, WC.CheckBox>>(window, "formations")[2].IsChecked = true;
        list.SelectedIndex = 1;
        list.SelectedIndex = 0;
        Record("remaining-basic-fields-names-tactic-and-formation-survive-switching", working[3001].Name == "설지"
            && RegisteredOfficerFields.Face.Read(working[3001].Data) == 54321 && RegisteredOfficerFields.Gender.Read(working[3001].Data) == 1
            && RegisteredOfficerFields.Birth.Read(working[3001].Data) == 189 && RegisteredOfficerFields.Death.Read(working[3001].Data) == 251
            && RegisteredOfficerFields.Compatibility.Read(working[3001].Data) == 149 && RegisteredOfficerFields.Ideology.Read(working[3001].Data) == 6
            && RegisteredOfficerFields.Policy.Read(working[3001].Data) == 1 && RegisteredOfficerFields.PolicyLevel.Read(working[3001].Data) == 6
            && RegisteredOfficerFields.Voice.Read(working[3001].Data) == 13 && RegisteredOfficerFields.Tactics[0].Read(working[3001].Data) == 1
            && RegisteredOfficerFields.Formations.Read(working[3001].Data) == 0x80004004);
        choices[RegisteredOfficerFields.Father].Pick(147);
        choices[RegisteredOfficerFields.Mother].Pick(148);
        list.SelectedIndex = 1;
        list.SelectedIndex = 0;
        Record("parent-pickers-store-officer-ids-and-keep-the-current-name", choices[RegisteredOfficerFields.Father].Picked == 147
            && choices[RegisteredOfficerFields.Mother].Picked == 148 && !Field<List<int>>(choices[RegisteredOfficerFields.Father], "values").Contains(3001));
        var tabs = Field<WC.TabControl>(window, "tabs");
        var root = (W.FrameworkElement)window.Content;
        var save = Field<PlateButton>(window, "save");
        var availability = Field<WC.TextBlock>(window, "availability");
        var gameTimer = Field<WT.DispatcherTimer>(window, "gameTimer");
        string stagedBiography = texts[RegisteredOfficerFields.Biography].Text;
        long stagedFace = numbers[RegisteredOfficerFields.Face].Value;
        gameTimer.Start();
        fixtureRunning = true;
        AwaitState(() => !save.IsEnabled);
        Record("running-state-timer-blocks-editing-and-saving", !save.IsEnabled && save.Text == L.Registered.SaveBlocked
            && window.Title == L.Registered.ReadOnlyTitle && availability.Text == L.Registered.RunningNote
            && numbers.Values.All(n => n.Box.IsReadOnly) && texts.Values.All(t => t.IsReadOnly)
            && choices.Values.All(c => !c.Box.IsEnabled) && relations.Values.All(r => !r.Panel.IsEnabled)
            && Field<Dictionary<int, WC.CheckBox>>(window, "formations").Values.All(c => !c.IsEnabled));
        Record("running-state-keeps-pending-input-and-browsing", window.HasPending && tabs.IsEnabled && list.IsEnabled
            && texts[RegisteredOfficerFields.Biography].Text == stagedBiography && numbers[RegisteredOfficerFields.Face].Value == stagedFace);
        Capture(root, "game-running");
        Field<Action>(save, "Click")();
        Record("running-save-guard-does-not-write-or-discard", File.ReadAllBytes(fixture).AsSpan().SequenceEqual(data)
            && window.HasPending && texts[RegisteredOfficerFields.Biography].Text == stagedBiography);
        fixtureRunning = false;
        AwaitState(() => save.IsEnabled);
        Record("game-exit-timer-restores-editing-with-pending-input", save.IsEnabled && save.Text == L.Registered.Save
            && availability.Text == L.Registered.Note && window.Title == L.Registered.Title && numbers.Values.All(n => !n.Box.IsReadOnly)
            && texts.Values.All(t => !t.IsReadOnly) && choices.Values.All(c => c.Box.IsEnabled) && relations.Values.All(r => r.Panel.IsEnabled)
            && texts[RegisteredOfficerFields.Biography].Text == stagedBiography && numbers[RegisteredOfficerFields.Face].Value == stagedFace);
        fixtureRunning = true; // Catch a launch that happened before the polling timer's next tick.
        Field<Action>(save, "Click")();
        Record("save-rechecks-a-new-game-launch-immediately", !save.IsEnabled && File.ReadAllBytes(fixture).AsSpan().SequenceEqual(data));
        fixtureRunning = false;
        AwaitState(() => save.IsEnabled);
        static IEnumerable<W.FrameworkElement> Elements(W.DependencyObject parent)
        {
            if (parent is W.FrameworkElement element) yield return element;
            if (parent is WC.ComboBox or WC.TextBox) yield break;
            for (int i = 0; i < WM.VisualTreeHelper.GetChildrenCount(parent); i++)
                foreach (var child in Elements(WM.VisualTreeHelper.GetChild(parent, i))) yield return child;
        }
        for (int page = 0; page < tabs.Items.Count; page++)
        {
            tabs.SelectedIndex = page;
            root.Measure(new W.Size(1100, 800));
            root.Arrange(new W.Rect(0, 0, 1100, 800));
            root.UpdateLayout();
            var labels = Elements(root).OfType<WC.TextBlock>().Select(label => new
            {
                label.Text, x = label.TransformToAncestor(root).Transform(new W.Point()).X,
                y = label.TransformToAncestor(root).Transform(new W.Point()).Y, width = label.ActualWidth, height = label.ActualHeight
            }).ToArray();
            File.WriteAllText(Path.Combine(folder, $"labels-{page}.json"), JsonSerializer.Serialize(labels));
            Capture(root, "page-" + page);
            Record("native-page-layout-" + page, tabs.ActualWidth > 700 && list.ActualWidth > 200);
        }
        if (original is not null)
        {
            byte[] beforeOriginal = File.ReadAllBytes(original);
            window.Load(original);
            var originalFile = RegisteredOfficerFile.Read(original);
            var rows = new List<object>();
            for (int index = 0; index < list.Items.Count; index++)
            {
                list.SelectedIndex = index;
                RegisteredOfficer officer = originalFile.Officers[index];
                Record("original-controls-match-all-fields-" + officer.Id, numbers.All(n => n.Value.Value == n.Key.Read(officer.Data))
                    && choices.All(c => c.Value.Picked == c.Key.Read(officer.Data))
                    && texts.All(t => t.Value.Text == RegisteredOfficerFields.ReadText(officer.Data, t.Key))
                    && Members(relations["sworn"]).SequenceEqual(RegisteredOfficerRelations.Members(originalFile.Officers, officer.Id, RegisteredOfficerFields.Sworn))
                    && Members(relations["clan"]).SequenceEqual(RegisteredOfficerRelations.Members(originalFile.Officers, officer.Id, RegisteredOfficerFields.Clan)));
                rows.Add(new { officer.Id, officer.Name, officer.Total,
                    abilities = RegisteredOfficerFields.Abilities.Select(f => f.Read(officer.Data)),
                    sworn = Members(relations["sworn"]), clan = Members(relations["clan"]),
                    likes = Members(relations["likes"]), dislikes = Members(relations["dislikes"]) });
            }
            File.WriteAllText(Path.Combine(folder, "original-readonly-audit.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions
                { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Record("original-readonly-audit-does-not-stage-or-write", !window.HasPending && File.ReadAllBytes(original).AsSpan().SequenceEqual(beforeOriginal));
        }
        window.AllowOwnerClose(); window.Close();
        Record("registered-state-timer-stops-when-window-closes", !gameTimer.IsEnabled);
        bool toolbarRunning = false;
        var editor = OfflineEditor(() => toolbarRunning);
        var button = Field<PlateButton>(editor, "registeredButton");
        Record("toolbar-button-available-without-game-connection", button.IsEnabled && button.ToolTip?.ToString() == L.Registered.Tip);
        Record("toolbar-button-click-wired", typeof(PlateButton).GetField("Click", Hidden)!.GetValue(button) is Action);
        var toolbarTimer = Field<WT.DispatcherTimer>(editor, "registeredTimer");
        toolbarTimer.Start();
        toolbarRunning = true;
        AwaitState(() => !button.IsEnabled);
        Record("toolbar-detects-running-game-without-editor-connection", !button.IsEnabled && button.Text == L.Registered.Button
            && button.ToolTip?.ToString() == L.Registered.RunningTip && WC.ToolTipService.GetShowOnDisabled(button));
        var editorRoot = (W.FrameworkElement)editor.Content;
        var map = Field<MapView>(editor, "map");
        Capture(editorRoot, "toolbar-game-running", 1000, 680);
        // This capture is offscreen; IsVisible also requires a shown Window.
        // Check the selected pane, layout visibility and rendered plate size.
        log.WriteLine(JsonSerializer.Serialize(new { scenario="toolbar-button-layout", map.PaneShown, button.IsVisible,
            visibility=button.Visibility.ToString(), button.ActualWidth, button.ActualHeight }));log.Flush();
        Record("registered-button-laid-out-without-pane", !map.PaneShown && button.Visibility == W.Visibility.Visible && button.ActualHeight > 0);
        W.Rect blockedButton = button.TransformToAncestor(editorRoot).TransformBounds(new W.Rect(0, 0, button.ActualWidth, button.ActualHeight));
        // FrameworkElement.DesiredSize includes Margin; ActualWidth is only the plate.
        double requiredWidth = button.DesiredSize.Width - button.Margin.Left - button.Margin.Right;
        File.WriteAllText(Path.Combine(folder, "toolbar-layout.json"), JsonSerializer.Serialize(new
            { rootWidth = editorRoot.ActualWidth, blockedButton.Left, blockedButton.Right, button.ActualWidth,
                desiredWidth = button.DesiredSize.Width, marginLeft = button.Margin.Left, marginRight = button.Margin.Right, requiredWidth }));
        Record("blocked-toolbar-caption-fits-minimum-width", blockedButton.Right <= editorRoot.ActualWidth + 0.75
            && blockedButton.Left >= -0.75 && button.ActualWidth + 0.75 >= requiredWidth);
        typeof(EditorWindow).GetMethod("UpdateControls", Hidden)!.Invoke(editor, null);
        Record("normal-toolbar-refresh-cannot-enable-running-edit", !button.IsEnabled);
        toolbarRunning = false;
        AwaitState(() => button.IsEnabled);
        Record("toolbar-detects-game-exit-without-editor-connection", button.IsEnabled && button.Text == L.Registered.Button && button.ToolTip?.ToString() == L.Registered.Tip);
        typeof(EditorWindow).GetField("busy", Hidden)!.SetValue(editor, true);
        typeof(EditorWindow).GetMethod("UpdateControls", Hidden)!.Invoke(editor, null);
        Record("busy-toolbar-still-blocks-opening", !button.IsEnabled);
        typeof(EditorWindow).GetField("busy", Hidden)!.SetValue(editor, false);
        typeof(EditorWindow).GetMethod("UpdateControls", Hidden)!.Invoke(editor, null);
        toolbarRunning = true;
        Field<Action>(button, "Click")();
        Record("open-rechecks-a-new-game-launch-immediately", !button.IsEnabled && typeof(EditorWindow).GetField("registeredEditor", Hidden)!.GetValue(editor) is null);
        bool observedRunning = RegisteredOfficerFile.GameIsRunning();
        var actualWindow = new RegisteredOfficerWindow(GameCatalog.Empty, _ => { });
        Record("initial-state-uses-the-real-game-process-detector", (actualWindow.Title == L.Registered.ReadOnlyTitle) == observedRunning);
        log.WriteLine(JsonSerializer.Serialize(new { scenario = "observed-game-process-state", running = observedRunning })); log.Flush();
        if (observedRunning)
        {
            var guardedFile = new RegisteredOfficerFile(fixture, data);
            byte[] edited = (byte[])data.Clone();
            edited[RegisteredOfficerFields.AbilityOffset] = 99;
            bool blocked = false;
            try { guardedFile.Save(edited, Path.Combine(folder, "blocked-game-backups")); }
            catch (InvalidOperationException ex) { blocked = ex.Message == L.Registered.GameRunning; }
            Record("real-running-game-blocks-the-core-writer-without-file-changes", blocked && File.ReadAllBytes(fixture).AsSpan().SequenceEqual(data)
                && !Directory.Exists(Path.Combine(folder, "blocked-game-backups")));
        }
        actualWindow.AllowOwnerClose(); actualWindow.Close();
        var surface = Field<MapSurface>(map, "surface");
        typeof(MapSurface).GetMethod("StopLoop", Hidden)!.Invoke(surface, null);
        Field<GpuMap?>(surface, "gpu")?.Dispose();
        editor.Close();
        Record("toolbar-state-timer-stops-when-window-closes", !toolbarTimer.IsEnabled);
    }
}

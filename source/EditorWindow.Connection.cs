using System.Globalization;
using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;

namespace San14Tenkazu;

// The connection to the game: finding it, reading it again every two seconds, and AUTO(데모) 플레이.
internal sealed partial class EditorWindow
{
    private async Task ConnectAsync()
    {
        if (busy || automaticGameChecking && !automaticGameConnecting) return;
        if (!ConfirmDiscard()) return;
        scanCancellation = new CancellationTokenSource();
        CancellationToken token = scanCancellation.Token;
        SetBusy(true, scanning: true);
        try
        {
            status.Text = L.Connection.CheckingExe;
            GameIdentity identity = await Task.Run(GameMemory.FindGame, token);
            StartFaceLibrary(GameMemory.ExecutablePath(identity));
            if(!await DisconnectAsync(null))return;
            lists.TableDrafts.Clear();
            aiView?.DiscardDrafts();
            status.Text = L.Connection.FindingData;
            List<BasePointer> known = identity.Verified ? [] : builds.Pointers(identity.ExeHash);
            GameLayout found = await Task.Run(() => GameMemory.Resolve(identity,
                message => Dispatcher.InvokeAsync(() => status.Text = message), token, known), token);
            if (!identity.Verified && found.Pointers.Count > 0)
            {
                if (found.Pointers.SequenceEqual(known)) AddLog(L.Connection.KnownPlaces);
                else
                {
                    Keep(() => builds.Remember(identity.ExeHash, found.Pointers), L.Connection.PlacesNotSaved);
                    AddLog(L.Connection.PlacesFound(found.Pointers.Count));
                }
            }
            AddLog(L.Connection.Connected(identity.ProcessId, found.Tables.City, found.CountyCount, found.OfficerCount));
            if (found.UnitCount == 0) AddLog(L.Connection.NoUnits);
            SetOperationProgress(L.OperationProgress.Reading);
            GameSnapshot data = await Task.Run(() => GameMemory.ReadSnapshot(identity, found), token);
            countryNames = await Task.Run(() => GameMemory.ReadCountryNames(identity, found), token);
            catalog = await Task.Run(() => GameMemory.ReadCatalog(identity, found), token);
            officerEditor.SetCatalog(catalog);
            forceEditor.SetNames(countryNames, catalog);
            if (catalog.Personalities.Length == 0 || catalog.Tactics.Length == 0) AddLog(L.Connection.NoNameLists);
            game = identity;
            layout = found;
            snapshot = data;
            if (runtimeRulesWindow is { } rulesWindow)
            {
                bool preserve = rulesWindow.HasPendingChanges;
                runtimeRulesIdentity = identity; rulesWindow.SetOffline(false);
                rulesWindow.ShowSettings(data.RuntimeRules, preserve);
                if (!preserve && runtimeRuleFile.Read() is { } savedRules) rulesWindow.ShowSaved(savedRules, select: true);
            }
            layoutRecovering = false;
            AttachSeasons(identity);
            map.SetSnapshot(data);
            FillLists();
            TrustBuild(identity, found, data);
            if (aiView is not null) await aiView.RereadAsync();
            liveTimer.Start();
            status.Text = writesAllowed ? L.Connection.Ready
                : L.Connection.ReadyReadOnly;
            AddLog(status.Text);
            if (found.State == 0) AddLog(L.Connection.NoGameState);
            demoNoticed = false;
            ObservePlayerForce(data);
        }
        catch (OperationCanceledException)
        {
            status.Text = L.Connection.SearchCancelled;
            AddLog(status.Text);
        }
        catch (Exception ex)
        {
            await DisconnectAsync(null);
            if (automaticGameConnecting) AutomaticNotice(L.AutomaticSettings.Failed + " " + ex.Message);
            else ReportError(ex);
        }
        finally
        {
            scanCancellation?.Dispose();
            scanCancellation = null;
            SetBusy(false, scanning: false);
        }
        // The game builds its AI trees anew from its file when it starts: the numbers kept go in again.
        if (game is not null) await ReapplyAiAsync();
        if (game is not null) await ReapplyRuntimeRulesAsync(retry: true);
        if (game is not null) await ReapplyEnvironmentAsync(retry: true);
        if (game is not null) { ruleTimer.Start(); await PollRulesAsync(); }
    }

    private async Task ReloadAsync()
    {
        if (busy || game is null || layout is null) return;
        if (!ConfirmDiscard()) return;
        GameIdentity identity = game.Value;
        GameLayout found = layout;
        SetBusy(true, scanning: false);
        SetOperationProgress(L.OperationProgress.Reading);
        try
        {
            if (identity.Verified) found = await Task.Run(() => GameMemory.ReadCurrentLayout(identity));
            snapshot = await Task.Run(() => GameMemory.ReadSnapshot(identity, found));
            if (!layout.SameStorage(found)) undoHistory.RemoveAll(h => h.Rule is null);
            layout = found;
            layoutRecovering = false;
            map.SetSnapshot(snapshot);
            lists.TableDrafts.Clear();
            aiView?.DiscardDrafts();
            FillLists();
            ReopenEditor();
            if (aiView is not null) await aiView.RereadAsync();
            status.Text = L.Connection.Reread;
            ObservePlayerForce(snapshot);
        }
        catch (Exception ex)
        {
            await DisconnectAsync(L.Connection.ReadFailed);
            ReportError(ex);
        }
        finally { SetBusy(false, scanning: false); }
        if (game == identity) await ReapplyRuntimeRulesAsync(retry: true);
        if (game == identity) await ReapplyEnvironmentAsync(retry: true);
    }

    // Keeps the map and the current values live while the game runs.
    private async Task LiveRefreshAsync()
    {
        if (busy || refreshing || game is null || layout is null) return;
        refreshing = true;
        GameIdentity identity = game.Value;
        GameLayout found = layout;
        bool replaced = false; // a load swapped the world: retry rules/environment that failed or self-stopped meanwhile
        try
        {
            if (identity.Verified) found = await Task.Run(() => GameMemory.ReadCurrentLayout(identity));
            GameSnapshot? before = snapshot;
            GameSnapshot data = await Task.Run(() => GameMemory.ReadSnapshot(identity, found, before));
            if (busy || game != identity) return;
            replaced = !layout.SameStorage(found);
            if (replaced) undoHistory.RemoveAll(h => h.Rule is null);
            layout = found;
            layoutRecovering = false;
            snapshot = data;
            map.SetSnapshot(data);
            MergeSnapshot();
            ObservePlayerForce(data);
        }
        catch (Exception ex)
        {
            if (game == identity)
            {
                if (identity.Verified && GameMemory.IsSameGame(identity))
                {
                    if (!layoutRecovering) AddLog(L.Rules.Rechecking + " " + ex.Message);
                    layoutRecovering = true;
                    status.Text = L.Rules.Rechecking;
                    UpdateControls();
                }
                else await DisconnectAsync(L.Connection.Disconnected(ex.Message));
            }
        }
        finally
        {
            refreshing = false;
#if LOCAL_DEVELOPER
            UpdateDeveloperControls();
#endif
        }
        if (replaced) AddLog(L.Connection.WorldReplaced);
        if (game == identity) await ReapplyRuntimeRulesAsync(retry: replaced);
        if (game == identity) await ReapplyEnvironmentAsync(retry: replaced);
    }

    private async Task<bool> DisconnectAsync(string? message)
    {
        if(seasonDisconnecting||seasonClosing||environmentChanging)return false;
        bool wasBusy=busy;
        seasonDisconnecting=true;
        SetBusy(true,scanning:false);
        liveTimer.Stop();
        try
        {
            if(!await StopEnvironmentAsync(true)||!await StopSeasonsAsync(true)){AddLog(L.Seasons.DisconnectBlocked);return false;}
            ResetRules();
            undoHistory.Clear();   // the records' addresses mean nothing in another game
            map.HideEditors();
            map.HighlightForce = 0;
            history.Clear();
            historyAt = -1;
            game = null;
            if (runtimeRulesWindow is { } rulesWindow)
            {
                runtimeRulesIdentity = null; rulesWindow.SetOffline(true);
                rulesWindow.ShowSettings(null, preserveSelection: true);
            }
            layout = null;
            snapshot = null;
            layoutRecovering = false;
            aiView?.Disconnect();
            checks = [];
            writesAllowed = false;
            map.SetSnapshot(null);
            FillLists();
            if (message is not null)
            {
                status.Text = message;
                AddLog(message);
            }
            return true;
        }
        finally{seasonDisconnecting=false;SetBusy(wasBusy,scanning:false);}
    }

    // The latest snapshot into the lists and the open editor card; what the user typed stays.
    private void MergeSnapshot()
    {
        FillLists();
        RefreshEditor();
    }

    // ---- AUTO(데모) 플레이 --------------------------------------------------------------------------------------

    private static readonly string PlayerFile = Path.Combine(Program.DataFolder, "player.txt");

    // Remembers the player's force (also across runs) and offers [AUTO 중지] while the game is in AUTO(데모) 플레이.
    private void ObservePlayerForce(GameSnapshot data)
    {
        if (data.PlayerForce > 0 && data.Forces.TryGetValue(data.PlayerForce, out ForceInfo info) && lastPlayer != (info.Id, info.RulerId))
        {
            lastPlayer = (info.Id, info.RulerId);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PlayerFile)!);
                File.WriteAllText(PlayerFile, $"{info.Id} {info.RulerId}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Only a default for the picker. */ }
        }
        if (data.IsDemoPlay && !demoNoticed)
        {
            demoNoticed = true;
            AddLog(L.Auto.Detected);
        }
        if (playButton.IsEnabled != (!busy && data.IsDemoPlay)) UpdateControls();
    }

    private async Task ResumePlayAsync()
    {
        if (busy || game is null || layout is null || snapshot is not { IsDemoPlay: true } data) return;
        if (WriteBlock([GameChecks.World]) is { } blocked)
        {
            ReportError(new InvalidOperationException(blocked));
            return;
        }
        GameIdentity running = game.Value;
        GameLayout where = layout;
        if (await Task.Run(() => GameMemory.ReadPace(running, where)) == GamePace.Running)
        {
            ReportError(new InvalidOperationException(L.Auto.PauseFirst));
            return;
        }
        if (ChoosePlayerForce(data) is not int force) return;
        GameIdentity identity = game.Value;
        GameLayout found = layout;
        SetBusy(true, scanning: false);
        try
        {
            AddLog(L.Auto.Request((data.PlayerForce > 0 ? data.ForceName(data.PlayerForce) : L.Terms.None), data.ForceName(force)));
            DemoEnd end = await Task.Run(() => GameMemory.EndDemoPlay(identity, found, force));
            GameSnapshot fresh = await Task.Run(() => GameMemory.ReadSnapshot(identity, found));
            snapshot = fresh;
            map.SetSnapshot(fresh);
            demoNoticed = false;
            ObservePlayerForce(fresh);
            status.Text = fresh.PlayerForce != force || fresh.IsDemoPlay ? L.Auto.StoppedButChanged
                : end.ByGame ? L.Auto.Stopped(fresh.ForceName(force))
                : L.Auto.StoppedValuesOnly(fresh.ForceName(force), end.Why);
            AddLog(status.Text);
            AddLog("  " + L.Auto.DelegationNote);
        }
        catch (Exception ex) { ReportError(ex); }
        finally { SetBusy(false, scanning: false); }
    }

    // Lists the forces with a ruler, biggest first; the current or last seen player force is picked by default.
    private int? ChoosePlayerForce(GameSnapshot data)
    {
        var choices = data.Forces.Values
            .Where(f => f.Id <= GameMemory.MaxPlayerForce && f.RulerId is not (0 or 0xFFFF))
            .Select(f => (Force: f, Cities: data.CitiesOf(f.Id).Count()))
            .OrderByDescending(c => c.Cities).ThenBy(c => c.Force.Id)
            .ToList();
        if (choices.Count == 0)
        {
            Tell(L.Auto.NoForces);
            return null;
        }

        (int Force, int Ruler)? remembered = data.PlayerForce > 0 && data.Forces.TryGetValue(data.PlayerForce, out ForceInfo current)
            ? (current.Id, current.RulerId) : lastPlayer;
        if (remembered is null)
        {
            try
            {
                string[] parts = File.ReadAllText(PlayerFile).Split(' ');
                remembered = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException) { /* No remembered force. */ }
        }
        // The remembered force is this game's under the same ruler. Under another ruler it is either that force
        // with an heir or another save's force of the same number: the user says which.
        int known = remembered is { } last ? choices.FindIndex(c => c.Force.Id == last.Force) : -1;
        if (known >= 0 && remembered is { } before && choices[known].Force.RulerId != before.Ruler &&
            !Confirm(L.Auto.SameForce(before.Force, data.CommanderName(before.Ruler), data.ForceName(before.Force)), title: L.Auto.Title)) known = -1;

        var items = new List<string>();
        for (int i = 0; i < choices.Count; i++)
        {
            string mark = data.PlayerForce == choices[i].Force.Id ? (" " + L.Auto.CurrentPlayer) : i == known ? (" " + L.Auto.LastPlayer) : "";
            items.Add(L.Auto.Choice(data.ForceName(choices[i].Force.Id), choices[i].Cities) + mark);
        }
        return PickFromList(L.Auto.Title, L.Auto.PickForce, L.Auto.PlayThisForce, items, Math.Max(known, 0)) is int picked ? choices[picked].Force.Id : null;
    }

    // A dialog with a list to pick one from (the forces of [AUTO 중지] and [항복시키기]): the index picked, or null.
    private int? PickFromList(string title, string prompt, string pick, IReadOnlyList<string> items, int selected)
    {
        var list = EditorCard.Choice(420);
        list.Margin = new W.Thickness(0, 0, 0, 10);
        foreach (string item in items) list.Items.Add(item);
        list.SelectedIndex = selected;
        var dialog = new W.Window
        {
            Title = title, Owner = this, WindowStartupLocation = W.WindowStartupLocation.CenterOwner, ResizeMode = W.ResizeMode.NoResize,
            ShowInTaskbar = false, SizeToContent = W.SizeToContent.WidthAndHeight, Background = CardSkin.Parchment
        };
        WM.TextOptions.SetTextFormattingMode(dialog, WM.TextFormattingMode.Display);
        var ok = new PlateButton(pick, primary: true) { MinWidth = 140, Margin = new W.Thickness(0, 0, 6, 0) };
        var cancel = new PlateButton(L.Window.Cancel) { MinWidth = 90 };
        ok.Click += () => dialog.DialogResult = true;
        cancel.Click += () => dialog.DialogResult = false;
        // Enter picks, Esc cancels, as the buttons of a dialog.
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (list.IsDropDownOpen) return;
            if (e.Key == WI.Key.Enter) dialog.DialogResult = true;
            else if (e.Key == WI.Key.Escape) dialog.DialogResult = false;
        };
        var buttons = new WC.StackPanel { Orientation = WC.Orientation.Horizontal, Children = { ok, cancel } };
        dialog.Content = new WC.StackPanel
        {
            Margin = new W.Thickness(12),
            Children =
            {
                new WC.TextBlock
                {
                    MaxWidth = 420, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 0, 0, 8), FontFamily = CardSkin.Font, FontSize = CardSkin.FontSize, Foreground = CardSkin.Ink,
                    Text = prompt
                },
                list,
                buttons
            }
        };
        dialog.Loaded += (_, _) => list.Focus();
        return dialog.ShowDialog() == true && list.SelectedIndex >= 0 ? list.SelectedIndex : null;
    }
}

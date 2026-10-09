using W = System.Windows;
using WT = System.Windows.Threading;

namespace San14Tenkazu;

// [AI] in the pane: the right pane's AI view, one at a time. The trees are read from the game's folder when it first
// opens. A number changed in the window is written as any card's values are (the question, GameMemory.Apply,
// [되돌리기]) and kept in the data folder (AiAdjustments); as the game builds its trees anew from its file each time it
// starts, a connection writes the kept numbers again where the game holds the file's values.
internal sealed partial class EditorWindow
{
    private readonly AiAdjustments aiKept;
    private readonly AiExtensionSettingsFile aiExtensionKept;
    // The game may not have built its trees yet right after it started: a connection tries again every few seconds.
    private readonly WT.DispatcherTimer aiRetry = new() { Interval = TimeSpan.FromSeconds(5) };
    private AiView? aiView;
    private AiTree[]? aiTrees;
    private string aiStamp = "";

    private void WireAi()
    {
        map.AiSelected += () => _ = OpenAiAsync();
        aiRetry.Tick += async (_, _) => { if (!busy) await ReapplyAiAsync(); };
    }

    private async Task OpenAiAsync()
    {
        if (busy) return;
        if (aiView is not null)
        {
            aiView.ShowSettings();
            map.ShowAi(aiView);
            if (!aiView.Pending) await aiView.RereadAsync();
            return;
        }
        if (LoadAiTrees() is not { } trees) return;
        AiExtensionSettings extensions;
        try { extensions = aiExtensionKept.Load(aiStamp); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportError(ex);
            return;
        }
        aiView = new AiView(trees, () => game, WriteAiAsync, extensions, WriteAiExtensionsAsync);
        aiView.ExpandedChanged += _ => map.ShowAi(aiView);
        aiView.CloseClicked += () => map.HideAi();
        map.ShowAi(aiView);
    }

    // The game's file of trees, read once; null (and told) when it cannot be read.
    private AiTree[]? LoadAiTrees(bool quiet = false)
    {
        if (aiTrees is not null) return aiTrees;
        try
        {
            string folder = FaceLibrary.FindGameDirectory(game is { } identity ? GameMemory.ExecutablePath(identity) : null)
                ?? throw new DirectoryNotFoundException(AiTreeFile.RelativePath);
            byte[] data = File.ReadAllBytes(Path.Combine(folder, AiTreeFile.RelativePath));
            aiTrees = AiTreeFile.Parse(data);
            aiStamp = AiTreeFile.Stamp(data);
            return aiTrees;
        }
        catch (Exception ex) when (KtglArchive.IsUnreadable(ex))
        {
            if (quiet) AddLog(L.Ai.NoFile(ex.Message));
            else Tell(L.Ai.NoFile(ex.Message), W.MessageBoxImage.Warning);
            return null;
        }
    }

    // A decision's numbers, as raw values, written after the question; then the numbers changed are kept.
    private async Task<bool> WriteAiAsync(string decision, IReadOnlyList<(AiGuideValue Value, int Raw)> requests)
    {
        if (busy || game is not { } identity || aiTrees is not { } trees) return false;
        EditPlan plan;
        try
        {
            plan = await Task.Run(() =>
            {
                AiLiveTree[] live = GameMemory.ReadAiTrees(identity, trees);
                return GameMemory.ReadAi(identity, memory => AiEdits.Plan(memory, trees, live, L.Ai.EditLabel(decision), requests));
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ReportError(ex);
            return false;
        }
        if (!plan.Refused && plan.Edits.Count > 0 && !ConfirmWrite(plan.Summary)) return false;
        if (plan.Refused) { status.Text = plan.Refusal; return false; }
        bool written = plan.Edits.Count == 0 || await WriteAsync([.. plan.Edits]);
        if (written) MergeSnapshot();
        if (written) await KeepAiAsync();
        return written;
    }

    // The numbers the game now holds otherwise than its file, kept for the next time it starts.
    private async Task KeepAiAsync()
    {
        if (game is not { } identity || aiTrees is not { } trees) return;
        try
        {
            Dictionary<string, int> changed = await Task.Run(() =>
            {
                AiLiveTree[] live = GameMemory.ReadAiTrees(identity, trees);
                return GameMemory.ReadAi(identity, memory => AiEdits.Changed(memory, trees, live));
            });
            aiKept.Save(aiStamp, changed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            AddLog(L.Ai.KeepFailed(ex.Message));
        }
    }

    // After a connection: the kept numbers go in again where the game holds its file's values (it started over); a
    // number it holds at a third value is left and told. Tried again while the game has not built its trees yet.
    private async Task ReapplyAiAsync()
    {
        aiRetry.Stop();
        if (game is not { Verified: true } identity || LoadAiTrees(quiet: true) is not { } trees) return;
        Dictionary<string, int> kept = aiKept.Load(aiStamp);
        try { await ReapplyAiExtensionsAsync(identity, trees); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AddLog(L.AiExtensions.Failed(ex.Message));
            // Startup can precede tree construction; the ordinary AI path uses the same retry timer.
            if (ex is InvalidDataException && !ex.Message.Contains(L.AiExtensions.UnsupportedTree, StringComparison.Ordinal)
                && !ex.Message.Contains(L.AiExtensions.ForeignNode, StringComparison.Ordinal)
                && !ex.Message.Contains(L.AiExtensions.InvalidSettings, StringComparison.Ordinal)) aiRetry.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AddLog(L.AiExtensions.Failed(ex.Message)); }
        if (kept.Count == 0) return;
        (List<(AiGuideValue Value, int Raw)> Write, List<(AiGuideValue Value, int? Now, int Kept)> Other) decided;
        try
        {
            decided = await Task.Run(() =>
            {
                AiLiveTree[] live = GameMemory.ReadAiTrees(identity, trees);
                return GameMemory.ReadAi(identity, memory => AiEdits.Reapply(memory, trees, live, kept));
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            aiRetry.Start();
            return;
        }
        foreach ((AiGuideValue value, int? now, int raw) in decided.Other)
            AddLog(L.Ai.ReapplyOther(value.Label, now is int n ? AiText.Number(value.Shown(n)) : L.AiGuide.Differs, AiText.Number(value.Shown(raw))));
        if (decided.Write.Count == 0) return;
        AddLog(L.Ai.ReapplyWrote(decided.Write.Count));
        EditPlan plan;
        try
        {
            plan = await Task.Run(() =>
            {
                AiLiveTree[] live = GameMemory.ReadAiTrees(identity, trees);
                return GameMemory.ReadAi(identity, memory => AiEdits.Plan(memory, trees, live, L.Ai.EditLabel(L.Ai.Title), decided.Write));
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            aiRetry.Start();
            return;
        }
        if (plan.Refused) AddLog(plan.Refusal);
        else if (plan.Edits.Count > 0) await WriteAsync([.. plan.Edits]);
    }

    private static bool IsAi(RecordEdit edit) => edit.Kind is RecordKind.AiNode or RecordKind.AiArgs;

    private async Task<bool> WriteAiExtensionsAsync(AiExtensionSettings settings)
    {
        if (busy || game is not { Verified: true } identity || aiTrees is not { } trees) return false;
        SetBusy(true, scanning: false);
        AiExtensionSettings? before = null;
        try
        {
            before = await Task.Run(() => GameMemory.ReadAiExtensions(identity, trees).Settings);
            AiExtensionStatus result = await Task.Run(() => GameMemory.ApplyAiExtensions(identity, trees, settings));
            aiExtensionKept.Save(aiStamp, settings);
            AddLog(L.AiExtensions.Applied);
            AddLog(L.AiExtensions.Counts(result.Checks, result.RecoveryPlans, result.Returns, result.Holds));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (before is not null)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        if (GameMemory.ReadAiExtensions(identity, trees).Settings != before)
                            GameMemory.ApplyAiExtensions(identity, trees, before);
                    });
                }
                catch (Exception restore) when (restore is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { AddLog(L.AiExtensions.Failed(restore.Message)); }
            }
            ReportError(ex);
            return false;
        }
        finally { SetBusy(false, scanning: false); }
    }

    private async Task ReapplyAiExtensionsAsync(GameIdentity identity, AiTree[] trees)
    {
        AiExtensionSettings settings = aiExtensionKept.Load(aiStamp);
        AiExtensionStatus current = await Task.Run(() => GameMemory.ReadAiExtensions(identity, trees));
        if (current.Settings == settings) return;
        SetBusy(true, scanning: false);
        try
        {
            AiExtensionStatus result = await Task.Run(() => GameMemory.ApplyAiExtensions(identity, trees, settings));
            AddLog(result.Settings.SupplyProtection ? L.AiExtensions.On : L.AiExtensions.Off);
        }
        finally { SetBusy(false, scanning: false); }
    }
}

using San14Tenkazu;
using San14Tenkazu.Tests;
using WC = System.Windows.Controls;

internal static partial class RenderChecks
{
    // Exercise the production connection lifecycle with offline snapshots: no game process is opened.
    private static void CheckSessionTransitions(EditorWindow editor, GameIdentity identity,
        Action<bool, string> check, Action<Task> awaitTask)
    {
        static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
        static void Set(object target, string name, object? value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
        static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
        bool Pending() => (bool)typeof(EditorWindow).GetProperty("HasPendingEdits", Hidden)!.GetValue(editor)!;
        var sessionLayout = identity.DataProfile?.Session ?? GameSessionReader.Original;
        GameSessionEntry[] parents =
        [
            new(0x25F1D1A94E0, sessionLayout.Root, "CRootState", 0),
            new(0x25F1DA27E70, sessionLayout.Motor, "CMotorGameState", 0),
            new(0x25F2F65FBA0, sessionLayout.Game, "CGameState", 0)
        ];
        // Addresses, pending count and child state below reproduce editor-20261113.log at 06:00:51.
        var child = new GameSessionEntry(0x25F338E4E40, 0x13D9700, "CProgressState", 0);
        var playing = new GameSessionObservation(GameSessionPhase.Playing, 0x25EF645D160, 0, [.. parents, child]);
        var transition = playing with { Phase = GameSessionPhase.Transition, Pending = 1, Entries = [.. parents, child with { Leaving = 1 }] };
        var data = new TestGame().Force(1, 1, 1).City(1, 1).Officer(1, "군주", 1, 1, 1).Build(playerForce: 0);
        typeof(GameSnapshot).GetProperty("Session", Hidden)!.SetValue(data, playing);
        var tables = new GameTables(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18);
        var layout = new GameLayout(tables, 1, 2, 2, 3, 1, 4, 5, 1, [], [], 6, 0, 7);
        Set(editor, "game", identity);
        Set(editor, "layout", layout);
        Set(editor, "snapshot", data);
        Set(editor, "writesAllowed", true);
        Set(editor, "sessionDataUnavailable", false);
        var card = Field<CityEditorCard>(editor, "cityEditor");
        Call(editor, "Show", new OfficerTarget(1));
        Call(editor, "Show", new CityTarget(1));
        var gold = Field<Dictionary<FieldDef, NumberField>>(card, "fields")[CityFields.Gold];
        check(!Pending() && card.Pending == 0, "untouched-visible-and-hidden-cards-have-no-pending-edits");

        foreach (bool draft in new[] { false, true })
        {
            if (draft) gold.Set(gold.Current + 100);
            long wanted = gold.Value;
            for (int poll = 0; poll < 3; poll++)
            {
                awaitTask((Task)Call(editor, "HandleSessionUnavailableAsync", identity, transition)!);
                awaitTask((Task)Call(editor, "PollAutomaticGameAsync")!);
                check(Field<GameIdentity?>(editor, "game") == identity && ReferenceEquals(Field<GameSnapshot>(editor, "snapshot"), data)
                    && ReferenceEquals(Field<GameLayout>(editor, "layout"), layout) && card.Shown,
                    $"child-transition-keeps-connection-and-card-draft-{draft}-poll-{poll}");
                check(Field<bool>(editor, "layoutRecovering") && !Field<PlateButton>(editor, "playButton").IsEnabled
                    && Call(editor, "FieldWriteBlock", (object)new[] { CityFields.Gold }) is string && Pending() == draft,
                    $"child-transition-blocks-writes-without-inventing-edits-{draft}-poll-{poll}");
            }
            check(!(bool)Call(editor, "AcceptLiveSnapshot", identity, layout, data)!
                && !Field<bool>(editor, "layoutRecovering") && Field<PlateButton>(editor, "playButton").IsEnabled
                && gold.Value == wanted && Pending() == draft
                && Field<WC.TextBlock>(editor, "status").Text != L.Connection.SessionWaiting,
                $"stable-snapshot-resumes-same-connection-and-retains-input-{draft}");
        }
        Call(card, "Revert");
        check(!Pending(), "clearing-real-draft-restores-no-pending-state");
        gold.Set(gold.Current + 100);
        awaitTask((Task)Call(editor, "FinishLeavingAsync", card, (object?)null)!);
        check(gold.IsChanged && Pending(), "cancel-leaving-retains-current-card-input");
        awaitTask((Task)Call(editor, "FinishLeavingAsync", card, false)!);
        check(!Pending() && card.Pending == 0, "discard-leaving-clears-card-input-and-cache");
        // [소속 주기]/[독립]/[항복시키기] ask to discard, then ask about the operation itself.
        gold.Set(gold.Current + 100);
        bool cancelled = !(bool)Call(editor, "FinishDiscard", true, true, (Func<bool>)(() => false))!;
        check(cancelled && gold.IsChanged && Pending(), "cancelled-operation-after-discard-approval-keeps-input");
        bool proceeded = (bool)Call(editor, "FinishDiscard", true, true, (Func<bool>)(() => true))!;
        check(proceeded && !gold.IsChanged && !Pending(), "confirmed-operation-drops-input");
        // LeaveAsync's discard branch used to leave the old card's cached values in memory.
        gold.Set(gold.Current + 100);
        Call(editor, "Show", new OfficerTarget(1));
        var load = transition with { Entries = [.. parents, new(0x90000000, 0, "CLoadState", 0)] };
        awaitTask((Task)Call(editor, "HandleSessionUnavailableAsync", identity, load)!);
        bool phantomPending = Pending();
        Call(card, "Revert"); // Always clear the fixture before reporting failure, so teardown never asks about discarding it.
        check(Field<GameIdentity?>(editor, "game") is null && Field<GameSnapshot?>(editor, "snapshot") is null && !phantomPending,
            "actual-load-ignores-abandoned-card-cache");

        // Live reads can recover without a reconnect when disconnecting was blocked; the flag stays set.
        Set(editor, "game", identity);
        Set(editor, "layout", layout);
        Call(editor, "AcceptLiveSnapshot", identity, layout, data);
        Call(editor, "Show", new CityTarget(1));
        gold.Set(gold.Current + 100);
        awaitTask((Task)Call(editor, "HandleSessionUnavailableAsync", identity, load)!);
        bool tracked = ReferenceEquals(Field<EditorCard?>(editor, "disconnectedEditor"), card) && Pending();
        Call(card, "Revert");
        check(Field<bool>(editor, "sessionDataUnavailable") && tracked && !Pending(),
            "card-opened-after-recovery-stays-tracked-at-next-transition");
    }
}

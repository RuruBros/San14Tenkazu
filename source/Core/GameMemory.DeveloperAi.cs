#if LOCAL_DEVELOPER
namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal static int RoundTripProfiledAi(GameIdentity identity, GameLayout layout, GameSnapshot expected, Action<object> record)
    {
        void RequireSameSession(GameSnapshot data)
        {
            if (expected.Session is not { IsPlaying: true, Entries.Length: >= 3 } session ||
                data.Session is not { IsPlaying: true, Entries.Length: >= 3 } current ||
                current.Entries[2].Address != session.Entries[2].Address ||
                data.Date is null || data.Date != expected.Date || data.Hour != expected.Hour ||
                data.PlayerForce <= 0 || data.PlayerForce != expected.PlayerForce)
                throw new InvalidOperationException(L.Errors.StateMoved);
        }

        RequireSameSession(ReadSnapshot(identity, layout));
        if (ReadPace(identity, layout) != GamePace.Paused) throw new InvalidOperationException(L.DeveloperUi.PauseRequired);
        var profile = LoadProfiledAi(identity);
        var live = ReadAiTrees(identity, profile.Trees, AiBuildProfile.NovemberStock);
        var plans = ReadAi(identity, memory => profile.Values.Select(value =>
        {
            if (AiEdits.GameRaw(memory, profile.Trees, live, value) is not int before ||
                AiEdits.FileRaw(profile.Trees, value) is not int stock || before < 0 || before > AiEdits.MaxRaw(stock))
                throw new InvalidDataException(L.Ai.ValueNotFound(value.Label));
            int requested = before == AiEdits.MaxRaw(stock) ? before - 1 : before + 1;
            var plan = AiEdits.Plan(memory, profile.Trees, live, value.Label, [(value, requested)]);
            if (plan.Refused || plan.Edits.Sum(edit => edit.Changes.Count) != AiEdits.Slots(profile.Trees, value).Count)
                throw new InvalidDataException(plan.Refused ? plan.Refusal : L.Ai.TreeChanged);
            return (Key: AiAdjustments.Key(value), value.Label, Before: before, Requested: requested, plan.Edits);
        }).ToArray());
        record(new { stage = "started", identity.ProcessId, identity.StartTimeUtc, identity.ExeHash, expected.Date,
            expected.Hour, expected.PlayerForce, values = plans.Length });
        foreach (var trial in plans)
        {
            record(new { stage = "planned", trial.Key, trial.Label, trial.Before, trial.Requested,
                records = trial.Edits.Select(edit => new { edit.Address, sha256 = Hash(edit.Expected) }).ToArray() });
            var result = WithProfiledEditSession(identity, layout, (handle, data) =>
            {
                RequireSameSession(data);
                var memory = new ProcessMemory(handle);
                return RunProfiledAiTrial(memory, trial.Edits, edits =>
                    ApplyProfiledAiTransaction(handle, identity, layout, data, profile, edits));
            });
            record(new { stage = "changed-readback", trial.Key, trial.Requested, records = result.Changed });
            record(new { stage = "restored-byte-exact", trial.Key, trial.Before, records = result.Restored });
        }
        record(new { stage = "all-restored", passed = plans.Length });
        return plans.Length;
    }

    internal sealed record AiTrialBytes(ulong Address, string Sha256);

    internal static (AiTrialBytes[] Changed, AiTrialBytes[] Restored) RunProfiledAiTrial(IRecordMemory memory,
        IReadOnlyList<RecordEdit> edits, Action<IReadOnlyList<RecordEdit>> apply)
    {
        foreach (var edit in edits)
            if (!memory.Read(edit.Address, edit.Expected.Length).AsSpan().SequenceEqual(edit.Expected))
                throw new InvalidOperationException(L.Ai.TreeChanged);
        var undo = Undo.Of(edits);
        AiTrialBytes[] Check(IReadOnlyList<RecordEdit> expected) => expected.Select(edit =>
        {
            byte[] read = memory.Read(edit.Address, edit.Expected.Length);
            if (!read.AsSpan().SequenceEqual(edit.Expected)) throw new IOException(L.Ai.TreeChanged);
            return new AiTrialBytes(edit.Address, Hash(read));
        }).ToArray();

        apply(edits);
        AiTrialBytes[] changed;
        AiTrialBytes[] restored = [];
        try { changed = Check(undo); }
        finally
        {
            apply(undo);
            restored = Check(edits);
        }
        return (changed, restored);
    }
}
#endif

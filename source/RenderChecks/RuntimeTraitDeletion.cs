using System.IO;
using San14Tenkazu;
using San14Tenkazu.Tests;

internal static partial class RenderChecks
{
    // Reproduce the review finding with the real officer edit/rollback and save-recovery plans.
    // Only fake record memory and isolated settings are used; no process or user save is opened.
    private static void CheckRuntimeTraitDeletion(string root, List<string> checks, Action<Task> awaitTask)
    {
        string storage = Path.Combine(root, "intentional-trait-deletion");
        var settings = new RuntimeRuleSettings(true, AbilityRuleMode.Original);
        var builder = new TestGame().Officer(2, "edited", 0, 1, 5);
        for (int i = 0; i < 6; i++) builder.With(2, OfficerFields.Personalities[i], i + 1);
        GameSnapshot data = builder.Build(settings);
        OfficerRecord officer = data.OfficerById[2];
        var memory = new FakeMemory(data);
        var identity = new GameIdentity(444, DateTime.UtcNow, 0x140000000, "fixture", true, TestGame.Classes);
        var layout = new GameLayout(new(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0),0,0,0,
            officer.Address - GameMemory.OfficerStride,2,0,0,0,[],[],0,0,0x1000);
        var file = new RuntimeRulePreferenceFile(Path.Combine(storage, "runtime-rules"));
        file.Save(new(1, identity.ExeHash, settings, true));
        string receipt = file.ReceiptPath(identity);
        SaveRestoreEdits History() => new(SaveRestoreEdits.PathFor(receipt), identity, layout);
        byte[] stream = new byte[GameSaveFile.RecordSize];
        foreach (var (at, offset, length) in GameSaveFile.Segments) officer.Data.AsSpan(offset, length).CopyTo(stream.AsSpan(at));
        var saved = new SaveOfficer(1, 2, officer.Data.AsSpan(SaveRestore.AbilityOffset, 5).ToArray(), new int[5], [1,2,3,4,5,6,0,0,0], stream);
        int automaticWrites = 0, traitWrites = 0;
        var evidence = new List<object>();
        void Check(bool value, string name)
        {
            var traits = OfficerFields.Personalities.Select(f => f.Read(memory[officer.Address]!)).ToArray();
            evidence.Add(new { scenario = name, passed = value, traits, automaticWrites, traitWrites, gameAccess = false });
            File.WriteAllText(Path.Combine(storage, "deletion-evidence.json"), System.Text.Json.JsonSerializer.Serialize(evidence));
            if (!value) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        RuntimeRuleWrite[] Plan(string path)
        {
            var history = new SaveRestoreEdits(SaveRestoreEdits.PathFor(path), identity, layout);
            history.Observe(address => memory[address]);
            byte[] record = memory[officer.Address]!;
            return SaveRestore.Plan(record, officer.Address, saved, settings, history.PreservesTraits(officer.Address, record));
        }
        var access = new RuntimeRuleGameAccess(_ => settings, (_, _) => GamePace.Paused,
            (_, target, path, _, _) =>
            {
                foreach (var write in Plan(path)) { memory.Write(write.Address, write.After); if (SaveRestore.IsTraitRestore(write)) traitWrites++; }
                automaticWrites++; return new(layout, new TestGame().Build(target), 0);
            }, (_, _, _, path) => { Check(path == receipt, "trait-deletion-check-uses-current-process-receipt"); return Plan(path).Length > 0; });
        var editor = OfflineEditor(() => false, storage, storage, storage, runtimeRuleAccess: access);
        void Set(string field, object? value) => typeof(EditorWindow).GetField(field, Hidden)!.SetValue(editor, value);
        void Setup() { Set("game", identity); Set("layout", layout); Set("snapshot", new TestGame().Build(settings)); }
        void Close() { Set("game", null); Set("snapshot", null); editor.Close(); }
        void Poll(bool retry = false) => awaitTask((Task)typeof(EditorWindow).GetMethod("ReapplyRuntimeRulesAsync", Hidden)!.Invoke(editor, [retry])!);
        void Apply(IReadOnlyList<RecordEdit> edits)
        {
            var history = History();
            GameMemory.Apply(memory, TestGame.Classes, edits, () => history.Record(memory, edits));
        }
        bool Empty() => OfficerFields.Personalities.All(f => f.Read(memory[officer.Address]!) == 0);
        try
        {
            Setup(); Poll(); Check(automaticWrites == 0, "trait-deletion-initial-match-does-not-write");
            var deletion = OfficerEdits.Plan(data, officer.Id, new(OfficerFields.Personalities.Select(f => new FieldChange(f, 0)).ToList(), null, null, []));
            if (deletion.Refused) throw new InvalidOperationException(deletion.Refusal);
            Apply(deletion.Edits);
            Check(Empty(), "trait-deletion-real-user-plan-clears-all-nine-slots");
            for (int i = 0; i < 3; i++) { Setup(); Poll(); }
            Check(Empty() && automaticWrites == 0, "trait-deletion-repeated-refresh-preserves-explicit-user-deletion");
            Poll(true); Check(Empty() && automaticWrites == 0, "trait-deletion-explicit-retry-preserves-deletion");
            Close(); editor = OfflineEditor(() => false, storage, storage, storage, runtimeRuleAccess: access);
            Setup(); Poll(true);
            Check(Empty() && automaticWrites == 0, "trait-deletion-editor-restart-and-reconnect-preserve-deletion");
            access.Apply(identity, settings, receipt, true, false);
            Check(Empty() && traitWrites == 0, "trait-deletion-actual-apply-plan-also-preserves-deletion");
            Apply(Undo.Of(deletion.Edits));
            Check(!Empty(), "trait-deletion-undo-restores-traits-and-retires-evidence");
            foreach (var field in OfficerFields.Personalities) memory.Set(officer.Address, field, 0);
            Poll(); Check(!Empty() && traitWrites == 1, "trait-deletion-later-same-storage-loader-loss-is-still-recovered");
            Apply(deletion.Edits); Poll(); Check(Empty() && traitWrites == 1, "trait-deletion-after-recovery-is-preserved");
            long loyalty = OfficerFields.Loyalty.Read(memory[officer.Address]!);
            memory.Set(officer.Address, OfficerFields.Loyalty, loyalty + 1); Poll();
            memory.Set(officer.Address, OfficerFields.Loyalty, loyalty); Poll();
            Check(!Empty() && traitWrites == 2, "trait-deletion-observed-different-world-state-retires-old-evidence");
        }
        finally { Close(); }
    }
}

#if LOCAL_DEVELOPER
using System.IO;
using San14Tenkazu;
using San14Tenkazu.Tests;

internal static partial class RenderChecks
{
    private static void CheckProfiledRuleRoundTrip()
    {
        var memory = new PlainRuleFixture(DoctrineRules.Spec);
        var formation = memory.Offices.Policies.Tactics.Formations;
        var doctrine = memory.Snapshot().Records[0];
        // Make activation require a cost write. Other records retain an enabled cost so the
        // diagnostic can also exercise a positive cost decrement.
        memory.Set(doctrine.Address + 0x3D, [0, 0]);
        var trials = GameMemory.PlanProfiledRuleTrials(memory.Identity, (_, plan) => plan(memory));
        Require(trials.Length == 50 && trials.Select(t => (t.Key.Table, t.Key.Field)).Distinct().Count() == 50 &&
            trials.Select(t => t.Key.Table).Distinct().Count() == 7 && memory.Writes == 0, "Rule diagnostic plan must cover all fields before writing");

        foreach (var trial in trials)
        {
            var result = trial.Run(memory);
            Require(trial.Before.SameBytes(result.Restored) && !trial.Before.SameBytes(result.Changed) &&
                trial.Changes.All(c => result.Changed.Value(c.Key.Field) == c.After), "Rule diagnostic failed readback or exact restoration");
        }
        Require(trials.Single(t => t.Key is { Table: "doctrine", Field: "kind1" }).Changes.Length == 2,
            "Rule diagnostic must activate a doctrine kind and its cost together");

        var first = trials[0];
        var row = first.Before;
        int writes = memory.Writes;
        formation.Set(row.Address + 0x80, [0xEF]); // Drift outside the selected field also invalidates the trial.
        try { first.Run(memory); throw new IOException("The rule diagnostic accepted a stale target"); }
        catch (InvalidOperationException) { Require(writes == memory.Writes, "A stale rule record must receive zero writes"); }
        formation.Set(row.Address, row.Data);

        memory.FailWrite = memory.Writes + 2;
        try { first.Run(memory); throw new InvalidOperationException("The rule diagnostic accepted a partial vector write"); }
        catch (IOException)
        {
            Require(memory.Read(row.Address, row.Data.Length).SequenceEqual(row.Data) &&
                memory.Read(row.VectorAddress, row.Vector.Length).SequenceEqual(row.Vector), "A partial write must restore record and vector");
        }
        Console.WriteLine("PASS rule diagnostic 50 fields / 7 tables, linked costs, stale target, partial write rollback; gameAccess=false");
    }
}
#endif

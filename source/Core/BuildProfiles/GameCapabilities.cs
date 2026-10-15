using System.Collections.Immutable;

namespace San14Tenkazu;

internal enum GameFeature { BasicRead, DataWrite, AbilityData, RuntimeTraits, RuntimeAbilities, TraitList, RuleTables, Tactics, Policies, Offices, Ranks, Doctrines, GameCalls, AiTrees, Environment, Seasons }
internal enum CapabilityState { Eligible, Blocked, Passed }
internal sealed record CapabilityEvidence(string Stage, bool Passed, string Detail);
internal sealed record GameCapability(GameFeature Feature, CapabilityState State, ImmutableArray<CapabilityEvidence> Evidence)
{
    internal bool CanAttempt => State != CapabilityState.Blocked;
}

// Eligibility never certifies live pointers, engine bytes, ownership, or page protection.
// Those guards remain at every existing operation, even after a diagnostic has passed.
internal static class GameCapabilities
{
    internal static GameCapability Inspect(GameIdentity identity, GameFeature feature)
    {
        var evidence = ImmutableArray.CreateBuilder<CapabilityEvidence>();
        bool known = identity.Verified && GameBuildProfiles.Find(identity.ExeHash) is not null;
        bool native = feature is not (GameFeature.BasicRead or GameFeature.DataWrite);
        if (native) evidence.Add(new("profile", known, known ? "registered EXE/layout/ABI; live guards still required" : "unregistered or unverified build"));
        var c = identity.Classes;
        (string, ulong)[] required = feature switch
        {
            GameFeature.BasicRead or GameFeature.DataWrite => [("city", c.City), ("county", c.County), ("officer", c.Officer), ("tile", c.Tile), ("building", c.Building), ("force", c.Force), ("corps", c.Corps)],
            GameFeature.AbilityData or GameFeature.RuntimeTraits or GameFeature.RuntimeAbilities or GameFeature.TraitList => [("officer", c.Officer), ("world", c.World)],
            GameFeature.RuleTables => [("world", c.World), ("formation", c.Formation)],
            GameFeature.Tactics => [("world", c.World), ("tactic", c.Tactic)],
            GameFeature.Policies => [("world", c.World), ("policy", c.Policy), ("policy-effect", c.PolicyEffect)],
            GameFeature.Offices => [("world", c.World), ("office", c.Post)],
            GameFeature.Ranks => [("world", c.World), ("rank", c.Rank)],
            GameFeature.Doctrines => [("world", c.World), ("doctrine", c.Doctrine), ("doctrine-panel", c.DoctrinePanel)],
            GameFeature.Environment => [("world", c.World), ("city", c.City), ("county", c.County), ("tile", c.Tile), ("unit", c.Unit)],
            GameFeature.Seasons or GameFeature.GameCalls => [("world", c.World)],
            GameFeature.AiTrees => [("officer", c.Officer), ("force", c.Force), ("corps", c.Corps), ("world", c.World)],
            _ => throw new ArgumentOutOfRangeException(nameof(feature))
        };
        foreach (var (name, address) in required) evidence.Add(new("class", address != 0, name));
        if (!native) evidence.Add(new("layout", true, known ? "registered layout; current storage checks required" : "RTTI/table discovery only; field self-checks and explicit write permission required"));
        return new(feature, evidence.All(e => e.Passed) ? CapabilityState.Eligible : CapabilityState.Blocked, evidence.ToImmutable());
    }
    internal static ImmutableArray<GameCapability> Inspect(GameIdentity identity) => Enum.GetValues<GameFeature>().Select(f => Inspect(identity, f)).ToImmutableArray();
    internal static void Require(GameIdentity identity, GameFeature feature)
    {
        var capability = Inspect(identity, feature);
        if (!capability.CanAttempt) throw new InvalidDataException($"{L.Compatibility.Unavailable}: {feature} ({string.Join(", ", capability.Evidence.Where(e => !e.Passed).Select(e => e.Detail))})");
    }
    internal static GameCapability Record(GameCapability eligibility, params CapabilityEvidence[] runtime)
    {
        if (runtime.Select(e => e.Stage).Distinct().Count() != runtime.Length) throw new InvalidDataException("Duplicate capability evidence stage");
        var all = eligibility.Evidence.AddRange(runtime);
        string[] required = eligibility.Feature switch
        {
            GameFeature.BasicRead => ["storage"],
            GameFeature.DataWrite => ["storage", "field-checks", "write-permission"],
            GameFeature.AbilityData => ["storage", "growth-curve"],
            GameFeature.RuntimeTraits or GameFeature.RuntimeAbilities => ["storage", "pause", "runtime-code", "ownership"],
            GameFeature.TraitList => ["storage", "pause", "runtime-code", "trait-list", "ownership"],
            GameFeature.Environment or GameFeature.Seasons => ["storage", "engine-code", "engine-protection", "bundle", "native-session"],
            _ => ["storage", "operation-guards"]
        };
        return eligibility with { Evidence = all, State = all.Any(e => !e.Passed) ? CapabilityState.Blocked
            : required.All(stage => runtime.Any(e => e.Stage == stage && e.Passed)) ? CapabilityState.Passed : CapabilityState.Eligible };
    }
}

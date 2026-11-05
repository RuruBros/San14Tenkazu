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
        bool abilityData = feature == GameFeature.AbilityData && identity.DataProfile?.AbilityTable is not null;
        bool ruleData = GameRuleTableProfile.Includes(feature) && identity.DataProfile is { RuleGuards.IsDefaultOrEmpty: false };
        bool renderer = feature is GameFeature.Seasons or GameFeature.Environment && GameBuildProfiles.FindNative(identity) is not null;
        bool ai = feature == GameFeature.AiTrees && AiBuildProfile.IsNovember(identity);
        bool calls = feature == GameFeature.GameCalls && GameCommandProfile.Find(identity) is not null;
        bool runtime = feature is GameFeature.RuntimeAbilities or GameFeature.RuntimeTraits or GameFeature.TraitList &&
            RuntimeRuleBuildProfile.Find(identity) is not null;
        if (native) evidence.Add(new("profile", known || abilityData || ruleData || renderer || ai || runtime || calls, abilityData
            ? "registered original ability table and code; live growth curve required"
            : ruleData ? "registered rule layouts and code; live table and codec checks required"
            : ai ? "registered AI manager and stock tree; live node and parameter checks required"
            : runtime ? "registered runtime patch and trait-list ABI; live bytes and ownership required"
            : calls ? "registered per-command ABI; live frame, code and result checks required"
            : known || renderer ? "registered EXE/layout/ABI; live guards still required" : "unregistered or unverified build"));
        if (feature == GameFeature.DataWrite && identity.DataProfile is { } dataProfile)
            evidence.Add(new("profile", !dataProfile.Fields.IsEmpty, "registered fields only; exact code and live storage checks required"));
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
            GameFeature.Environment => [("world", c.World), ("city", c.City), ("county", c.County), ("tile", c.Tile), ("unit", c.Unit), ("building", c.Building)],
            GameFeature.Seasons or GameFeature.GameCalls => [("world", c.World)],
            GameFeature.AiTrees => [("officer", c.Officer), ("force", c.Force), ("corps", c.Corps), ("world", c.World)],
            _ => throw new ArgumentOutOfRangeException(nameof(feature))
        };
        foreach (var (name, address) in required) evidence.Add(new("class", address != 0, name));
        if (!native) evidence.Add(new("layout", true, known || identity.DataProfile is not null
            ? "registered layout; current storage checks required" : "RTTI/table discovery only; field self-checks and explicit write permission required"));
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

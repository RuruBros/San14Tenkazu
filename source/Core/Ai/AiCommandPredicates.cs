namespace San14Tenkazu;

// Predicate composition only. Gate and candidate-list helpers still have their own command rules.
// Reverse/ai_command_predicates.py runs those helpers unchanged and observes their inputs and outputs.
internal static class AiCommandPredicates
{
    internal enum Source { District, MainDistrict, AgentDistrictOrMain, Force, DistrictForce, Foothold }
    internal readonly record struct Plan(Source Input, int Gate, int Candidates, int ListArgument = 1,
        bool RequiresSecondObject = false, bool InspectPermission = false, bool FilterCandidates = false,
        bool CandidateFromSecondDistrict = false);

    internal static Plan GetPlan(int predicate) => predicate switch
    {
        44 => new(Source.District, 0x709CF0, 0),
        46 => new(Source.District, 0, 0x6DD5E0, RequiresSecondObject: true),
        52 => new(Source.District, 0, 0x6DA8A0, RequiresSecondObject: true),
        57 => new(Source.MainDistrict, 0x70BEE0, 0),
        58 => new(Source.MainDistrict, 0x70BEE0, 0x6DE910, RequiresSecondObject: true),
        66 => new(Source.MainDistrict, 0x70B810, 0),
        67 => new(Source.MainDistrict, 0x70B810, 0x6DE160, RequiresSecondObject: true),
        68 => new(Source.Foothold, 0x70DC70, 0),
        69 => new(Source.Foothold, 0x70DC70, 0x6D7EA0, RequiresSecondObject: true),
        70 => new(Source.AgentDistrictOrMain, 0x70ACF0, 0),
        71 => new(Source.AgentDistrictOrMain, 0x70ACF0, 0x6DAA10, RequiresSecondObject: true),
        72 => new(Source.District, 0x70DF40, 0, RequiresSecondObject: true),
        73 => new(Source.District, 0x70DF40, 0x6D8350, RequiresSecondObject: true, CandidateFromSecondDistrict: true),
        74 => new(Source.MainDistrict, 0x709E30, 0),
        75 => new(Source.MainDistrict, 0x709E30, 0x6D7AA0, RequiresSecondObject: true),
        76 => new(Source.Force, 0x70A0D0, 0),
        77 => new(Source.Force, 0x70A0D0, 0x6DBF00, RequiresSecondObject: true),
        79 => new(Source.AgentDistrictOrMain, 0x70BBF0, 0),
        80 => new(Source.AgentDistrictOrMain, 0x70BBF0, 0x6DAF20, RequiresSecondObject: true),
        81 => new(Source.AgentDistrictOrMain, 0x70BBF0, 0x6DE4C0, 2, true),
        85 => new(Source.MainDistrict, 0x70C530, 0),
        86 => new(Source.MainDistrict, 0x70C530, 0x6DF9B0, RequiresSecondObject: true),
        88 => new(Source.AgentDistrictOrMain, 0x70CC00, 0),
        90 or 93 or 97 or 101 => new(Source.AgentDistrictOrMain, 0, 0x6DAF20, RequiresSecondObject: true),
        92 => new(Source.AgentDistrictOrMain, 0x70C8D0, 0),
        94 => new(Source.AgentDistrictOrMain, 0, 0x6E0210, RequiresSecondObject: true),
        96 => new(Source.AgentDistrictOrMain, 0x70C0B0, 0),
        98 => new(Source.AgentDistrictOrMain, 0, 0x6DEB30, RequiresSecondObject: true),
        100 => new(Source.AgentDistrictOrMain, 0x70C1E0, 0),
        102 => new(Source.AgentDistrictOrMain, 0, 0x6DECA0, RequiresSecondObject: true),
        104 => new(Source.MainDistrict, 0x70C2F0, 0),
        105 => new(Source.MainDistrict, 0, 0x6DF3F0, RequiresSecondObject: true),
        106 => new(Source.MainDistrict, 0x70B9E0, 0),
        107 => new(Source.MainDistrict, 0, 0x6DE310, RequiresSecondObject: true),
        136 => new(Source.MainDistrict, 0x70CF00, 0),
        142 => new(Source.MainDistrict, 0x70CD30, 0),
        143 => new(Source.MainDistrict, 0, 0x6E0720, RequiresSecondObject: true),
        144 => new(Source.AgentDistrictOrMain, 0x70BDD0, 0),
        151 => new(Source.District, 0, 0x6D8670, RequiresSecondObject: true),
        152 => new(Source.District, 0, 0x6D6440, RequiresSecondObject: true),
        153 => new(Source.Force, 0x70AE70, 0),
        154 => new(Source.MainDistrict, 0, 0x6DD920, RequiresSecondObject: true),
        155 => new(Source.MainDistrict, 0, 0x6DABE0, RequiresSecondObject: true, FilterCandidates: true),
        156 => new(Source.MainDistrict, 0, 0x6D7650, RequiresSecondObject: true),
        158 => new(Source.DistrictForce, 0x70D930, 0),
        165 => new(Source.District, 0x70C7D0, 0),
        166 => new(Source.District, 0x70B390, 0, InspectPermission: true),
        _ => throw new ArgumentOutOfRangeException(nameof(predicate)),
    };

    internal static long ResolveSource(Plan plan, long first, long mainDistrict, long districtForce,
        long agentDistrict, bool validAgentDistrict) => plan.Input switch
        {
            Source.MainDistrict => mainDistrict,
            Source.AgentDistrictOrMain => validAgentDistrict ? agentDistrict : mainDistrict,
            Source.DistrictForce => districtForce,
            _ => first,
        };

    internal static bool Evaluate(Plan plan, bool validFirst, bool validSecond, bool permission,
        bool gatePassed, bool candidatePresent) => validFirst && (!plan.RequiresSecondObject || validSecond)
        && (!plan.InspectPermission || permission) && (plan.Gate == 0 || gatePassed)
        && (plan.Candidates == 0 || candidatePresent);

    // P91/95/99: player-owned target accepts native outcome 1 or 2; other targets require 2.
    internal static bool SchemeLikely(int outcome, bool targetIsPlayer) =>
        targetIsPlayer ? outcome is 1 or 2 : outcome == 2;

    // P157, after actual candidate membership; equality at the old level is excluded.
    internal static bool NextSceneryLevel(short oldLevel, short nextLevel, int maximumIncrement) =>
        oldLevel < nextLevel && nextLevel <= unchecked(oldLevel + maximumIncrement);
}

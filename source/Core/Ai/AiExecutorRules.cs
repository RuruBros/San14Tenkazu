namespace San14Tenkazu;

internal enum AiTargetKind { None, Base, CautionTroops, Area, Building, Hex }

// Numeric wrapper effects only; native command generation and routing are not reimplemented here.
internal static class AiExecutorRules
{
    internal static AiTargetKind TargetKind(ushort purpose) => purpose switch
    {
        0 or 2 or 8 or 9 or 10 or 11 or 12 or 17 or 18 or 22 => AiTargetKind.Base,
        1 or 19 => AiTargetKind.CautionTroops,
        3 or 13 or 14 or 15 or 16 or 21 or 23 => AiTargetKind.Area,
        4 or 20 => AiTargetKind.Building,
        5 or 6 or 7 => AiTargetKind.Hex,
        _ => AiTargetKind.None
    };
    internal static ushort TargetIndex(int index) => (ushort)Math.Clamp(index, 0, ushort.MaxValue);
    internal static uint ExclusionFlags(uint before, bool helper) => helper ? before | 0x20U : before;
    internal static int AttractStatus(bool targetAllowed, byte stage) => !targetAllowed ? 2 : stage == 5 ? 1 : 3;
    internal static int OccupyRouteStatus(ushort purpose, bool pathSucceeded) =>
        AiLeafRules.RejectBuildingPurpose(purpose) || !pathSucceeded ? 2 : 1;
    // A fresh state with no usable relay returns before checking the purpose.
    internal static int? InitialRelayStatus(bool relayAvailable, ushort purpose) =>
        !relayAvailable ? 1 : AiLeafRules.RejectBuildingPurpose(purpose) ? 2 : null;
    // Fresh facility-attack state at 0x66E6B..0x66E97, before processing the route.
    internal static (ulong First, ulong Second) InitialFacilityRelayTargets(IReadOnlyList<ulong> relays) =>
        (relays.Count > 0 ? relays[0] : 0, relays.Count > 1 ? relays[1] : 0);
    // Polling at 0x66667..0x666CE, before further route advancement.
    internal static byte AreaConstructionPoll(byte stage, bool validHex, bool validBuilding,
        ushort durability, uint maximum) => stage == 2 && (!validHex || validBuilding && durability == maximum)
        ? (byte)4 : stage;
    internal static byte ClearAreaConstructionCompletion(byte stage) => stage == 4 ? (byte)0 : stage;
    internal static int FacilityMode(int helperChoice, int original) => helperChoice switch
    {
        0 or 1 or 2 => 0,
        3 or 4 => 1,
        _ => original
    };
    internal static int FacilityAreaStatus(bool centerHexKind18) => centerHexKind18 ? 2 : 1;

    // Dispatch in CCommandFootholdAttackDepatureNode (0x30055..0x300BF).
    // Zero denotes the direct failure path without a worker call.
    internal static uint AttackDepartureWorker(int purpose) => purpose switch
    {
        11 => 0x4ABE0,
        8 => 0x48CE0,
        9 => 0x46B80,
        10 => 0x4BA50,
        22 => 0x44A80,
        _ => 0
    };

    // First fort replacement path only. PlacementAllowed means that the genuine
    // kind-9 placement check succeeded beside a fort with no category-3 neighbor.
    internal static int? SpecialFortFacilityKind(IReadOnlyCollection<int> candidates,
        bool pendingCategory3, bool placementAllowed)
    {
        if (pendingCategory3 || !placementAllowed) return null;
        if (candidates.Contains(10)) return 10;
        return candidates.Contains(9) ? 9 : null;
    }

    internal static bool SupportFacilityNeighborhoodAllowed(int category3Count, int category2Count) =>
        category3Count > 0 && category2Count == 0;

    // Second replacement path only. PlacementAllowed is the consumed result
    // after the native kind-5 check and then the native kind-6 check, when present.
    // This path changes the candidate list without setting the special flag.
    internal static int? SpecialSupportFacilityKind(IReadOnlyCollection<int> candidates,
        bool pendingCategory2, bool placementAllowed)
    {
        if (pendingCategory2 || !placementAllowed) return null;
        if (candidates.Contains(6)) return 6;
        return candidates.Contains(5) ? 5 : null;
    }
}

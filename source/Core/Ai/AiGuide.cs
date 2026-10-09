namespace San14Tenkazu;

// Where a number of the guide is in the game's file: a leaf's param (Term -1, Arg the param), or an arg of a
// condition's term. Node numbers are the file's (0000/bt_exce.bin of the verified build); AiGuideTests checks each
// still holds the number the guide was written for.
internal readonly record struct AiAnchor(AiTreeKind Tree, int Node, int Term, int Arg)
{
    internal AiSlot? Slot(AiTree[] trees)
    {
        AiTree tree = trees[(int)Tree];
        if (Node < 0 || Node >= tree.Nodes.Length) return null;
        AiNode node = tree.Nodes[Node];
        if (Term < 0) return Arg < node.Params.Length ? new AiSlot(Tree, Node, Arg) : null;
        if (node.Class != AiCatalog.Condition) return null;
        AiTerm[] terms = AiCondition.Of(node.Params).Terms;
        return Term < terms.Length && Arg < terms[Term].Args.Length ? new AiSlot(Tree, Node, terms[Term].FirstArg + Arg) : null;
    }
}

// One number of a decision: what it means, every place the trees keep it (changed together: one rule written in
// several conditions), and how it reads (shown = raw × Scale + Shift: "at most 3 more" reads as 4 targets, ×8÷10 as 80%).
internal sealed record AiGuideValue(string Label, AiAnchor[] Anchors, string Unit = "", int Scale = 1, int Shift = 0)
{
    internal int Shown(int raw) => raw * Scale + Shift;
}

internal sealed record AiGuideSection(string Heading, AiGuideValue[] Values);

internal sealed record AiGuidePage(AiDecision Decision, string Summary, AiGuideSection[] Sections);

// The AI window's guide: each decision the trees make, in words, with the numbers that set it (the judgments mapped in
// source/Reverse/notes/ai.md 판단 지도). Corps-tree numbers are the corps type 0 branch's (every AI corps was type 0 on
// 2026-10-03); AiValues.Counterparts finds the same number in the other types' branches.
internal static partial class AiGuide
{
    private const AiTreeKind F = AiTreeKind.Force, C = AiTreeKind.Corps, T = AiTreeKind.Troops;

    private static AiAnchor P(AiTreeKind tree, int node, int param) => new(tree, node, -1, param);
    private static AiAnchor A(AiTreeKind tree, int node, int term, int arg) => new(tree, node, term, arg);

    internal static IReadOnlyList<AiGuidePage> Pages => pages.Value;

    internal static IEnumerable<AiGuideValue> Values => Pages.SelectMany(p => p.Sections).SelectMany(s => s.Values);

    private static readonly Lazy<AiGuidePage[]> pages = new(() =>
    [
        new(AiDecision.TargetForces, L.AiGuide.TargetSummary,
        [
            new(L.AiGuide.TargetScoring,
            [
                new(L.AiGuide.TargetMinScore, [A(F, 54, 0, 1)]),
                new(L.AiGuide.TargetDuty, [P(F, 39, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.TargetHated, [P(F, 45, 1)]),
                new(L.AiGuide.TargetLiked, [P(F, 47, 1)]),
            ]),
            new(L.AiGuide.TargetCount,
            [
                new(L.AiGuide.TargetWarlikeHigh, [A(F, 56, 2, 1)]),
                new(L.AiGuide.TargetCountHigh, [A(F, 56, 1, 1)], L.AiGuide.UnitCount, Shift: 1),
                new(L.AiGuide.TargetWarlikeMid, [A(F, 60, 1, 1), A(F, 64, 1, 1)]),
                new(L.AiGuide.TargetCountMid, [A(F, 60, 0, 1)], L.AiGuide.UnitCount, Shift: 1),
                new(L.AiGuide.TargetCountLow, [A(F, 64, 0, 1)], L.AiGuide.UnitCount, Shift: 1),
            ]),
            new(L.AiGuide.TargetPlayer, [new(L.AiGuide.TargetPlayerShare, [A(F, 52, 4, 1)], L.AiGuide.UnitPercent)]),
        ]),
        new(AiDecision.FootholdAttack, L.AiGuide.AttackSummary,
        [
            new(L.AiGuide.AttackWhen,
            [
                new(L.AiGuide.AttackPlayerShare, [A(C, 1888, 2, 1), A(C, 1888, 6, 1), A(C, 1888, 10, 1), A(C, 1888, 13, 1), A(C, 1888, 16, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.AttackAtOnce, [A(C, 1929, 3, 1)], L.AiGuide.UnitPlaces),
            ]),
            new(L.AiGuide.AttackFrom,
            [
                new(L.AiGuide.AttackMorale, [A(C, 1979, 0, 1), A(C, 2143, 3, 1)]),
                new(L.AiGuide.AttackFood, [P(C, 1910, 1), P(C, 1913, 1)]),
                new(L.AiGuide.AttackKeep, [P(C, 1924, 1)]),
                new(L.AiGuide.AttackRoute, [A(C, 1979, 5, 4)]),
            ]),
            new(L.AiGuide.AttackDecide,
            [
                new(L.AiGuide.AttackRatio, [P(C, 2038, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.AttackRatioOneBase, [P(C, 2037, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.AttackRatioHardest, [P(C, 2028, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.AttackEndurance, [P(C, 1937, 1), P(C, 2097, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.AttackSend,
            [
                new(L.AiGuide.AttackSendRatio, [P(C, 2051, 1), P(C, 2155, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.AttackSendPlayer, [P(C, 2050, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.AttackSendHard, [P(C, 2048, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.AttackCautious, [A(C, 2061, 0, 1), A(C, 2063, 1, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.AttackAllOut, [A(C, 2063, 0, 1), A(C, 2065, 0, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.AttackFoodFactor, [P(C, 2060, 7), P(C, 2062, 7), P(C, 2064, 7), P(C, 2066, 7), P(C, 2160, 7)]),
            ]),
            new(L.AiGuide.AttackReinforce, [new(L.AiGuide.AttackReinforceRatio, [P(C, 2132, 1)], L.AiGuide.UnitPercent, Scale: 10)]),
        ]),
        new(AiDecision.Intercept, L.AiGuide.InterceptSummary,
        [
            new(L.AiGuide.InterceptWhich,
            [
                new(L.AiGuide.InterceptAreas, [A(C, 1327, 1, 1)], L.AiGuide.UnitAreas),
                new(L.AiGuide.InterceptHexes, [A(C, 1327, 2, 4)], L.AiGuide.UnitHexes),
                new(L.AiGuide.InterceptTribeAreas, [A(C, 1327, 5, 1)], L.AiGuide.UnitAreas),
                new(L.AiGuide.InterceptTribeHexes, [A(C, 1327, 6, 4)], L.AiGuide.UnitHexes),
            ]),
            new(L.AiGuide.InterceptSend,
            [
                new(L.AiGuide.InterceptMargin, [A(C, 1361, 0, 1)]),
                new(L.AiGuide.InterceptSoldiers, [A(C, 1766, 1, 1), A(C, 1776, 0, 1)]),
            ]),
        ]),
        new(AiDecision.Transport, L.AiGuide.TransportSummary,
        [
            new(L.AiGuide.TransportFrom,
            [
                new(L.AiGuide.TransportGold, [P(C, 1827, 1)]),
                new(L.AiGuide.TransportGoldTrait, [P(C, 1826, 1)]),
                new(L.AiGuide.TransportGoldMin, [P(C, 1829, 1)]),
                new(L.AiGuide.TransportGoldMax, [P(C, 1828, 1)]),
                new(L.AiGuide.TransportFood, [P(C, 1833, 1)]),
                new(L.AiGuide.TransportFoodTrait, [P(C, 1832, 1)]),
                new(L.AiGuide.TransportSoldier, [P(C, 1837, 1)]),
                new(L.AiGuide.TransportSoldierTrait, [P(C, 1836, 1)]),
                new(L.AiGuide.TransportSoldierMin, [P(C, 1839, 1)]),
                new(L.AiGuide.TransportSoldierMax, [P(C, 1838, 1)]),
            ]),
            new(L.AiGuide.TransportAnd,
            [
                new(L.AiGuide.TransportSafe, [A(C, 1861, 5, 3)], L.AiGuide.UnitHexes),
                new(L.AiGuide.TransportPlayerShare, [A(C, 1845, 1, 1)], L.AiGuide.UnitPercent),
            ]),
        ]),
        new(AiDecision.Return, L.AiGuide.ReturnSummary,
        [
            new(L.AiGuide.ReturnOthers,
            [
                new(L.AiGuide.ReturnFood, [P(C, 10019, 1), P(C, 10020, 1)]),
                new(L.AiGuide.ReturnMorale, [A(C, 10029, 1, 1)]),
                new(L.AiGuide.ReturnFarFood, [P(C, 10024, 1), P(C, 10025, 1)]),
                new(L.AiGuide.ReturnFar, [A(C, 10029, 4, 4), A(C, 10153, 3, 4)], L.AiGuide.UnitHexes),
            ]),
            new(L.AiGuide.ReturnAttacking,
            [
                new(L.AiGuide.ReturnOutmatched, [P(C, 10119, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.ReturnSiege, [P(C, 10075, 1)]),
                new(L.AiGuide.ReturnNearby, [P(C, 10060, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.ReturnAttackMorale, [A(C, 10132, 0, 1)]),
                new(L.AiGuide.ReturnAttackFood, [P(C, 10143, 1), P(C, 10144, 1)]),
                new(L.AiGuide.ReturnAttackFarFood, [P(C, 10148, 1), P(C, 10149, 1)]),
            ]),
            new(L.AiGuide.ReturnIntercepting, [new(L.AiGuide.ReturnInterceptRatio, [P(C, 10221, 1)], L.AiGuide.UnitPercent, Scale: 10)]),
        ]),
        new(AiDecision.TroopsStandby, L.AiGuide.StandbySummary,
        [
            new(L.AiGuide.StandbyOrder,
            [
                new(L.AiGuide.StandbyIntercept, [A(T, 154, 0, 4)], L.AiGuide.UnitHexes),
                new(L.AiGuide.StandbyJoin, [A(T, 177, 0, 1)], L.AiGuide.UnitHexes),
                new(L.AiGuide.StandbyRoute, [A(T, 206, 2, 4)]),
                new(L.AiGuide.StandbyEnemyRadius, [P(T, 208, 2)], L.AiGuide.UnitHexes),
                new(L.AiGuide.StandbyAllyRadius, [P(T, 213, 3)], L.AiGuide.UnitHexes),
            ]),
        ]),
        .. CivilPages(),
    ]);
}

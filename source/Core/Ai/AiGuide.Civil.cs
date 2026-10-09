namespace San14Tenkazu;

// Diplomacy, domestic affairs and personnel: source/Reverse/notes/ai.md. Numbers with
// unknown effects, random seeds and divisors are excluded; all writes use AiEdits.
internal static partial class AiGuide
{
    private static AiGuidePage[] CivilPages() =>
    [
        new(AiDecision.FriendForces, L.AiGuide.FriendForcesSummary,
        [
            new(L.AiGuide.FriendPower,
            [
                new(L.AiGuide.FriendRatio, [P(F, 130, 1)], L.AiGuide.UnitPercent, Scale: 10),
                new(L.AiGuide.FriendHardest, [P(F, 126, 1)], L.AiGuide.UnitPercent, Scale: 10),
            ]),
        ]),
        new(AiDecision.Goodwill, L.AiGuide.GoodwillSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 3345, 2, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.GoodwillTarget,
            [
                new(L.AiGuide.GoodwillForces, [A(C, 3342, 0, 1)], L.AiGuide.UnitCount),
                new(L.AiGuide.GoodwillFriendship, [A(C, 3365, 2, 2), A(C, 3378, 1, 2)]),
                new(L.AiGuide.GoodwillAttempt, [P(C, 3357, 1)]),
            ]),
        ]),
        new(AiDecision.Alliance, L.AiGuide.AllianceSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 3402, 2, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.AllianceTarget,
            [
                new(L.AiGuide.AllianceForces, [A(C, 3399, 0, 1)], L.AiGuide.UnitCount),
                new(L.AiGuide.AllianceFriendship, [A(C, 3445, 0, 2)]),
                new(L.AiGuide.AllianceRenew, [A(C, 3417, 4, 1), A(C, 3450, 0, 1)], L.AiGuide.UnitMonths),
                new(L.AiGuide.AllianceMonths, [P(C, 3485, 4), P(C, 3486, 4), P(C, 3488, 4), P(C, 3489, 4)], L.AiGuide.UnitMonths),
            ]),
            new(L.AiGuide.AllianceArea,
            [
                new(L.AiGuide.AllianceAreaPower, [P(C, 3467, 1)], L.AiGuide.UnitPercent, Scale: 10),
                new(L.AiGuide.AllianceAreaAmbition, [A(C, 3471, 1, 1)]),
            ]),
        ]),
        new(AiDecision.Annul, L.AiGuide.AnnulSummary,
        [
            new(L.AiGuide.AnnulFirst,
            [
                new(L.AiGuide.AnnulPowerFirst, [A(C, 1291, 1, 1)]),
                new(L.AiGuide.AnnulFriendshipFirst, [A(C, 1291, 2, 1)]),
            ]),
            new(L.AiGuide.AnnulSecond,
            [
                new(L.AiGuide.AnnulPowerSecond, [A(C, 1291, 4, 1)]),
                new(L.AiGuide.AnnulFriendshipSecond, [A(C, 1291, 5, 1)]),
            ]),
        ]),
        new(AiDecision.Exchange, L.AiGuide.ExchangeSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 1153, 2, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.ExchangeTarget,
            [
                new(L.AiGuide.ExchangeFriendship, [A(C, 1161, 3, 2)]),
            ]),
            new(L.AiGuide.ExchangeGold,
            [
                new(L.AiGuide.ExchangeGoldFood, [A(C, 1196, 1, 1)]),
                new(L.AiGuide.ExchangeGoldLow, [A(C, 1196, 4, 1)]),
                new(L.AiGuide.ExchangeGoldGoal, [P(C, 1231, 1)]),
                new(L.AiGuide.ExchangeGoldCap, [P(C, 1230, 1)]),
            ]),
            new(L.AiGuide.ExchangeFood,
            [
                new(L.AiGuide.ExchangeFoodGold, [A(C, 1200, 1, 1)]),
                new(L.AiGuide.ExchangeFoodLow, [A(C, 1200, 4, 1)]),
                new(L.AiGuide.ExchangeFoodGoal, [P(C, 1238, 1)]),
                new(L.AiGuide.ExchangeFoodCap, [P(C, 1237, 1)]),
            ]),
        ]),
        new(AiDecision.PrisonerReturn, L.AiGuide.PrisonerReturnSummary,
        [
            new(L.AiGuide.PrisonerCount,
            [
                new(L.AiGuide.PrisonerMax, [A(C, 925, 0, 1)], L.AiGuide.UnitCount, Shift: 1),
            ]),
        ]),
        new(AiDecision.AttackRequest, L.AiGuide.AttackRequestSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 3012, 2, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.RequestTarget,
            [
                new(L.AiGuide.RequestRadius, [P(C, 3038, 2)], L.AiGuide.UnitHexes),
                new(L.AiGuide.RequestNeighbor, [P(C, 3042, 1)], L.AiGuide.UnitPercent, Scale: 10),
            ]),
        ]),
        new(AiDecision.Recruit, L.AiGuide.RecruitSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 2467, 1, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.RecruitFunds,
            [
                new(L.AiGuide.RecruitGold, [A(C, 2491, 2, 1), A(C, 2509, 2, 1)]),
            ]),
        ]),
        new(AiDecision.Training, L.AiGuide.TrainingSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 2515, 1, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.TrainingSet,
            [
                new(L.AiGuide.TrainingGold, [A(C, 2542, 0, 1), A(C, 2560, 2, 1)]),
                new(L.AiGuide.TrainingLowGold, [A(C, 2542, 2, 1)]),
                new(L.AiGuide.TrainingMorale, [A(C, 2542, 3, 1), A(C, 2560, 1, 1)]),
            ]),
            new(L.AiGuide.TrainingRemove,
            [
                new(L.AiGuide.TrainingRemoveGold, [A(C, 2560, 3, 1)]),
            ]),
        ]),
        new(AiDecision.Development, L.AiGuide.DevelopmentSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 2567, 1, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.DevelopmentAreas,
            [
                new(L.AiGuide.DevelopmentTurn, [A(C, 2582, 1, 1)]),
                new(L.AiGuide.DevelopmentSecurity, [A(C, 2596, 0, 1)]),
            ]),
            new(L.AiGuide.DevelopmentWeights,
            [
                new(L.AiGuide.DevelopmentCommerce, [P(C, 2649, 2), P(C, 2666, 2)]),
                new(L.AiGuide.DevelopmentAgriculture, [P(C, 2649, 3), P(C, 2666, 3)]),
                new(L.AiGuide.DevelopmentBarracks, [P(C, 2649, 4), P(C, 2666, 4)]),
            ]),
        ]),
        new(AiDecision.Merchant, L.AiGuide.MerchantSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 1064, 1, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.MerchantFunds,
            [
                new(L.AiGuide.MerchantLowGold, [A(C, 1094, 0, 1), A(C, 1127, 3, 1)]),
                new(L.AiGuide.MerchantNeedGold, [P(C, 1095, 1)]),
                new(L.AiGuide.MerchantFirstHalf, [P(C, 1100, 1)], L.AiGuide.UnitCount),
                new(L.AiGuide.MerchantSecondHalf, [P(C, 1102, 1)], L.AiGuide.UnitCount),
            ]),
        ]),
        new(AiDecision.Trade, L.AiGuide.TradeSummary,
        [
            new(L.AiGuide.TradeStock,
            [
                new(L.AiGuide.TradeGold, [A(C, 4189, 1, 1)]),
                new(L.AiGuide.TradeFood, [A(C, 4192, 1, 1)]),
                new(L.AiGuide.TradeSoldier, [A(C, 4194, 0, 1)]),
            ]),
            new(L.AiGuide.TradePeople,
            [
                new(L.AiGuide.TradeOfficerStock, [A(C, 4203, 3, 1)], L.AiGuide.UnitCount),
                new(L.AiGuide.TradeOfficerMax, [A(C, 4221, 0, 1)], L.AiGuide.UnitCount),
            ]),
            new(L.AiGuide.TradeAmount,
            [
                new(L.AiGuide.TradeGoldBase, [P(C, 4225, 1)]),
                new(L.AiGuide.TradeAmountBase, [P(C, 4228, 1)]),
            ]),
        ]),
        new(AiDecision.Inspect, L.AiGuide.InspectSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 3069, 4, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.InspectAttempt,
            [
                new(L.AiGuide.InspectThreshold, [P(C, 3060, 1)]),
            ]),
        ]),
        new(AiDecision.Scenery, L.AiGuide.ScenerySummary,
        [
            new(L.AiGuide.SceneryReserve,
            [
                new(L.AiGuide.SceneryFirst, [P(C, 2991, 1)]),
                new(L.AiGuide.ScenerySecond, [P(C, 3002, 1)]),
                new(L.AiGuide.SceneryOther, [P(C, 3004, 1)]),
            ]),
        ]),
        new(AiDecision.Hire, L.AiGuide.HireSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 719, 1, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.HireCity,
            [
                new(L.AiGuide.HireOfficers, [A(C, 737, 0, 1)], L.AiGuide.UnitCount),
                new(L.AiGuide.HireGold, [A(C, 737, 1, 1)]),
            ]),
        ]),
        new(AiDecision.OfficerMove, L.AiGuide.OfficerMoveSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 758, 1, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.MoveCounts,
            [
                new(L.AiGuide.MovePriority, [P(C, 761, 1)], L.AiGuide.UnitCount),
                new(L.AiGuide.MoveCity, [P(C, 762, 1)], L.AiGuide.UnitCount),
                new(L.AiGuide.MoveGate, [P(C, 763, 1)], L.AiGuide.UnitCount),
            ]),
        ]),
        new(AiDecision.Employ, L.AiGuide.EmploySummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 768, 1, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.EmployScoring,
            [
                new(L.AiGuide.EmployFocusLoyalty, [A(C, 787, 1, 1)]),
                new(L.AiGuide.EmployLoyalty, [A(C, 796, 0, 1)]),
                new(L.AiGuide.EmployZeroBonus, [P(C, 801, 1)]),
            ]),
        ]),
        new(AiDecision.Search, L.AiGuide.SearchSummary,
        [
            new(L.AiGuide.SearchPeople,
            [
                new(L.AiGuide.SearchPolitics, [A(C, 846, 5, 1)]),
                new(L.AiGuide.SearchExtraRoll, [A(C, 846, 3, 1)]),
            ]),
        ]),
        new(AiDecision.Reward, L.AiGuide.RewardSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 936, 1, 1)], L.AiGuide.UnitPercent),
            ]),
        ]),
        new(AiDecision.CreditRankUp, L.AiGuide.CreditRankUpSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 961, 1, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.CreditRankScoring,
            [
                new(L.AiGuide.CreditRankIdeology, [P(C, 983, 1)], L.AiGuide.UnitPercent, Scale: 10),
            ]),
        ]),
        new(AiDecision.CreditGive, L.AiGuide.CreditGiveSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 1001, 1, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.CreditGiveScoring,
            [
                new(L.AiGuide.CreditGiveIdeology, [P(C, 1023, 1)], L.AiGuide.UnitPercent, Scale: 10),
            ]),
        ]),
        new(AiDecision.ItemGive, L.AiGuide.ItemGiveSummary,
        [
            new(L.AiGuide.CivilWhen,
            [
                new(L.AiGuide.CivilPlayerShare, [A(C, 1040, 1, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.ItemPeople,
            [
                new(L.AiGuide.ItemLoyalty, [A(C, 1051, 0, 1)]),
                new(L.AiGuide.ItemAbility, [A(C, 1051, 1, 1)]),
            ]),
        ]),
        new(AiDecision.Schemes, L.AiGuide.SchemesSummary,
        [
            new(L.AiGuide.SchemeBase,
            [
                new(L.AiGuide.SchemeGold, [A(C, 42, 0, 1)]),
                new(L.AiGuide.SchemeHigh, [P(C, 47, 1)]),
                new(L.AiGuide.SchemeMid, [P(C, 49, 1)]),
                new(L.AiGuide.SchemeNeutral, [P(C, 51, 1)]),
                new(L.AiGuide.SchemeLow, [P(C, 53, 1)]),
                new(L.AiGuide.SchemeLowest, [P(C, 55, 1)]),
            ]),
            new(L.AiGuide.SchemeAttempts,
            [
                new(L.AiGuide.SchemePoison, [P(C, 3494, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.SchemeEstrange, [P(C, 3557, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.SchemeDestroy, [P(C, 3620, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.SchemePlacation, [P(C, 3681, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.SchemeRumor, [P(C, 3753, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.SchemeUnattended, [P(C, 3776, 1)], L.AiGuide.UnitPercent),
                new(L.AiGuide.SchemeDual, [P(C, 3826, 1)], L.AiGuide.UnitPercent),
            ]),
            new(L.AiGuide.SchemeTargets,
            [
                new(L.AiGuide.SchemeEstrangeDuty, [A(C, 3599, 0, 1), A(C, 3604, 0, 1)]),
                new(L.AiGuide.SchemeDualFriendship, [A(C, 3874, 0, 1)]),
                new(L.AiGuide.SchemePlayerShare, [A(C, 3503, 4, 1), A(C, 3566, 4, 1), A(C, 3629, 4, 1), A(C, 3690, 4, 1), A(C, 3762, 4, 1), A(C, 3785, 4, 1), A(C, 3835, 2, 1)], L.AiGuide.UnitPercent),
            ]),
        ]),
    ];
}

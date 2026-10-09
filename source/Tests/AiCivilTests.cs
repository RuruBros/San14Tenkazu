namespace San14Tenkazu.Tests;

public partial class AiTreeTests
{
    // The custom loaders have optional fields selected by the input count. A trailing file param that was not
    // copied by the game must stay unreadable, even when a made-up heap has bytes at that offset.
    [Theory]
    [InlineData(0xBB940C54u, 5, 5)]
    [InlineData(0xBB940C54u, 6, 5)]
    [InlineData(0xBB940C54u, 7, 7)]
    [InlineData(0xBB940C54u, 8, 5)]
    [InlineData(0x874694D6u, 2, 2)]
    [InlineData(0x874694D6u, 4, 2)]
    [InlineData(0x874694D6u, 5, 5)]
    [InlineData(0x874694D6u, 6, 2)]
    [InlineData(0x184B7500u, 3, 3)]
    [InlineData(0x184B7500u, 4, 4)]
    [InlineData(0x184B7500u, 5, 5)]
    [InlineData(0x184B7500u, 6, 6)]
    [InlineData(0x184B7500u, 7, 6)]
    public void CivilLoadersReadOnlyTheFieldsTheGameCopied(uint hash, int count, int loaded)
    {
        var root = new AiNode(0, 0, AiCatalog.Root, null, -1, 1, [], null, null);
        var leaf = new AiNode(1, 1, hash, null, 0, 0, Enumerable.Range(10, count).ToArray(), null, null);
        root.Children.Add(leaf);
        AiTree[] file = [new(AiTreeKind.Force, 0, 0, [root, leaf])];
        var heap = new MadeUp.Heap(file);
        AiLiveTree live = AiMemory.Read(heap, MadeUp.Heap.ModuleBase, file)[0];
        for (int p = 0; p < count; p++)
        {
            AiCell? cell = AiMemory.Cell(heap, live, 1, p);
            if (p < loaded)
            {
                Assert.Equal(new AiCell(live.Nodes[1] + 0x38 + (ulong)(4 * p), 4), cell);
                Assert.Equal(10 + p, AiMemory.Value(heap, live, 1, p));
            }
            else Assert.Null(cell);
        }
    }

    // The added decisions are public guide pages, each backed by a named block and a localized label.
    [Fact]
    public void DiplomacyDomesticAndPersonnelHaveTheirOwnPages()
    {
        Assert.Equal(29, AiGuide.Pages.Count);
        Assert.Equal(Enum.GetValues<AiDecision>(), AiGuide.Pages.Select(p => p.Decision));
        Assert.Equal(29, AiGuide.Pages.Select(p => AiLabels.Decision(p.Decision)).Distinct().Count());
        foreach (AiGuidePage page in AiGuide.Pages.Skip(6))
        {
            Assert.Contains(AiValues.Decisions, d => d.Decision == page.Decision);
            Assert.NotEmpty(page.Summary);
            Assert.NotEmpty(page.Sections.SelectMany(s => s.Values));
        }
    }

    // Important thresholds, boundary operators and unit conversions from the research: do not reinterpret a < as
    // <=, a multiplier divided by ten as a raw percentage, or the Count <= 2 loop as two prisoners.
    [AiFileFact]
    public void CivilThresholdsKeepTheirFileValuesAndBoundaryOperators()
    {
        AiTree[] trees = GameTrees();
        (AiDecision Decision, string Label, int Shown)[] expected =
        [
            (AiDecision.FriendForces, L.AiGuide.FriendRatio, 300), (AiDecision.FriendForces, L.AiGuide.FriendHardest, 200),
            (AiDecision.Goodwill, L.AiGuide.GoodwillForces, 3), (AiDecision.Goodwill, L.AiGuide.GoodwillAttempt, 30),
            (AiDecision.Alliance, L.AiGuide.AllianceMonths, 12), (AiDecision.Alliance, L.AiGuide.AllianceRenew, 3),
            (AiDecision.Alliance, L.AiGuide.AllianceAreaPower, 200), (AiDecision.PrisonerReturn, L.AiGuide.PrisonerMax, 3),
            (AiDecision.AttackRequest, L.AiGuide.RequestNeighbor, 50), (AiDecision.Recruit, L.AiGuide.RecruitGold, 3000),
            (AiDecision.Training, L.AiGuide.TrainingGold, 6000), (AiDecision.Training, L.AiGuide.TrainingLowGold, 3000),
            (AiDecision.Training, L.AiGuide.TrainingMorale, 60), (AiDecision.Training, L.AiGuide.TrainingRemoveGold, 2000),
            (AiDecision.Development, L.AiGuide.DevelopmentCommerce, 25), (AiDecision.Development, L.AiGuide.DevelopmentAgriculture, 50),
            (AiDecision.Development, L.AiGuide.DevelopmentBarracks, 75), (AiDecision.Trade, L.AiGuide.TradeGold, 9000),
            (AiDecision.Trade, L.AiGuide.TradeFood, 30000), (AiDecision.Trade, L.AiGuide.TradeSoldier, 6000),
            (AiDecision.Trade, L.AiGuide.TradeOfficerMax, 3), (AiDecision.Hire, L.AiGuide.HireOfficers, 5),
            (AiDecision.Employ, L.AiGuide.EmployFocusLoyalty, 80), (AiDecision.Employ, L.AiGuide.EmployLoyalty, 70),
            (AiDecision.Search, L.AiGuide.SearchPolitics, 60), (AiDecision.Schemes, L.AiGuide.SchemeGold, 6000),
            (AiDecision.Schemes, L.AiGuide.SchemePoison, 30), (AiDecision.Schemes, L.AiGuide.SchemeDual, 80),
        ];
        foreach ((AiDecision decision, string label, int shown) in expected)
        {
            AiGuideValue value = AiGuide.Pages.Single(p => p.Decision == decision).Sections.SelectMany(s => s.Values).Single(v => v.Label == label);
            Assert.Equal(shown, value.Shown(AiEdits.FileRaw(trees, value)!.Value));
            Assert.Equal(AiEdits.FileRaw(trees, value), AiEdits.Raw(value, shown));
        }
        var recruit = AiCondition.Of(trees[1].Nodes[2491].Params).Terms[2];
        Assert.Equal((21, false, 3000), (recruit.Function, recruit.Negated, recruit.Args[1]));
        var morale = AiCondition.Of(trees[1].Nodes[2542].Params).Terms[3];
        Assert.Equal((21, true, 60), (morale.Function, morale.Negated, morale.Args[1]));
        var loyalty = AiCondition.Of(trees[1].Nodes[796].Params).Terms[0];
        Assert.Equal((21, true, 70), (loyalty.Function, loyalty.Negated, loyalty.Args[1]));
    }

    // Corps type 2 has no diplomacy/scheme blocks; other decisions have three copies, and search has six. Coupled
    // rules include both sides of a boundary and every calculation/execution call, not just the first anchor.
    [AiFileFact]
    public void CivilRulesReachExactlyTheirExistingCopies()
    {
        AiTree[] trees = GameTrees();
        (AiDecision Decision, string Label, int Count)[] expected =
        [
            (AiDecision.FriendForces, L.AiGuide.FriendRatio, 1), (AiDecision.Goodwill, L.AiGuide.GoodwillFriendship, 4),
            (AiDecision.Alliance, L.AiGuide.AllianceMonths, 8), (AiDecision.Alliance, L.AiGuide.AllianceRenew, 4),
            (AiDecision.Recruit, L.AiGuide.RecruitGold, 6), (AiDecision.Training, L.AiGuide.TrainingGold, 6),
            (AiDecision.Training, L.AiGuide.TrainingMorale, 6), (AiDecision.Development, L.AiGuide.DevelopmentCommerce, 6),
            (AiDecision.Trade, L.AiGuide.TradeGold, 3), (AiDecision.Search, L.AiGuide.SearchPolitics, 6),
            (AiDecision.Search, L.AiGuide.SearchExtraRoll, 6), (AiDecision.Schemes, L.AiGuide.SchemePoison, 2),
            (AiDecision.Schemes, L.AiGuide.SchemePlayerShare, 14), (AiDecision.Schemes, L.AiGuide.SchemeGold, 1),
        ];
        foreach ((AiDecision decision, string label, int count) in expected)
        {
            AiGuideValue value = AiGuide.Pages.Single(p => p.Decision == decision).Sections.SelectMany(s => s.Values).Single(v => v.Label == label);
            List<AiSlot> slots = AiEdits.Slots(trees, value);
            Assert.Equal(count, slots.Count);
            Assert.All(slots, s => Assert.Equal(AiEdits.FileRaw(trees, value), trees[(int)s.Tree].Nodes[s.Node].Params[s.Param]));
        }
        AiGuideValue[] values = AiGuide.Pages.Skip(6).SelectMany(p => p.Sections).SelectMany(s => s.Values).ToArray();
        AiSlot[] all = values.SelectMany(v => AiEdits.Slots(trees, v)).ToArray();
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.Equal(values.Length, values.Select(AiAdjustments.Key).Distinct().Count());
    }

    // Exercise every new value through the real card write path on a made-up heap, including independent args
    // arrays, shared rules and missing branches. Undo restores all values; a fresh heap re-applies the kept keys.
    [AiFileFact]
    public void CivilValuesCanBeWrittenUndoneAndReappliedTogether()
    {
        AiTree[] trees = GameTrees();
        var heap = new MadeUp.Heap(trees);
        AiLiveTree[] live = AiMemory.Read(heap, MadeUp.Heap.ModuleBase, trees);
        var requests = AiGuide.Pages.Skip(6).SelectMany(p => p.Sections).SelectMany(s => s.Values)
            .Select(v => (Value: v, Raw: AiEdits.FileRaw(trees, v)!.Value + 1)).ToList();
        EditPlan plan = AiEdits.Plan(heap, trees, live, "AI civil", requests);
        Assert.False(plan.Refused, plan.Refusal);
        Assert.Equal(requests.Sum(r => AiEdits.Slots(trees, r.Value).Count), plan.Edits.Sum(e => e.Changes.Count));
        GameMemory.Apply(heap, TestGame.Classes, plan.Edits);
        foreach (var request in requests) Assert.Equal(request.Raw, AiEdits.GameRaw(heap, trees, live, request.Value));
        Dictionary<string, int> kept = AiEdits.Changed(heap, trees, live);
        Assert.Equal(requests.Count, kept.Count);

        List<RecordEdit> undo = Undo.Of(plan.Edits);
        Assert.Empty(Undo.Compare(undo, e => heap.Read(e.Address, e.Expected.Length)).Moved);
        GameMemory.Apply(heap, TestGame.Classes, undo);
        Assert.Empty(AiEdits.Changed(heap, trees, live));
        var (write, other) = AiEdits.Reapply(heap, trees, live, kept);
        Assert.Empty(other);
        Assert.Equal(requests.Count, write.Count);
        EditPlan again = AiEdits.Plan(heap, trees, live, "AI civil", write);
        Assert.False(again.Refused, again.Refusal);
        GameMemory.Apply(heap, TestGame.Classes, again.Edits);
        Assert.Equal(kept, AiEdits.Changed(heap, trees, live));
    }
}

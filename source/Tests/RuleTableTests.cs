namespace San14Tenkazu.Tests;

// The 개성 and 명품 cards' rules, the two of the game's tables that are saved with the game: a 개성's effect values
// (reaching every officer with it) and a 명품's ability and bonus.
public class RuleTableTests
{
    private const int 저돌 = 1, 장구 = 20, 절약 = 30, 둔전 = 32, 낭비 = 31, 신장 = 150;

    // 절약 and 둔전 cut 군량 consumption (kind 20), 낭비 raises it; 저돌 only switches a setting on (kind 1, value 1);
    // 신장 has two effects. 하후돈 (in play) and 전위 (dead) have 절약.
    private static TestGame Game() => new TestGame()
        .Personality(저돌, "저돌", "명령설정에서 적접근시「무시」", (1, 1))
        .Personality(장구, "장구", "자신부대의 사기저하가 적음", (13, 20))
        .Personality(절약, "절약", "자신부대 군량소비감소", (20, 20))
        .Personality(낭비, "낭비", "자신부대 군량소비증가", (20, -20))
        .Personality(둔전, "둔전", "특정범위 내 자신세력부대의 군량소비감소", (20, 20))
        .Personality(신장, "신장", "특정범위 내 자신세력부대의 공군·방어", (5, 6), (7, 6))
        .Officer(10, "하후돈", 1, 15, status: 3).Officer(11, "전위", 0, 0, status: 9).Officer(12, "조인", 1, 15, status: 4)
        .With(10, OfficerFields.Personalities[1], 절약).With(11, OfficerFields.Personalities[0], 절약).With(12, OfficerFields.Personalities[0], 장구)
        .Item(11, "참사검", owner: 10, ability: 2, bonus: 10).Item(1, "적토마", owner: 0);

    private static EditPlan Personality(GameSnapshot data, int index, params (int Effect, long Value)[] values) =>
        PersonalityEdits.Plan(data, index, values.Select(v => new FieldChange(PersonalityFields.Values[v.Effect], v.Value)).ToList());

    [Fact]
    public void APersonalitysValueReachesEveryOfficerWithItAndSaysHowMany()
    {
        GameSnapshot data = Game().Build();
        EditPlan plan = Personality(data, 절약, (0, 30));

        Assert.Equal(30, new Written(plan)[data.Personalities.Single(p => p.Index == 절약), PersonalityFields.Values[0]]);
        Assert.Contains("장수 2명(활동 중 1명)", plan.Summary);
    }

    [Fact]
    public void ValuesAreSigned()
    {
        GameSnapshot data = Game().Build();
        PersonalityRecord 낭비record = data.Personalities.Single(p => p.Index == 낭비);
        Assert.Equal(-20, 낭비record.Value(0));

        EditPlan plan = Personality(data, 낭비, (0, -35));
        var memory = new FakeMemory(data);
        GameMemory.Apply(memory, TestGame.Classes, plan.Edits);

        Assert.Equal(-35, PersonalityFields.Values[0].Read(memory[낭비record.Address]));
    }

    [Fact]
    public void AnEffectSlotWithoutAnEffectOrOneOnlySwitchedOnTakesNoValue()
    {
        GameSnapshot data = Game().Build();

        Assert.Contains("효과가 없는 칸", Personality(data, 절약, (1, 10)).Refusal);
        Assert.Contains("켜고 끄기", Personality(data, 저돌, (0, 2)).Refusal);
        Assert.False(Personality(data, 신장, (1, 9)).Refused);
    }

    [Fact]
    public void EachEffectKindIsNamedAfterAPersonalityWithItAlone()
    {
        Dictionary<int, string> names = PersonalityEdits.EffectNames(Game().Build());

        Assert.Equal("군량소비감소", names[20]);
        Assert.Equal("효과 #5", names[5]);   // only 신장 has it, with another
        Assert.Equal([1], PersonalityEdits.Switches(Game().Build()));
    }

    // As the game has them (2026-10-03): 효웅 lowers enemy units in range by -2 on 19, 간웅 raises its own force's
    // others by 2, 오만 lowers its own unit by -2 with few soldiers, 상승 raises its own unit by 4. 19 is named for a
    // positive value with no one in it, from 상승, though 간웅 comes first.
    private static TestGame AllAbilities() => new TestGame()
        .Personality(14, "간웅", "자신부대를 제외한 특정범위 내 자신세력부대의 모든능력상승(함선，병기 외)", (19, 2), condition: 9, filter: 37)
        .Personality(19, "효웅", "특정범위 내 적부대의 모든능력저하(함선，병기 외)", (19, -2), condition: 10, filter: 37)
        .Personality(93, "오만", "병사가 일정 수 이하면 자신부대의 모든능력저하", (19, -2), condition: 20, conditionValue: 3000, filter: 1)
        .Personality(161, "상승", "자신부대의 모든능력상승(함선，병기 외)", (19, 4), condition: 0x25, filter: 37)
        .Personality(258, "무염", "", (19, -2), (13, -10), condition: 10, conditionValue: 3, filter: 1)
        .Personality(108, "투장", "자신부대가 포위효과의 영향을 받지 않음", (55, 1), condition: 26, filter: 1);

    [Fact]
    public void AnEffectIsNamedForAPositiveValueWithoutWhomItReaches()
    {
        Dictionary<int, string> names = PersonalityEdits.EffectNames(AllAbilities().Build());

        Assert.Equal("모든능력상승", names[19]);
        Assert.Equal("포위효과의 영향을 받지 않음", names[55]);
        Assert.Equal("다음 턴개시 시, 사기상승", PersonalityEdits.EffectOnly("다음 턴개시 시,자신부대를 제외한 특정범위 내 자신세력부대의 사기상승"));
        Assert.Equal("상태이상 시, 기간단축", PersonalityEdits.EffectOnly("자신부대를 제외한 특정범위 내 자신세력부대가 상태이상 시,기간단축"));
        Assert.Equal("사기상승", PersonalityEdits.EffectOnly("특정범위 내 적부대 격파 시,자신부대의 사기상승"));
    }

    [Fact]
    public void TheConditionSaysWhomTheEffectsReachAndWhen()
    {
        GameSnapshot data = AllAbilities().Build();
        string Of(int index) => PersonalityEdits.Condition(data.Personalities.Single(p => p.Index == index));

        Assert.Equal("범위 안의 적 부대", Of(258));
        Assert.Equal("범위 안의 적 부대 (함선·병기 제외)", Of(19));
        Assert.Equal("범위 안의 아군 부대(자기 부대 제외) (함선·병기 제외)", Of(14));
        Assert.Equal("병사가 3,000 이하일 때 · 자기 부대", Of(93));
        Assert.Equal("적에게 둘러싸였을 때 · 자기 부대", Of(108));
        Assert.Equal("자기 부대(함선·병기 제외)", Of(161));
    }

    [Fact]
    public void APersonalityEditUndoesToItsValuesBefore()
    {
        GameSnapshot data = Game().Build();
        var memory = new FakeMemory(data);
        EditPlan plan = Personality(data, 신장, (0, 8), (1, 9));
        GameMemory.Apply(memory, TestGame.Classes, plan.Edits);

        Undo.Check check = Undo.Compare(Undo.Of(plan.Edits), edit => memory[edit.Address]);
        GameMemory.Apply(memory, TestGame.Classes, check.Edits);

        Assert.Equal(data.Personalities.Single(p => p.Index == 신장).Data, memory[data.Personalities.Single(p => p.Index == 신장).Address]);
    }

    // As the game has them (2026-10-03): how long each lasts (-1 always on, 0 while its condition holds, days once
    // attacked) comes from its files; 위무 only keeps 도적 away (an officer's kind, 66).
    private static TestGame Kinds() => new TestGame()
        .Personality(20, "장구", "자신부대의 사기저하가 적음", (13, 20), duration: -1)
        .Personality(4, "질주", "자신부대 기동상승(함선，병기 외)", (4, 6), condition: 37, filter: 37)
        .Personality(19, "효웅", "특정범위 내 적부대의 모든능력저하(함선，병기 외)", (19, -2), condition: 10, filter: 37)
        .Personality(25, "일심", "자신부대가 공격 받을 때의 사기저하 적음", (13, 30), condition: 30, filter: 1, duration: 12)
        .Personality(26, "앙양", "특정범위 내 적부대 격파 시，자신부대의 사기상승", (79, 10), condition: 40, filter: 1, duration: -1)
        .Personality(30, "절약", "자신부대 군량소비감소", (20, 20), duration: -1)
        .Personality(93, "오만", "병사가 일정 수 이하면 자신부대의 모든능력저하", (19, -2), condition: 20, conditionValue: 3000, filter: 1)
        .Personality(123, "위무", "소속도시에 있으면 도적이 나타나지 않음", (66, 1))
        .Personality(1, "저돌", "명령설정에서 적접근시「무시」", (1, 1), duration: -1)
        .Personality(258, "무염", "", (19, -2), (13, -10), condition: 10, conditionValue: 3, filter: 1)
        .Officer(10, "하후돈", 1, 15, status: 3).With(10, OfficerFields.Personalities[0], 19);

    private static EditPlan Change(GameSnapshot data, int index, params (FieldDef Field, long Value)[] changes) =>
        PersonalityEdits.Plan(data, index, changes.Select(c => new FieldChange(c.Field, c.Value)).ToList());

    private static readonly FieldDef Condition = PersonalityFields.Condition, Number = PersonalityFields.ConditionValue,
        Filter = PersonalityFields.Filter, Kind1 = PersonalityFields.Kinds[0], Kind2 = PersonalityFields.Kinds[1],
        Value1 = PersonalityFields.Values[0], Value2 = PersonalityFields.Values[1];

    [Fact]
    public void AKindReachesWhomTheWayTheGameReadsItSays()
    {
        Assert.Equal(PersonalityRules.Reach.Unit, PersonalityRules.ReachOf(19));
        Assert.Equal(PersonalityRules.Reach.Own, PersonalityRules.ReachOf(21));
        Assert.Equal(PersonalityRules.Reach.OwnLit, PersonalityRules.ReachOf(51));
        Assert.Equal(PersonalityRules.Reach.Received, PersonalityRules.ReachOf(117));
        Assert.Equal(PersonalityRules.Reach.Officer, PersonalityRules.ReachOf(33));
        Assert.Equal(PersonalityRules.Reach.OfficerAlways, PersonalityRules.ReachOf(56));
        Assert.Equal(PersonalityRules.Reach.Event, PersonalityRules.ReachOf(79));
        Assert.Equal(PersonalityRules.Reach.Unknown, PersonalityRules.ReachOf(88));
    }

    [Fact]
    public void AKindGoesWhereTheConditionNamesTheUnitsItReaches()
    {
        GameSnapshot data = Kinds().Build();

        EditPlan plan = Change(data, 19, (Kind1, 4));   // 효웅: 범위 안의 적 부대, 기동
        Assert.Equal(4, new Written(plan)[data.Personalities.Single(p => p.Index == 19), Kind1]);
        Assert.Contains("효과 1 모든능력저하 (#19) → 기동상승 (#4)", plan.Summary);
        Assert.Contains("자기 부대에만", Change(data, 19, (Kind1, 21)).Refusal);       // 상태이상 면역 reaches its own unit only
        Assert.Contains("다른 부대에만", Change(data, 4, (Kind1, 117)).Refusal);      // 위보 해제 reaches others only
        Assert.False(Change(data, 4, (Kind1, 51)).Refused);                              // ZOC 무시 with its own unit named
        Assert.False(Change(data, 19, (Kind2, 33), (Value2, 30)).Refused);              // an officer's kind, whatever the condition
        Assert.Contains("찾지 못했습니다", Change(data, 19, (Kind1, 88)).Refusal);
    }

    [Fact]
    public void AnAlwaysOnPersonalityTakesNoConditionAndNoKindThatNeedsLighting()
    {
        GameSnapshot data = Kinds().Build();

        Assert.Contains("조건을 쓰지 않습니다", Change(data, 20, (Condition, 10)).Refusal);
        Assert.Contains("발동이 항상인 개성에서는 작동하지 않습니다", Change(data, 20, (Kind1, 51)).Refusal);
        Assert.False(Change(data, 20, (Kind1, 19), (Value1, 2)).Refused);
        Assert.False(Change(data, 30, (Kind1, 56), (Value1, 1)).Refused);                 // 등갑 only with always on
        Assert.Contains("발동이 항상인 개성에서만", Change(data, 4, (Kind1, 56)).Refusal);
    }

    [Fact]
    public void APersonalityWithoutAConditionLightsNothingInBattle()
    {
        GameSnapshot data = Kinds().Build();

        Assert.Contains("켜지지 않아", Change(data, 123, (Kind1, 19), (Value1, 2)).Refusal);  // 위무 has no condition
        Assert.False(Change(data, 123, (Kind1, 33), (Value1, 30)).Refused);
        Assert.Contains("판정하지 않는 조건", Change(data, 4, (Condition, 27)).Refusal);
        Assert.Contains("며칠 동안 켜지는 개성만", Change(data, 4, (Condition, 30)).Refusal);
        Assert.Contains("조건을 바꾸지 않습니다", Change(data, 25, (Condition, 37)).Refusal);   // 일심 is lit by an attack
    }

    [Fact]
    public void TheUnitsAConditionNamesMustMeetASecondCondition()
    {
        GameSnapshot data = Kinds().Build();
        PersonalityRecord 위무 = data.Personalities.Single(p => p.Index == 123);

        var after = new Written(Change(data, 123, (Condition, 9), (Kind1, 19), (Value1, 2)));
        Assert.Equal(9, after[위무, Condition]);
        Assert.Equal(PersonalityRules.FilterArmy, after[위무, Filter]);   // it had none, so no unit would have got it
        Assert.Contains("대상 부대는", Change(data, 19, (Filter, 0)).Refusal);
        Assert.Equal(PersonalityRules.FilterAny, new Written(Change(data, 19, (Filter, 11)))[data.Personalities.Single(p => p.Index == 19), Filter]);
    }

    [Fact]
    public void AConditionsNumberIsTroopsForFewSoldiersAndARangeOnlyForTheLastPersonalities()
    {
        GameSnapshot data = Kinds().Build();

        Assert.Contains("1~60,000", Change(data, 93, (Number, 0)).Refusal);
        Assert.False(Change(data, 93, (Number, 2000)).Refused);
        Assert.Contains("1~10", Change(data, 258, (Number, 11)).Refusal);
        Assert.False(Change(data, 258, (Number, 4)).Refused);
        Assert.Empty(Change(data, 19, (Number, 5)).Edits);   // 효웅's range is the game's 3: no number
    }

    [Fact]
    public void WhenUnitsAreDestroyedTheValuesAreMoraleMarkedAsTheGameMarksThem()
    {
        GameSnapshot data = Kinds().Build();
        PersonalityRecord 위무 = data.Personalities.Single(p => p.Index == 123);

        var after = new Written(Change(data, 123, (Condition, 40), (Value1, 15)));
        Assert.Equal(PersonalityRules.EventMorale, after[위무, Kind1]);
        Assert.Equal(15, after[위무, Value1]);
        Assert.Equal(PersonalityRules.FilterArmy, after[위무, Filter]);
        Assert.Contains("괴멸·격파 조건에서만", Change(data, 26, (Condition, 0)).Refusal);   // 앙양 left with its mark
    }

    [Fact]
    public void AnEffectWithoutAKindHasNoValueAndASwitchedOneHasOne()
    {
        GameSnapshot data = Kinds().Build();
        PersonalityRecord 무염 = data.Personalities.Single(p => p.Index == 258);

        Assert.Equal(0, new Written(Change(data, 258, (Kind2, 0)))[무염, Value2]);
        Assert.Equal(1, new Written(Change(data, 30, (Kind1, 1)))[data.Personalities.Single(p => p.Index == 30), Value1]);
    }

    [Fact]
    public void AConditionAndKindEditUndoesToWhatItWas()
    {
        GameSnapshot data = Kinds().Build();
        PersonalityRecord 위무 = data.Personalities.Single(p => p.Index == 123);
        var memory = new FakeMemory(data);
        EditPlan plan = Change(data, 123, (Condition, 10), (Kind1, 19), (Value1, -2));
        GameMemory.Apply(memory, TestGame.Classes, plan.Edits);

        Undo.Check check = Undo.Compare(Undo.Of(plan.Edits), edit => memory[edit.Address]);
        GameMemory.Apply(memory, TestGame.Classes, check.Edits);

        Assert.Equal(위무.Data, memory[위무.Address]);
    }

    [Fact]
    public void ANewConditionHasTheHoldersLitSlotsJudgedAfresh()
    {
        GameSnapshot data = Kinds().With(10, OfficerFields.PersonalitiesInEffect, 0b11).Build();   // 효웅 in slot 0, lit
        OfficerRecord 하후돈 = data.Officers.Single(o => o.Id == 10);
        var memory = new FakeMemory(data);
        EditPlan plan = Change(data, 19, (Condition, 9));
        GameMemory.Apply(memory, TestGame.Classes, plan.Edits);

        Assert.Equal(0b10, OfficerFields.PersonalitiesInEffect.Read(memory[하후돈.Address]));   // 효웅's slot only
        memory.Set(하후돈.Address, OfficerFields.PersonalitiesInEffect, 0b11);                     // the game lit it again
        GameMemory.Apply(memory, TestGame.Classes, Undo.Compare(Undo.Of(plan.Edits), edit => memory[edit.Address]).Edits);
        Assert.Equal(0b10, OfficerFields.PersonalitiesInEffect.Read(memory[하후돈.Address]));   // undoing judges it afresh too
        Assert.Null(Change(data, 19, (Kind1, 4)).Edits.Single().Restarts);                        // a new kind needs nothing
    }

    [Fact]
    public void AnItemsAbilityAndBonusChange()
    {
        GameSnapshot data = Game().Build();
        ItemRecord 참사검 = data.Items.Single(i => i.Index == 11);
        EditPlan plan = ItemEdits.Plan(data, 11, [new FieldChange(ItemFields.Ability, 3), new FieldChange(ItemFields.Bonus, 15)]);
        var after = new Written(plan);

        Assert.Equal(3, after[참사검, ItemFields.Ability]);
        Assert.Equal(15, after[참사검, ItemFields.Bonus]);
        Assert.Equal("참사검: 무력 +10 → 지력 +15", plan.Summary);
    }

    [Fact]
    public void AnItemThatAddsNoAbilityAddsNoBonus()
    {
        GameSnapshot data = Game().Build();
        EditPlan plan = ItemEdits.Plan(data, 11, [new FieldChange(ItemFields.Ability, 0)]);

        Assert.Equal(0, new Written(plan)[data.Items.Single(i => i.Index == 11), ItemFields.Bonus]);
        Assert.Empty(ItemEdits.Plan(data, 1, [new FieldChange(ItemFields.Bonus, 5)]).Edits);   // 적토마 adds no ability
    }

    [Fact]
    public void TheCardsChangeNothingElse()
    {
        GameSnapshot data = Game().Build();

        Assert.True(PersonalityEdits.Plan(data, 절약, [new FieldChange(PersonalityFields.Kinds[0], 13)]).Refused);
        Assert.True(ItemEdits.Plan(data, 11, [new FieldChange(ItemFields.Owner, 12)]).Refused);
    }
}

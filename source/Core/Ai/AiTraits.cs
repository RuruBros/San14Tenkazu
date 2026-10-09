namespace San14Tenkazu;

// An officer's leanings the AI weighs (source/Reverse/notes/ai.md, 장수의 AI 성향): shorts from +0x16A of the officer
// record, -20..20 in steps of 10 in the game's own data (1,636 officers read on 2026-10-03). The trees read the ruler's
// for his force, the 군단장's for his corps and the best leader's for a sortie, so changing them changes one force's AI
// where a tree's number changes every force's. The names are the trees' own variable names ($PersonalityWarlike…);
// +0x16E is the Fame input ($Personalityfame, case-variant hash and kind 15 verified); +0x17A is the game's Han inclination.
// All twelve exposed fields survived real-game save -> undo -> load and were restored afterward
// (AiSavedTraitVectors.json and AiHanSavedTraitVectors.json; the original English Han variable name is not recovered).
internal static class AiTraits
{
    internal const int Min = -20, Max = 20;

    internal static readonly FieldDef[] Fields =
    [
        Trait(L.Ai.TraitWarlike, 0x16A), Trait(L.Ai.TraitDuty, 0x16C), Trait(L.Ai.TraitFame, 0x16E), Trait(L.Ai.TraitMoney, 0x170), Trait(L.Ai.TraitFood, 0x172),
        Trait(L.Ai.TraitSoldier, 0x174), Trait(L.Ai.TraitTalent, 0x176), Trait(L.Ai.TraitDiplomacy, 0x178), Trait(L.Ai.TraitHan, 0x17A), Trait(L.Ai.TraitAmbition, 0x17C),
        Trait(L.Ai.TraitStrategic, 0x17E), Trait(L.Ai.TraitBuilding, 0x180),
    ];

    // What each is weighed for, as the officer card's tooltips say it (in the order of Fields).
    internal static readonly string[] Tips =
    [
        L.Ai.TraitWarlikeTip, L.Ai.TraitDutyTip, L.Ai.TraitFameTip, L.Ai.TraitMoneyTip, L.Ai.TraitFoodTip, L.Ai.TraitSoldierTip, L.Ai.TraitTalentTip,
        L.Ai.TraitDiplomacyTip, L.Ai.TraitHanTip, L.Ai.TraitAmbitionTip, L.Ai.TraitStrategicTip, L.Ai.TraitBuildingTip,
    ];

    private static FieldDef Trait(string label, int offset) => new(label, RecordKind.Officer, offset, FieldKind.Int16, Min, Max, L.Ai.TraitHint(Min, Max));
}

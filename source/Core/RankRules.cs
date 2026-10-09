namespace San14Tenkazu;

internal static class RankRules
{
    // Plain Native reads: 2B2986 (capacity), 22FE97 (five ability bonuses), appointment paths (+AC).
    // Grade 1..9 is the verified first UI range; baseline restoration preserves the raw byte.
    internal static readonly PlainRuleSpec Spec=new("rank",0x78490,13,0xB8,13,c => c.Rank,RecordKind.Rank,
        [new("capacity",L.Rules.OfficeCapacity,0xAA,FieldKind.UInt16,0,ushort.MaxValue),
         new("leadership",L.Rules.OfficeLeadership,0xAD,FieldKind.Byte,0,byte.MaxValue),
         new("strength",L.Rules.OfficeStrength,0xAE,FieldKind.Byte,0,byte.MaxValue),
         new("intelligence",L.Rules.OfficeIntelligence,0xAF,FieldKind.Byte,0,byte.MaxValue),
         new("politics",L.Rules.OfficePolitics,0xB0,FieldKind.Byte,0,byte.MaxValue),
         new("charm",L.Rules.RankCharm,0xB1,FieldKind.Byte,0,byte.MaxValue),
         new("grade",L.Rules.OfficeGrade,0xAC,FieldKind.Byte,1,9)],
         L.Rules.RankPage,L.Rules.RankHint,L.Rules.RankSearch,L.Rules.RanksInitialized);
}

namespace San14Tenkazu;

internal static class DoctrineRules
{
    // 22C0F0 reads a signed word only for an open, non-disabled panel. 322D50 reads the two cost pairs.
    internal static readonly PlainRuleSpec Spec=new("doctrine",0x85F20,80,0x50,22,c => c.DoctrinePanel,RecordKind.Doctrine,
        [new("effect",L.Rules.DoctrineEffect,0x44,FieldKind.Int16,short.MinValue,short.MaxValue),
         new("kind1",L.Rules.DoctrineKind1,0x3D,FieldKind.Byte,0,6),new("cost1",L.Rules.DoctrineCost1,0x3E,FieldKind.Byte,0,byte.MaxValue),
         new("kind2",L.Rules.DoctrineKind2,0x40,FieldKind.Byte,0,6),new("cost2",L.Rules.DoctrineCost2,0x41,FieldKind.Byte,0,byte.MaxValue)],
         L.Rules.DoctrinePage,L.Rules.DoctrineHint,L.Rules.DoctrineSearch,L.Rules.DoctrinesInitialized);
    internal static bool ValidCosts(Func<string,int> value) => (value("kind1")==0 || value("cost1")>0) && (value("kind2")==0 || value("cost2")>0);
}

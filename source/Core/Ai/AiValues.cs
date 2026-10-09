namespace San14Tenkazu;

// A number of a tree: the node and which of its params (a condition's args are params too, AiTerm.FirstArg).
internal readonly record struct AiSlot(AiTreeKind Tree, int Node, int Param);

// The decisions the AI window lists first (source/Reverse/notes/ai.md 판단 지도), each where a named block (StaticLink)
// makes it; by the block's name hash, so each corps type's copy of it is found too.
internal enum AiDecision
{
    TargetForces, FootholdAttack, Intercept, Transport, Return, TroopsStandby,
    FriendForces, Goodwill, Alliance, Annul, Exchange, PrisonerReturn, AttackRequest,
    Recruit, Training, Development, Merchant, Trade, Inspect, Scenery,
    Hire, OfficerMove, Employ, Search, Reward, CreditRankUp, CreditGive, ItemGive, Schemes
}

internal static class AiValues
{
    internal static readonly (AiDecision Decision, AiTreeKind Tree, uint Block)[] Decisions =
    [
        (AiDecision.TargetForces, AiTreeKind.Force, 0x289AE87D),   // the forces to take on (TARGET_FORCE_LIST)
        (AiDecision.FootholdAttack, AiTreeKind.Corps, 0x0810D369), // sorties against a base (CCommandFootholdAttackDepature)
        (AiDecision.Intercept, AiTreeKind.Corps, 0xFE63F632),      // sorties against enemy groups drawing near
        (AiDecision.Transport, AiTreeKind.Corps, 0x82725CD9),      // transports
        (AiDecision.Return, AiTreeKind.Corps, 0xB4D3D84B),         // each sortie's purpose looked at again: returns
        (AiDecision.TroopsStandby, AiTreeKind.Troops, 0xC517D3A1), // what a sortie on standby does next
        (AiDecision.FriendForces, AiTreeKind.Force, 0xBE2B2510),
        (AiDecision.Goodwill, AiTreeKind.Corps, 0xAEDD56EC),
        (AiDecision.Alliance, AiTreeKind.Corps, 0xBF186B14),
        (AiDecision.Annul, AiTreeKind.Corps, 0xB5B1B07B),
        (AiDecision.Exchange, AiTreeKind.Corps, 0x5ED0B89C),
        (AiDecision.PrisonerReturn, AiTreeKind.Corps, 0x66A370DC),
        (AiDecision.AttackRequest, AiTreeKind.Corps, 0x1E67A315),
        (AiDecision.Recruit, AiTreeKind.Corps, 0x6194E96E),
        (AiDecision.Training, AiTreeKind.Corps, 0x85AA8E6C),
        (AiDecision.Development, AiTreeKind.Corps, 0x1BE5DB28),
        (AiDecision.Merchant, AiTreeKind.Corps, 0x5EBBD40E),
        (AiDecision.Trade, AiTreeKind.Corps, 0x0137B3B0),
        (AiDecision.Inspect, AiTreeKind.Corps, 0x7CA8F884),
        (AiDecision.Scenery, AiTreeKind.Corps, 0xF7D66DE5),
        (AiDecision.Hire, AiTreeKind.Corps, 0x1D41DD52),
        (AiDecision.OfficerMove, AiTreeKind.Corps, 0x4F434AAE),
        (AiDecision.Employ, AiTreeKind.Corps, 0xB64C60B8),
        (AiDecision.Search, AiTreeKind.Corps, 0xE79AC205),
        (AiDecision.Reward, AiTreeKind.Corps, 0xD0A50A02),
        (AiDecision.CreditRankUp, AiTreeKind.Corps, 0x835FFEA5),
        (AiDecision.CreditGive, AiTreeKind.Corps, 0x29A4BF9C),
        (AiDecision.ItemGive, AiTreeKind.Corps, 0xBBF03330),
        (AiDecision.Schemes, AiTreeKind.Corps, 0xC4BD9D36),
        (AiDecision.Schemes, AiTreeKind.Corps, 0xF3A85FD4),
        (AiDecision.Schemes, AiTreeKind.Corps, 0x60732FC0),
        (AiDecision.Schemes, AiTreeKind.Corps, 0x964C9E7C),
        (AiDecision.Schemes, AiTreeKind.Corps, 0x5208EA1D),
        (AiDecision.Schemes, AiTreeKind.Corps, 0x29A79F67),
        (AiDecision.Schemes, AiTreeKind.Corps, 0x918C8FAC),
    ];

    // Every number a designer could tune in a tree (AiCatalog.IsNumber), in file order.
    internal static IEnumerable<AiSlot> Numbers(AiTree tree)
    {
        foreach (AiNode node in tree.Nodes)
            for (int p = 0; p < node.Params.Length; p++)
                if (AiCatalog.IsNumber(node, p)) yield return new AiSlot(tree.Kind, node.Index, p);
    }

    // The blocks of this name in a tree, in file order.
    internal static IEnumerable<AiNode> Blocks(AiTree tree, uint name) =>
        tree.Nodes.Where(n => n.Class == AiCatalog.StaticLink && unchecked((uint)n.Params[0]) == name);

    // The named block a node sits in: the nearest StaticLink above it, or itself.
    internal static AiNode? BlockOf(AiTree tree, AiNode node)
    {
        for (AiNode? n = node; n is not null; n = tree.ParentOf(n))
            if (n.Class == AiCatalog.StaticLink) return n;
        return null;
    }

    // The same number in the other copies of its block: blocks of the same name and shape, the number at the same place
    // in each. The corps tree has a copy of most blocks for each corps type (P26), and a few blocks twice in one; a copy
    // may hold another value, which is why they are listed rather than changed together.
    internal static List<AiSlot> Counterparts(AiTree tree, AiSlot slot)
    {
        var found = new List<AiSlot>();
        if (BlockOf(tree, tree.Nodes[slot.Node]) is not { } block) return found;
        int offset = slot.Node - block.Index, size = tree.SubtreeSize(block);
        uint name = unchecked((uint)block.Params[0]);
        foreach (AiNode other in Blocks(tree, name))
            if (other != block && tree.SubtreeSize(other) == size && SameShape(tree, block.Index, other.Index, size))
                found.Add(slot with { Node = other.Index + offset });
        return found;
    }

    // Whether two runs of nodes are the same logic: each pair alike in class, children and params, the numbers aside.
    private static bool SameShape(AiTree tree, int a, int b, int size)
    {
        for (int k = 0; k < size; k++)
        {
            AiNode x = tree.Nodes[a + k], y = tree.Nodes[b + k];
            if (x.Class != y.Class || x.Children.Count != y.Children.Count || x.Params.Length != y.Params.Length) return false;
            if (x.Parent - a != y.Parent - b && k > 0) return false;
            for (int p = 0; p < x.Params.Length; p++)
                if (x.Params[p] != y.Params[p] && !AiCatalog.IsNumber(x, p)) return false;
        }
        return true;
    }
}

using System.Globalization;

namespace San14Tenkazu;

// The AI window's words for what the trees hold: ko.txt's AI tables by the game's names ([AiNodes] by class name,
// [AiParams] by class and param, [AiInfo] by class and kind, [AiPredicates] by number), and the enums named.
internal static class AiLabels
{
    internal const string NodeTable = "AiNodes", ParamTable = "AiParams", InfoTable = "AiInfo", PredicateTable = "AiPredicates";

    // What a class does; the game's class name when the table has nothing for it.
    internal static string Node(AiClass cls) => L.Named(NodeTable, cls.Name) is { Length: > 0 } label ? label : cls.Name;

    internal static string Param(AiClass cls, int param) => L.Named(ParamTable, $"{cls.Name}.{param}");

    internal static string Info(AiClass cls, int kind) => L.Named(InfoTable, $"{cls.Name}.{kind.ToString(CultureInfo.InvariantCulture)}");

    internal static string Predicate(int id) =>
        L.Table(PredicateTable, id) is { Length: > 0 } label ? label : AiCatalog.Predicate(id)?.Name ?? $"#{id}";

    internal static string Tree(AiTreeKind kind) => kind switch
    {
        AiTreeKind.Force => L.Ai.TreeForce,
        AiTreeKind.Corps => L.Ai.TreeCorps,
        AiTreeKind.Troops => L.Ai.TreeTroops,
        _ => L.Ai.TreeUnit
    };

    internal static string Decision(AiDecision decision) => decision switch
    {
        AiDecision.TargetForces => L.Ai.DecisionTargetForces,
        AiDecision.FootholdAttack => L.Ai.DecisionFootholdAttack,
        AiDecision.Intercept => L.Ai.DecisionIntercept,
        AiDecision.Transport => L.Ai.DecisionTransport,
        AiDecision.Return => L.Ai.DecisionReturn,
        AiDecision.FriendForces => L.Ai.DecisionFriendForces,
        AiDecision.Goodwill => L.Ai.DecisionGoodwill,
        AiDecision.Alliance => L.Ai.DecisionAlliance,
        AiDecision.Annul => L.Ai.DecisionAnnul,
        AiDecision.Exchange => L.Ai.DecisionExchange,
        AiDecision.PrisonerReturn => L.Ai.DecisionPrisonerReturn,
        AiDecision.AttackRequest => L.Ai.DecisionAttackRequest,
        AiDecision.Recruit => L.Ai.DecisionRecruit,
        AiDecision.Training => L.Ai.DecisionTraining,
        AiDecision.Development => L.Ai.DecisionDevelopment,
        AiDecision.Merchant => L.Ai.DecisionMerchant,
        AiDecision.Trade => L.Ai.DecisionTrade,
        AiDecision.Inspect => L.Ai.DecisionInspect,
        AiDecision.Scenery => L.Ai.DecisionScenery,
        AiDecision.Hire => L.Ai.DecisionHire,
        AiDecision.OfficerMove => L.Ai.DecisionOfficerMove,
        AiDecision.Employ => L.Ai.DecisionEmploy,
        AiDecision.Search => L.Ai.DecisionSearch,
        AiDecision.Reward => L.Ai.DecisionReward,
        AiDecision.CreditRankUp => L.Ai.DecisionCreditRankUp,
        AiDecision.CreditGive => L.Ai.DecisionCreditGive,
        AiDecision.ItemGive => L.Ai.DecisionItemGive,
        AiDecision.Schemes => L.Ai.DecisionSchemes,
        _ => L.Ai.DecisionTroopsStandby
    };

    // A param's role (AiClass.Roles) as a word.
    internal static string Role(char role) => role switch
    {
        'v' => L.Ai.RoleVariable,
        'n' => L.Ai.RoleNumber,
        't' => L.Ai.RoleTunable,
        'e' => L.Ai.RoleEnum,
        '-' => L.Ai.RoleUnused,
        _ => L.Ai.RoleUnknown
    };
}

// A node spelled out in one line, as the AI window lists it. Variables are the game's own names ($EnemyBattlePower), or
// their hash where none was found; numbers have separators. Comparisons read as comparisons ($Soldier > 1,000), a
// negated one turned over ($Soldier ≤ 1,000).
internal static class AiText
{
    // The predicates that compare two values, with the sign that holds and the one that holds when it is negated.
    private static readonly Dictionary<int, (string Holds, string Negated)> Comparisons = new()
    {
        [18] = (">", "≤"), [19] = ("≥", "<"), [20] = (">", "≤"), [21] = ("≥", "<"), [23] = ("=", "≠"), [37] = ("=", "≠"),
    };

    internal static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    internal static string Variable(int value) =>
        "$" + (AiCatalog.Variable(unchecked((uint)value)) ?? unchecked((uint)value).ToString("X8", CultureInfo.InvariantCulture));

    // A value in a slot that holds a variable (role v) or a number; an unknown slot guesses from the value, as a hash is
    // far from the small numbers the trees write.
    internal static string Value(int value, char role) => role switch
    {
        'v' when value != 0 => Variable(value),
        'v' or 'n' or 't' or 'e' or '-' => Number(value),
        _ => AiCatalog.Variable(unchecked((uint)value)) is not null || Math.Abs((long)value) >= 0x100000 ? Variable(value) : Number(value)
    };

    internal static string Line(AiTree tree, AiNode node)
    {
        if (node.Class == AiCatalog.Root) return L.Ai.Root;
        if (node.Class == AiCatalog.Sequential) return Sequence(AiSequence.Of(node.Params));
        if (node.Class == AiCatalog.Condition) return Condition(AiCondition.Of(node.Params));
        if (node.Class == AiCatalog.StaticLink) return Block(tree, node);
        if (AiCatalog.Class(node.Class) is not { } cls) return $"{node.Class:X8}({string.Join(", ", node.Params.Select(p => Value(p, '?')))})";
        string[] args = node.Params.Select((p, k) => Value(p, cls.Role(k))).ToArray();
        if (AiCatalog.IsInfo(cls.Name))
        {
            int kind = AiCatalog.KindParam(cls.Name);
            if (kind < args.Length && AiLabels.Info(cls, node.Params[kind]) is { Length: > 0 } fact)
                args[kind] = fact;
        }
        return $"{AiLabels.Node(cls)}({string.Join(", ", args)})";
    }

    internal static string Sequence(AiSequence s)
    {
        string order = s.Order switch
        {
            AiSequenceOrder.ByPriority => L.Ai.OrderByPriority,
            AiSequenceOrder.Shuffled => L.Ai.OrderShuffled,
            _ => L.Ai.OrderInOrder
        };
        string rule = s.Rule switch
        {
            AiSequenceRule.Selector => L.Ai.RuleSelector,
            AiSequenceRule.Sequence => L.Ai.RuleSequence,
            _ => L.Ai.RuleAll
        };
        return L.Ai.Sequence(order, rule) + (s.WholeTick ? "" : " " + L.Ai.OneATick);
    }

    internal static string Condition(AiCondition c)
    {
        var parts = new List<string>();
        for (int k = 0; k < c.Terms.Length;)
        {
            AiTerm term = c.Terms[k];
            if (term.Kind == AiTermKind.Predicate)
            {
                parts.Add(Term(term));
                k++;
                continue;
            }
            string join = term.Kind == AiTermKind.All ? L.Ai.And : L.Ai.Or;
            parts.Add("(" + string.Join($" {join} ", c.Terms.Skip(k + 1).Take(term.Group).Select(Term)) + ")");
            k += 1 + term.Group;
        }
        string text = string.Join($" {(c.AllMustHold ? L.Ai.And : L.Ai.Or)} ", parts);
        if (c.Inverted) text = L.Ai.Inverted(text);
        return c.Mode switch
        {
            AiConditionMode.While => L.Ai.ConditionWhile(text),
            AiConditionMode.Until => L.Ai.ConditionUntil(text),
            _ => L.Ai.ConditionIf(text)
        };
    }

    internal static string Term(AiTerm term)
    {
        string[] signature = AiCatalog.Predicate(term.Function)?.Args ?? [];
        string[] args = term.Args.Select((a, k) => Value(a, k < signature.Length ? signature[k] switch { "n" => 'n', "e" => 'e', _ => 'v' } : '?')).ToArray();
        if (Comparisons.TryGetValue(term.Function, out var sign) && args.Length == 2)
            return $"{args[0]} {(term.Negated ? sign.Negated : sign.Holds)} {args[1]}";
        string call = $"{AiLabels.Predicate(term.Function)}({string.Join(", ", args)})";
        return term.Negated ? $"{L.Ai.Not} {call}" : call;
    }

    internal static string Block(AiTree tree, AiNode node)
    {
        uint name = unchecked((uint)node.Params[0]);
        string shown = AiCatalog.Variable(name) ?? name.ToString("X8", CultureInfo.InvariantCulture);
        foreach ((AiDecision decision, AiTreeKind kind, uint block) in AiValues.Decisions)
            if (kind == tree.Kind && block == name) return L.Ai.BlockDecision(shown, AiLabels.Decision(decision));
        return L.Ai.Block(shown);
    }

    // What a param of a node is, for the window's table of them: its role and its meaning.
    internal static (string Role, string Meaning, bool Number) Describe(AiNode node, int param)
    {
        if (node.Class == AiCatalog.Condition)
        {
            AiCondition c = AiCondition.Of(node.Params);
            if (c.ArgAt(param) is (int t, int a))
            {
                AiTerm term = c.Terms[t];
                bool number = AiCatalog.IsNumber(node, param);
                string role = number ? L.Ai.RoleTunable : AiCatalog.Predicate(term.Function)?.Args is { } sig && a < sig.Length && sig[a] == "e" ? L.Ai.RoleEnum : L.Ai.RoleVariable;
                return (role, L.Ai.TermArg(t + 1, a + 1, AiLabels.Predicate(term.Function)), number);
            }
            if (c.TermFieldAt(param) is (int ft, _)) return (L.Ai.RoleStructure, L.Ai.TermField(ft + 1), false);
            return (L.Ai.RoleStructure, L.Ai.ConditionHead, false);
        }
        if (node.Class == AiCatalog.Sequential)
            return (L.Ai.RoleStructure, param switch { 0 => L.Ai.SequenceOrder, 1 => L.Ai.SequenceRule, _ => L.Ai.SequenceWhole }, false);
        if (node.Class == AiCatalog.StaticLink) return (L.Ai.RoleStructure, L.Ai.BlockName, false);
        if (AiCatalog.Class(node.Class) is not { } cls) return (L.Ai.RoleUnknown, "", false);
        char r = cls.Role(param);
        string meaning = AiLabels.Param(cls, param);
        if (AiCatalog.IsInfo(cls.Name))
        {
            int kind = AiCatalog.KindParam(cls.Name);
            meaning = param == kind ? L.Ai.InfoKind(AiLabels.Info(cls, node.Params[kind])) : param < kind ? L.Ai.InfoSource : L.Ai.InfoOutput;
        }
        return (AiLabels.Role(r), meaning, r is 'n' or 't');
    }

    // Which corps type's branch a node of the corps tree is in (the MyDistrictTypeIs condition above it), or null.
    internal static int? CorpsType(AiTree tree, AiNode node)
    {
        for (AiNode? n = node; n is not null; n = tree.ParentOf(n))
            if (n.Class == AiCatalog.Condition && AiCondition.Of(n.Params).Terms is [{ Kind: AiTermKind.Predicate, Function: AiCatalog.CorpsTypePredicate, Args: [int type] }, ..])
                return type;
        return null;
    }
}

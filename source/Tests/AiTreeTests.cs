using System.Buffers.Binary;
using System.Text;

namespace San14Tenkazu.Tests;

// A test that reads the game's bt_exce.bin: skipped where the game is not installed.
public sealed class AiFileFactAttribute : FactAttribute
{
    internal static readonly string Folder = Path.GetDirectoryName(GameFiles.Exe)!;

    public AiFileFactAttribute()
    {
        if (!File.Exists(Path.Combine(Folder, AiTreeFile.RelativePath))) Skip = $"게임 폴더에 {AiTreeFile.RelativePath}이 없습니다: {Folder}";
    }
}

// The AI's behaviour trees (Core/Ai): the game's file read as the game reads it, what its numbers are, the copies of a
// block, and the trees on the heap of a made-up game and of the running one (read only).
public partial class AiTreeTests
{
    private static AiTree[] GameTrees() => AiTreeFile.Read(AiFileFactAttribute.Folder);

    [AiFileFact]
    public void ReadsTheFourTreesAsTheGameDoes()
    {
        AiTree[] trees = GameTrees();

        Assert.Equal(new[] { 246, 10312, 348, 111 }, trees.Select(t => t.Nodes.Length));
        Assert.Equal(new uint[] { 0x7CC94F1A, 0xEB10FB33, 0x9C5B9A81, 0xFB6999B7 }, trees.Select(t => t.ChunkHash));
        Assert.Equal(new uint[] { 0x57C83E03, 0xCCA2CE60, 0x6B6C9FAD, 0xEEE443B1 }, trees.Select(t => t.TreeHash));
        Assert.All(trees, tree =>
        {
            Assert.Equal(AiCatalog.Root, tree.Root.Class);
            Assert.Equal(-1, tree.Root.Parent);
            Assert.All(tree.Nodes, node => Assert.Equal(node.ChildCount, node.Children.Count));
            Assert.All(tree.Nodes.Skip(1), node => Assert.InRange(node.Parent, 0, node.Index - 1));
        });
        Assert.Equal("攻略対象勢力リスト", trees[0].Nodes[75].Text);
    }

    [AiFileFact]
    public void EveryClassIsKnownAndEveryConditionReadsToItsEnd()
    {
        foreach (AiNode node in GameTrees().SelectMany(t => t.Nodes))
        {
            AiClass? cls = AiCatalog.Class(node.Class);
            Assert.NotNull(cls);
            Assert.Equal(cls.Leaf, node.IsLeaf);
            if (node.Class != AiCatalog.Condition) continue;
            AiCondition condition = AiCondition.Of(node.Params);
            Assert.All(condition.Terms.Where(t => t.Kind == AiTermKind.Predicate), t => Assert.NotNull(AiCatalog.Predicate(t.Function)));
            Assert.All(condition.Terms.Where(t => t.Kind != AiTermKind.Predicate), t => Assert.True(t.Group > 0));
        }
    }

    // The corps tree sends a unit out (#1777 CDepartureNode) only past "the soldiers to send > 1,000" (#1776).
    [AiFileFact]
    public void ASortieWaitsForAThousandSoldiers()
    {
        AiTree corps = GameTrees()[(int)AiTreeKind.Corps];
        AiNode departure = corps.Nodes[1777], gate = corps.Nodes[1776];
        AiCondition condition = AiCondition.Of(gate.Params);
        AiTerm term = Assert.Single(condition.Terms);

        Assert.Equal("CDepartureNode", AiCatalog.Class(departure.Class)!.Name);
        Assert.Equal(gate, corps.ParentOf(departure));
        Assert.Equal((AiConditionMode.If, true, false), (condition.Mode, condition.AllMustHold, condition.Inverted));
        Assert.Equal((AiTermKind.Predicate, 20, "Greater"), (term.Kind, term.Function, AiCatalog.Predicate(term.Function)!.Value.Name));
        Assert.Equal(new[] { unchecked((int)0x608D13B6), 1000 }, term.Args);
        Assert.True(AiCatalog.IsNumber(gate, term.FirstArg + 1));
        Assert.False(AiCatalog.IsNumber(gate, term.FirstArg));
        Assert.False(AiCatalog.IsNumber(gate, term.FirstArg - 3));
        Assert.Equal<(int, int)?>((0, 1), condition.ArgAt(term.FirstArg + 1));
        Assert.Equal<(int, int)?>((0, 2), condition.TermFieldAt(term.FirstArg - 3));
    }

    // The AI window's lines: a comparison as a comparison (a negated one turned over), a block with its decision, an info
    // node with the fact it reads, and which corps type's branch a node is in.
    [AiFileFact]
    public void TheWindowSpellsANodeOutInALine()
    {
        AiTree corps = GameTrees()[(int)AiTreeKind.Corps];
        AiNode gate = corps.Nodes[1776];
        AiTerm term = AiCondition.Of(gate.Params).Terms[0];

        Assert.Equal(L.Ai.ConditionIf("$608D13B6 > 1,000"), AiText.Line(corps, gate));
        Assert.Contains("$478F0486 ≤ 5", AiText.Line(corps, corps.Nodes[1773]));
        Assert.Equal(L.Ai.Sequence(L.Ai.OrderInOrder, L.Ai.RuleSequence), AiText.Line(corps, corps.Nodes[1767]));
        Assert.Equal(L.Ai.Root, AiText.Line(corps, corps.Root));
        Assert.Equal(L.Ai.BlockDecision("82725CD9", L.Ai.DecisionTransport), AiText.Line(corps, corps.Nodes[1813]));
        Assert.Contains(AiLabels.Info(AiCatalog.Class(corps.Nodes[1821].Class)!, 16), AiText.Line(corps, corps.Nodes[1821]));
        Assert.Contains("$PersonalityMoney", AiText.Line(corps, corps.Nodes[1821]));
        Assert.Equal((L.Ai.RoleTunable, L.Ai.TermArg(1, 2, AiLabels.Predicate(20)), true), AiText.Describe(gate, term.FirstArg + 1));
        Assert.Equal((L.Ai.RoleVariable, false), (AiText.Describe(gate, term.FirstArg).Role, AiText.Describe(gate, term.FirstArg).Number));
        Assert.Equal(new int?[] { 0, 1, 2, null }, new[] { 1777, 5400, 8692, 10007 }.Select(i => AiText.CorpsType(corps, corps.Nodes[i])));
        Assert.All(GameTrees().SelectMany(t => t.Nodes.Select(n => (t, n))), x => Assert.NotEmpty(AiText.Line(x.t, x.n)));
    }

    // Every number of the guide is where the guide says, a number the trees write (not a variable or a kind), the same
    // in every place it is kept, and reads as the guide reads it.
    [AiFileFact]
    public void TheGuidesNumbersAreWhereItSays()
    {
        AiTree[] trees = GameTrees();
        Assert.Equal(Enum.GetValues<AiDecision>(), AiGuide.Pages.Select(p => p.Decision));
        foreach (AiGuideValue value in AiGuide.Pages.SelectMany(p => p.Sections).SelectMany(s => s.Values))
        {
            var slots = value.Anchors.Select(a => a.Slot(trees)).ToList();
            Assert.All(slots, s => Assert.NotNull(s));
            Assert.All(slots, s => Assert.True(AiCatalog.IsNumber(trees[(int)s!.Value.Tree].Nodes[s.Value.Node], s.Value.Param), $"{value.Label}: {s}"));
            Assert.Single(slots.Select(s => trees[(int)s!.Value.Tree].Nodes[s.Value.Node].Params[s.Value.Param]).Distinct());
        }
        int Raw(AiDecision d, string label) => AiGuide.Pages.Single(p => p.Decision == d).Sections.SelectMany(s => s.Values).First(v => v.Label == label)
            is { } v && v.Anchors[0].Slot(trees) is { } s ? v.Shown(trees[(int)s.Tree].Nodes[s.Node].Params[s.Param]) : -1;
        Assert.Equal(2000, Raw(AiDecision.Transport, L.AiGuide.TransportGold));
        Assert.Equal(4, Raw(AiDecision.TargetForces, L.AiGuide.TargetCountHigh));
        Assert.Equal(2, Raw(AiDecision.TargetForces, L.AiGuide.TargetCountLow));
        Assert.Equal(80, Raw(AiDecision.FootholdAttack, L.AiGuide.AttackReinforceRatio));
        Assert.Equal(150, Raw(AiDecision.Return, L.AiGuide.ReturnInterceptRatio));
        Assert.Equal(1000, Raw(AiDecision.Intercept, L.AiGuide.InterceptSoldiers));
        Assert.Equal(AiTraits.Fields.Length, AiTraits.Tips.Length);
    }

    // Each label of ko.txt's AI tables is for a class, param or kind there is.
    [Fact]
    public void TheLabelsAreForClassesThereAre()
    {
        var names = AiCatalog.Classes.ToDictionary(c => c.Name);
        foreach (string key in L.Texts.Keys)
        {
            string[] parts = key.Split('.');
            if (parts[0] == AiLabels.NodeTable) Assert.True(names.ContainsKey(parts[1]), key);
            if (parts[0] == AiLabels.ParamTable) Assert.True(names.TryGetValue(parts[1], out AiClass? c) && c.Leaf && int.Parse(parts[2]) < Math.Max(c.Roles.Length, c.Offsets?.Length ?? 16), key);
            if (parts[0] == AiLabels.InfoTable) Assert.True(AiCatalog.IsInfo(parts[1]), key);
            if (parts[0] == AiLabels.PredicateTable) Assert.NotNull(AiCatalog.Predicate(int.Parse(parts[1])));
        }
        Assert.All(AiValues.Decisions, d => Assert.NotEmpty(AiLabels.Decision(d.Decision)));
    }

    // An officer's leanings are written as the officer card writes its other numbers: a short each, at its place.
    [Fact]
    public void AnOfficersLeaningIsWrittenWhereTheTreesReadIt()
    {
        GameSnapshot data = new TestGame().Force(1, ruler: 1, 1).City(15, 1, governor: 1).Officer(1, "동탁", 1, 15, status: 1)
            .With(1, AiTraits.Fields[0], 20).Build();
        EditPlan plan = OfficerEdits.Plan(data, 1, new OfficerEdits.Request([new FieldChange(AiTraits.Fields[0], -10)], null, null, []));
        var after = new Written(plan);

        Assert.Equal(20, data.OfficerById[1][AiTraits.Fields[0]]);
        Assert.Equal(-10, after[data.OfficerById[1], AiTraits.Fields[0]]);
        Assert.Equal(new[] { 0x16A, 0x16C, 0x16E, 0x170, 0x172, 0x174, 0x176, 0x178, 0x17A, 0x17C, 0x17E, 0x180 }, AiTraits.Fields.Select(f => f.Offset));
        Assert.All(AiTraits.Fields, f => Assert.Equal((FieldKind.Int16, RecordKind.Officer, -20L, 20L), (f.Kind, f.Record, f.Min, f.Max)));
    }

    [Fact]
    public void NamesHashAsTheGameHashesThem()
    {
        Assert.Equal(AiCatalog.Root, AiCatalog.NameHash("CBehaviorTreeRootNode"));
        Assert.Equal(AiCatalog.Sequential, AiCatalog.NameHash("CBehaviorTreeSequentialNode"));
        Assert.Equal(AiCatalog.Condition, AiCatalog.NameHash("CBehaviorTreeConditionDecoratorNode"));
        Assert.Equal(AiCatalog.StaticLink, AiCatalog.NameHash("CBehaviorTreeStaticLinkNode"));
        Assert.Equal(0xC553A000u, AiCatalog.NameHash("TARGET_FORCE_LIST"));
        Assert.Equal("TARGET_FORCE_LIST", AiCatalog.Variable(0xC553A000));
        Assert.Equal(AiCatalog.NameHash("SUM"), AiCatalog.NameHash("StL"));   // why a short name may be a chance match
        Assert.All(AiCatalog.Variables, v => Assert.Equal(v.Key, AiCatalog.NameHash(v.Value)));
    }

    // The transport block is in each corps type's branch; a number of it is found at the same place in the others.
    [AiFileFact]
    public void ACopyOfABlockIsFoundInEachCorpsType()
    {
        AiTree corps = GameTrees()[(int)AiTreeKind.Corps];
        (_, AiTreeKind tree, uint block) = AiValues.Decisions.Single(d => d.Decision == AiDecision.Transport);
        var surplus = new AiSlot(AiTreeKind.Corps, 1827, 1);   // VariablePlusValue(gold kept back, 2,000)

        Assert.Equal(AiTreeKind.Corps, tree);
        Assert.Equal(new[] { 1813, 5386, 8678 }, AiValues.Blocks(corps, block).Select(n => n.Index));
        Assert.Equal(2000, corps.Nodes[1827].Params[1]);
        Assert.Equal(new[] { 5400, 8692 }, AiValues.Counterparts(corps, surplus).Select(s => s.Node));
        Assert.All(AiValues.Counterparts(corps, surplus), s => Assert.Equal(2000, corps.Nodes[s.Node].Params[1]));
        Assert.Contains(surplus, AiValues.Numbers(corps));
        Assert.DoesNotContain(surplus with { Param = 0 }, AiValues.Numbers(corps));
        Assert.All(AiValues.Decisions, d => Assert.NotEmpty(AiValues.Blocks(GameTrees()[(int)d.Tree], d.Block)));
    }

    [Fact]
    public void AMadeUpFileReadsBackAndABrokenOneIsRefused()
    {
        byte[] file = MadeUp.File();
        AiTree[] trees = AiTreeFile.Parse(file);

        Assert.Equal(4, trees.Length);
        AiTree tree = trees[2];
        Assert.Equal((AiTreeKind.Troops, 0xC0000002u, 0x70000002u), (tree.Kind, tree.ChunkHash, tree.TreeHash));
        Assert.Equal(new[] { AiCatalog.Root, AiCatalog.Sequential, AiCatalog.Condition, MadeUp.Leaf }, tree.Nodes.Select(n => n.Class));
        Assert.Equal(new[] { 1, 2, 3 }, tree.Nodes.Take(3).Select(n => n.Children.Single().Index));
        Assert.Equal(MadeUp.ConditionParams, tree.Nodes[2].Params);
        Assert.Equal(new[] { 7, 5 }, tree.Nodes[3].Params);
        Assert.Equal(new[] { 9, 8 }, tree.Nodes[3].Array);
        Assert.Equal(("経路", 0x1234u), (tree.Nodes[3].Text, tree.Nodes[3].Extra));
        Assert.Equal(AiSequenceRule.Sequence, AiSequence.Of(tree.Nodes[1].Params).Rule);

        Assert.Throws<InvalidDataException>(() => AiTreeFile.Parse(file.AsSpan(0, file.Length - 40)));
        Assert.Throws<InvalidDataException>(() => AiTreeFile.Parse(Encoding.ASCII.GetBytes("JBTX").Concat(file.Skip(4)).ToArray()));
        Assert.Throws<InvalidDataException>(() => AiCondition.Of([0, 1, 0, 1, 2, 0, 20, 0, 3, 1, 2]));
    }

    // The made-up file laid out on a made-up heap as the game lays it out: read back node by node, a number found
    // where it lives, and one the "game" holds otherwise told apart from the file.
    [Fact]
    public void ReadsTheTreesOnTheHeapAndTellsWhatDiffersFromTheFile()
    {
        AiTree[] file = AiTreeFile.Parse(MadeUp.File());
        var heap = new MadeUp.Heap(file);
        AiLiveTree[] live = AiMemory.Read(heap, MadeUp.Heap.ModuleBase, file);
        AiLiveTree troops = live[2];
        AiTerm term = AiCondition.Of(file[2].Nodes[2].Params).Terms[0];

        Assert.Equal(heap.Roots, live.Select(t => t.Root));
        Assert.Equal(5, AiMemory.Value(heap, troops, 3, 1));
        Assert.Equal(1000, AiMemory.Value(heap, troops, 2, term.FirstArg + 1));
        Assert.Equal(20, AiMemory.Value(heap, troops, 2, term.FirstArg - 3));
        Assert.Equal(1, AiMemory.Value(heap, troops, 2, 1));
        Assert.Equal(1, AiMemory.Value(heap, troops, 1, 1));
        Assert.Empty(live.SelectMany(t => AiMemory.Differences(heap, t)));

        heap.Poke(AiMemory.Cell(heap, troops, 2, term.FirstArg + 1)!.Value, 1500);
        Assert.Equal((2, term.FirstArg + 1, 1000, 1500), Assert.Single(AiMemory.Differences(heap, troops)));

        heap.Poke(new AiCell(troops.Nodes[3] + 8, 4), 0x12345678);
        Assert.Throws<InvalidDataException>(() => AiMemory.Read(heap, MadeUp.Heap.ModuleBase, file));
    }

    // A leaf's number and a condition's arg written through the editor's write (GameMemory.Apply) on a made-up heap,
    // and [되돌리기] putting them back.
    [Fact]
    public void AGuideNumberIsWrittenAndUndoneAsACardsValueIs()
    {
        AiTree[] file = AiTreeFile.Parse(MadeUp.File());
        var heap = new MadeUp.Heap(file);
        AiLiveTree[] live = AiMemory.Read(heap, MadeUp.Heap.ModuleBase, file);
        var leaf = new AiGuideValue("더할 값", [new AiAnchor(AiTreeKind.Troops, 3, -1, 1)]);
        var arg = new AiGuideValue("병사 기준", [new AiAnchor(AiTreeKind.Troops, 2, 0, 1)]);

        EditPlan plan = AiEdits.Plan(heap, file, live, "AI 시험", [(leaf, 9), (arg, 1500)]);
        Assert.False(plan.Refused, plan.Refusal);
        Assert.Equal(new[] { RecordKind.AiNode, RecordKind.AiArgs }, plan.Edits.Select(e => e.Kind).Order());
        GameMemory.Apply(heap, TestGame.Classes, plan.Edits);
        Assert.Equal((9, 1500), (AiMemory.Value(heap, live[2], 3, 1), AiEdits.GameRaw(heap, file, live, arg)));
        Assert.Equal(2, AiMemory.Differences(heap, live[2]).Count);

        List<RecordEdit> undo = Undo.Of(plan.Edits);
        Assert.Empty(Undo.Compare(undo, e => heap.Read(e.Address, e.Expected.Length)).Moved);
        GameMemory.Apply(heap, TestGame.Classes, undo);
        Assert.Empty(AiMemory.Differences(heap, live[2]));

        Assert.True(AiEdits.Plan(heap, file, live, "AI 시험", [(leaf, 5_000)]).Refused);   // past ten times the file's
        heap.Poke(new AiCell(live[2].Nodes[3] + 8, 4), 0x1234);
        Assert.True(AiEdits.Plan(heap, file, live, "AI 시험", [(leaf, 9)]).Refused);       // not the node read
    }

    // The game's own trees on a made-up heap: a kept number goes to all three corps types' copies, is written again
    // where the game holds its file's value (it started over), and left where it holds a third.
    [AiFileFact]
    public void AKeptNumberGoesInAgainWhereTheGameStartedOver()
    {
        AiTree[] trees = GameTrees();
        var heap = new MadeUp.Heap(trees);
        AiLiveTree[] live = AiMemory.Read(heap, MadeUp.Heap.ModuleBase, trees);
        AiGuideValue gold = AiGuide.Values.Single(v => v.Label == L.AiGuide.TransportGold);
        var kept = new Dictionary<string, int> { [AiAdjustments.Key(gold)] = 3000 };

        var (write, other) = AiEdits.Reapply(heap, trees, live, kept);
        Assert.Equal((gold, 3000), Assert.Single(write));
        Assert.Empty(other);
        EditPlan plan = AiEdits.Plan(heap, trees, live, "AI", write);
        Assert.Equal(3, plan.Edits.Sum(e => e.Changes.Count));
        GameMemory.Apply(heap, TestGame.Classes, plan.Edits);
        Assert.Equal(3000, AiEdits.GameRaw(heap, trees, live, gold));
        Assert.Equal(kept, AiEdits.Changed(heap, trees, live));
        Assert.Empty(AiEdits.Reapply(heap, trees, live, kept).Write);

        heap.Poke(AiMemory.Cell(heap, live[1], 5400, 1)!.Value, 2500);
        Assert.Null(AiEdits.GameRaw(heap, trees, live, gold));
        Assert.Equal(gold, Assert.Single(AiEdits.Reapply(heap, trees, live, kept).Other).Value);
    }

    // The kept numbers are for one edition of the game's file: another stamp finds none.
    [Fact]
    public void KeptNumbersAreForTheFileTheyWereKeptFor()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ai_values_{Guid.NewGuid():N}.txt");
        try
        {
            var kept = new AiAdjustments(path);
            kept.Save("STAMP", new Dictionary<string, int> { ["1:1827:-1:1"] = 3000, ["0:54:0:1"] = 20 });
            Assert.Equal(3000, kept.Load("STAMP")["1:1827:-1:1"]);
            Assert.Equal(2, kept.Load("STAMP").Count);
            Assert.Empty(kept.Load("OTHER"));
            Assert.Empty(new AiAdjustments(path + ".none").Load("STAMP"));
            Assert.Equal(80, AiGuide.Values.Single(v => v.Label == L.AiGuide.AttackReinforceRatio).Shown(8));
            Assert.Equal(8, AiEdits.Raw(AiGuide.Values.Single(v => v.Label == L.AiGuide.AttackReinforceRatio), 80));
            Assert.Null(AiEdits.Raw(AiGuide.Values.Single(v => v.Label == L.AiGuide.AttackReinforceRatio), 85));
        }
        finally { File.Delete(path); }
    }

    // AiCatalog.Data.cs is made from the research tables (ai.py csharp): they must still say the same.
    [Fact]
    public void TheCatalogSaysWhatTheResearchTablesSay()
    {
        string reverse = Path.Combine(SourceFolder(), "Reverse");
        if (!File.Exists(Path.Combine(reverse, "ai_preds.tsv"))) return;
        foreach (string[] row in Rows(Path.Combine(reverse, "ai_preds.tsv")))
            Assert.Equal((row[1], row.Length > 2 ? row[2] : ""), AiCatalog.Predicate(int.Parse(row[0])) is { } p ? (p.Name, string.Join(' ', p.Args)) : ("", ""));
        foreach (string line in File.ReadLines(Path.Combine(reverse, "ai_vars.txt")).Where(l => !l.StartsWith('#')))
            Assert.Equal(line.Split(' ')[1], AiCatalog.Variable(Convert.ToUInt32(line.Split(' ')[0], 16)));
        var roles = Rows(Path.Combine(reverse, "ai_leaves.tsv")).Where(r => r[1] != "node").GroupBy(r => r[0].Trim());
        foreach (var cls in roles)
        {
            AiClass known = AiCatalog.Classes.Single(c => c.Name == cls.Key);
            foreach (string[] row in cls) Assert.Equal(RoleLetter(row[2]), known.Role(int.Parse(row[1])));
        }
    }

    // The running game's trees are the file's, and the catalog names each class as the game's own code does: the
    // function in a leaf's vtable slot 13 (a composite's 12) is "lea rax, [rip + name]; ret".
    [LiveGameFact]
    public void TheGameHoldsTheFilesTreesUnderTheCatalogsNames()
    {
        GameIdentity identity = GameMemory.FindGame();
        AiTree[] file = GameTrees();
        AiLiveTree[] live = GameMemory.ReadAiTrees(identity, file);

        Assert.Equal(file.Select(t => t.Nodes.Length), live.Select(t => t.Nodes.Length));
        GameMemory.ReadAi(identity, memory =>
        {
            Assert.All(live, tree => Assert.Empty(AiMemory.Differences(memory, tree)));
            var seen = new HashSet<uint>();
            foreach (AiLiveTree tree in live)
                for (int i = 0; i < tree.Nodes.Length; i++)
                {
                    AiClass cls = AiCatalog.Class(tree.File.Nodes[i].Class)!;
                    if (!seen.Add(cls.Hash) || cls.Name == "CLoopListElementNode") continue;   // a composite made by the leaves' factory: no name slot
                    ulong vtable = BitConverter.ToUInt64(memory.Read(tree.Nodes[i], 8));
                    Assert.Equal(cls.Name, NameInSlot(memory, vtable, cls.Leaf ? 13 : 12));
                }
            return 0;
        });
    }

    private static string NameInSlot(IRecordMemory memory, ulong vtable, int slot)
    {
        ulong function = BitConverter.ToUInt64(memory.Read(vtable + (ulong)(8 * slot), 8));
        byte[] code = memory.Read(function, 8);
        Assert.Equal([0x48, 0x8D, 0x05], code[..3]);
        Assert.Equal(0xC3, code[7]);
        ulong text = function + 7 + (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(3));
        byte[] bytes = memory.Read(text, 100);
        return Encoding.ASCII.GetString(bytes, 0, Array.IndexOf(bytes, (byte)0)).Trim();
    }

    private static char RoleLetter(string role) =>
        role.StartsWith("in", StringComparison.Ordinal) || role.StartsWith("out", StringComparison.Ordinal) ? 'v'
        : role == "number:tunable" ? 't' : role.StartsWith("number", StringComparison.Ordinal) ? 'n'
        : role.StartsWith("enum", StringComparison.Ordinal) ? 'e' : role == "unused" ? '-' : '?';

    private static IEnumerable<string[]> Rows(string tsv) =>
        File.ReadLines(tsv).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t'));

    private static string SourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "San14Tenkazu.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException("San14Tenkazu.csproj");
    }

    // A small file of four trees, each root → Sequence → condition → leaf, and that file laid out on a heap.
    private static class MadeUp
    {
        internal const uint Leaf = 0x8B144FCD;   // CVariablePlusValueNode: params at +0x38, +0x3C
        internal static readonly int[] ConditionParams = [0, 1, 0, 1, 2, 0, 20, 0, 2, 0x11223344, 1000];

        internal static byte[] File()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var chunks = Enumerable.Range(0, 4).Select(Chunk).ToArray();
            var data = new List<byte>();
            data.AddRange("JBTP"u8.ToArray()); Int(data, 2); Int(data, 4); Int(data, 0);
            int offset = 0x10 + 12 * 4;
            foreach (var (bytes, i) in chunks.Select((c, i) => (c, i))) { Int(data, offset); Int(data, bytes.Length); Int(data, unchecked((int)(0xC0000000 + i))); offset += bytes.Length; }
            foreach (byte[] chunk in chunks) data.AddRange(chunk);
            return data.ToArray();
        }

        private static byte[] Chunk(int t)
        {
            var ktbt = new List<byte>();
            ktbt.AddRange("KTBT"u8.ToArray()); Int(ktbt, 1); Int(ktbt, unchecked((int)(0x70000000 + t))); Int(ktbt, 0); Int(ktbt, 4);
            Node(ktbt, 0, AiCatalog.Root, -1, 1, []);
            Node(ktbt, 0, AiCatalog.Sequential, 0, 1, [0, 1, 1]);
            Node(ktbt, 0, AiCatalog.Condition, 1, 1, ConditionParams);
            Node(ktbt, 1 | 2 | 4 | 8, Leaf, 2, 0, [7, 5], extra: 0x1234, text: "経路", array: [9, 8]);
            var tree = new List<byte>();
            tree.AddRange("ITBG0000"u8.ToArray()); Int(tree, 0x10 + ktbt.Count); Int(tree, 0); tree.AddRange(ktbt);
            var chunk = new List<byte>();
            chunk.AddRange("TB1G0000"u8.ToArray()); Int(chunk, 0); Int(chunk, 0x20); chunk.AddRange(new byte[0x10]);
            chunk.AddRange("IZSG0000"u8.ToArray()); Int(chunk, 0x18); chunk.AddRange(new byte[0x0C]);
            chunk.AddRange(tree);
            BinaryPrimitives.WriteInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chunk)[8..], chunk.Count);
            return chunk.ToArray();
        }

        private static void Node(List<byte> d, uint flags, uint cls, int parent, int children, int[] p, uint extra = 0, string? text = null, int[]? array = null)
        {
            Int(d, (int)flags); Int(d, unchecked((int)cls));
            if ((flags & 4) != 0) Int(d, (int)extra);
            Int(d, parent); Int(d, children); Int(d, p.Length);
            foreach (int v in p) Int(d, v);
            if (text is not null) { byte[] b = Encoding.GetEncoding(932).GetBytes(text); Int(d, b.Length); d.AddRange(b); }
            if (array is not null) { Int(d, array.Length); foreach (int v in array) Int(d, v); }
        }

        private static void Int(List<byte> d, int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); d.AddRange(b); }

        // The heap: exe + 0x1B9A4A0 → CAIManager, +8 → the tree manager with the roots at +0x10..; nodes as AiMemory reads them.
        internal sealed class Heap : IRecordMemory
        {
            internal const ulong ModuleBase = 0x7FF7_0000_0000;
            private readonly SortedList<ulong, byte[]> blocks = [];
            private ulong next = 0x1_0000_0000;
            internal ulong[] Roots { get; }

            internal Heap(AiTree[] trees)
            {
                ulong manager = Block(0x60), ai = Block(0x30);
                Put(ModuleBase + AiMemory.ManagerPointerRva, ai);
                Put(ai + 8, manager);
                Roots = new ulong[trees.Length];
                for (int k = 0; k < trees.Length; k++)
                {
                    AiNode[] nodes = trees[k].Nodes;
                    ulong[] at = nodes.Select(_ => Block(0x100)).ToArray();
                    ulong table = Block(8 * nodes.Length);
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        Put(table + (ulong)(8 * i), at[i]);
                        Put32(at[i] + 8, nodes[i].Class);
                        int[] p = nodes[i].Params;
                        if (nodes[i].Class == AiCatalog.Condition)
                        {
                            for (int h = 0; h < 4; h++) blocks[at[i]][0x28 + h] = (byte)p[h];
                            AiCondition c = AiCondition.Of(p);
                            ulong terms = Block(0x18 * c.Terms.Length);
                            Put(at[i] + 0x30, terms);
                            for (int t = 0; t < c.Terms.Length; t++)
                            {
                                ulong term = terms + (ulong)(0x18 * t), args = Block(4 * Math.Max(1, c.Terms[t].Args.Length));
                                byte[] b = blocks[terms];
                                int o = 0x18 * t;
                                (b[o + 8], b[o + 9], b[o + 10], b[o + 11]) = ((byte)c.Terms[t].Kind, (byte)(c.Terms[t].Negated ? 1 : 0), (byte)c.Terms[t].Group, (byte)c.Terms[t].Args.Length);
                                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 0xC), c.Terms[t].Function);
                                Put(term + 0x10, args);
                                for (int a = 0; a < c.Terms[t].Args.Length; a++) Put32(args + (ulong)(4 * a), unchecked((uint)c.Terms[t].Args[a]));
                            }
                        }
                        else if (nodes[i].Class == AiCatalog.Sequential || nodes[i].Class == AiCatalog.StaticLink || nodes[i].IsLeaf)
                            for (int q = 0; q < p.Length; q++) Put32(at[i] + (nodes[i].IsLeaf ? 0x38u : 0x28u) + (ulong)(4 * q), unchecked((uint)p[q]));
                    }
                    Put(at[0] + 0x38, (ulong)nodes.Length);
                    Put(at[0] + 0x40, table);
                    Put(manager + 0x10 + (ulong)(8 * k), at[0]);
                    Roots[k] = at[0];
                }
            }

            internal void Poke(AiCell cell, int value)
            {
                byte[] data = new byte[cell.Size];
                if (cell.Size == 1) data[0] = (byte)value; else BinaryPrimitives.WriteInt32LittleEndian(data, value);
                Write(cell.Address, data);
            }

            public byte[] Read(ulong address, int length)
            {
                (ulong start, byte[] block) = Find(address);
                return block.AsSpan((int)(address - start), length).ToArray();
            }

            public void Write(ulong address, byte[] data)
            {
                (ulong start, byte[] block) = Find(address);
                data.CopyTo(block, (int)(address - start));
            }

            private (ulong, byte[]) Find(ulong address)
            {
                IList<ulong> starts = blocks.Keys;
                int lo = 0, hi = starts.Count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    if (starts[mid] <= address) lo = mid + 1; else hi = mid - 1;
                }
                if (hi >= 0 && address < starts[hi] + (ulong)blocks.Values[hi].Length) return (starts[hi], blocks.Values[hi]);
                throw new InvalidOperationException($"unmapped {address:X}");
            }

            private ulong Block(int size)
            {
                ulong at = next;
                blocks[at] = new byte[size];
                next += (ulong)((size + 0xFFF) & ~0xFFF) + 0x1000;
                return at;
            }

            private void Put(ulong address, ulong value)
            {
                if (address >= ModuleBase && address < ModuleBase + 0x3000_0000 && !blocks.ContainsKey(address)) blocks[address] = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(Find(address).Item2.AsSpan((int)(address - Find(address).Item1)), value);
            }

            private void Put32(ulong address, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Find(address).Item2.AsSpan((int)(address - Find(address).Item1)), value);
        }
    }
}

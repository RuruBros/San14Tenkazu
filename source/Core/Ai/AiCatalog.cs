namespace San14Tenkazu;

// A node class: its hash in the file, the game's name for it, whether it is a leaf, where a leaf keeps each param on
// the heap (its loader, vtable +0x88, copies params[k] to node + Offsets[k]; null for a composite or a leaf whose loader
// lays them out its own way) and what each param is (Roles, a letter a param: v variable, n number, t number a designer
// tunes, e enum, - unused, ? not read; shorter than the params when the class was not read).
internal sealed record AiClass(uint Hash, string Name, bool Leaf, int[]? Offsets, string Roles)
{
    internal char Role(int param) => param < Roles.Length ? Roles[param] : '?';
}

// What the trees' numbers mean, as far as the game's code was read (source/Reverse/notes/ai.md and its tables).
internal static partial class AiCatalog
{
    // ktgl's composites that the file uses (vtable slot 12 names them; the hash is NameHash of the name).
    internal const uint Root = 0xF3836309, Sequential = 0x416D418C, Condition = 0x2252B2CB, StaticLink = 0xBD1BADA3;

    // MyDistrictTypeIs (P26): the corps tree's three branches by the corps' type (+0x15).
    internal const int CorpsTypePredicate = 26;

    private static readonly Dictionary<uint, AiClass> classes;
    private static readonly Dictionary<int, (string Name, string Signature)> predicates;
    private static readonly Dictionary<uint, string> variables;
    private static readonly HashSet<string> infoClasses;

    // After every part's field initializers (the tables are in AiCatalog.Data.cs, whose turn among them is not fixed).
    static AiCatalog()
    {
        classes = ClassData.ToDictionary(c => c.Hash);
        predicates = PredicateData.ToDictionary(p => p.Id, p => (p.Name, p.Signature));
        variables = VariableData.ToDictionary(v => v.Hash, v => v.Name);
        infoClasses = [.. InfoClassData];
    }

    internal static IReadOnlyCollection<AiClass> Classes => classes.Values;
    internal static IReadOnlyDictionary<uint, string> Variables => variables;

    internal static AiClass? Class(uint hash) => classes.GetValueOrDefault(hash);

    // A predicate of the dispatcher 0xC2610 (ids 1..167): its name and a letter an argument (v int variable, o object
    // variable, l list variable, n number, e enum, space-separated; "v|o" either).
    internal static (string Name, string[] Args)? Predicate(int id) =>
        predicates.TryGetValue(id, out var p) ? (p.Name, p.Signature.Split(' ', StringSplitOptions.RemoveEmptyEntries)) : null;

    internal static string? Variable(uint hash) => variables.GetValueOrDefault(hash);

    // An info node reads one fact of an object chosen by a kind number: (source, kind, output), or (kind, output) for the
    // ones whose source is the AI running the tree (CGetMyself*Info) or the game (CVariableGetGameData).
    internal static bool IsInfo(string className) => infoClasses.Contains(className);

    internal static int KindParam(string className) =>
        className.StartsWith("CGetMyself", StringComparison.Ordinal) || className.StartsWith("CVariableGetGameData", StringComparison.Ordinal) ? 0 : 1;

    // The game's hash of a variable's or a composite's name (0xB263C0): each signed byte times 31 to the power of its
    // place plus one, summed in 32 bits. The leaves' hashes come from other strings.
    internal static uint NameHash(string name) => NameHash(System.Text.Encoding.ASCII.GetBytes(name));

    // 0xB263C0 consumes a NUL-terminated byte string, including signed non-ASCII bytes.
    internal static uint NameHash(ReadOnlySpan<byte> name)
    {
        uint hash = 0, power = 1;
        foreach (byte b in name)
        {
            if (b == 0) break;
            power = unchecked(power * 31);
            hash = unchecked(hash + (uint)(sbyte)b * power);
        }
        return hash;
    }

    // Whether a param of a node is a number written in the tree (not a variable, a kind or a term's own field): what a
    // designer could tune. A condition's numbers are the predicates' "n" args; a leaf's are its "n" and "t" params.
    internal static bool IsNumber(AiNode node, int param)
    {
        if (node.Class == Condition)
        {
            AiCondition condition = AiCondition.Of(node.Params);
            if (condition.ArgAt(param) is not (int t, int a)) return false;
            AiTerm term = condition.Terms[t];
            return term.Kind == AiTermKind.Predicate && Predicate(term.Function) is { Args: var args } && a < args.Length && args[a] == "n";
        }
        return node.IsLeaf && Class(node.Class)?.Role(param) is 'n' or 't';
    }
}

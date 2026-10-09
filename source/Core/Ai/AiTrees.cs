using System.Buffers.Binary;
using System.Text;

namespace San14Tenkazu;

// The four behaviour trees the game's AI runs (source/Reverse/notes/ai.md), in the order its file has them and its AI
// manager keeps them (CAIManager +0x10..: CForceAI, CDistrictAI, CTroopsAI, CArmyAI). Every AI force runs the same ones.
internal enum AiTreeKind { Force, Corps, Troops, Unit }

// A node as the file has it. Its params are the ints the file gives it; what they mean depends on its class (AiCatalog):
// a leaf keeps them as they are, a condition is laid out as AiCondition reads it.
internal sealed class AiNode(int index, uint flags, uint classHash, uint? extra, int parent, int childCount, int[] parameters, string? text, int[]? array)
{
    internal int Index { get; } = index;
    internal uint Flags { get; } = flags;
    internal uint Class { get; } = classHash;
    internal uint? Extra { get; } = extra;
    internal int Parent { get; } = parent;
    internal int ChildCount { get; } = childCount;
    internal int[] Params { get; } = parameters;
    internal string? Text { get; } = text;
    internal int[]? Array { get; } = array;
    internal List<AiNode> Children { get; } = [];

    // Flag 1: a leaf, made by the game's own factory; the others are ktgl's composites.
    internal bool IsLeaf => (Flags & 1) != 0;
}

internal sealed record AiTree(AiTreeKind Kind, uint ChunkHash, uint TreeHash, AiNode[] Nodes)
{
    internal AiNode Root => Nodes[0];

    internal AiNode? ParentOf(AiNode node) => node.Parent >= 0 ? Nodes[node.Parent] : null;

    // The node and every node under it, in file order (which is depth first: a subtree is a run of numbers).
    internal int SubtreeSize(AiNode node) => 1 + node.Children.Sum(SubtreeSize);
}

// 0000/bt_exce.bin: "JBTP", u32 version, u32 count, then (offset, size, hash) a chunk. A chunk is a TB1G0000 block whose
// header gives its own size; inside it blocks of {magic 8, u32 size with the header, u32 0}: IZSG0000, ITBG0000 (the
// tree, KTBT at +0x10), IBBG0000 (its blackboard, not read here). KTBT: u32 version, u32 tree hash, u32 0, u32 nodes,
// then each node: u32 flags (1 leaf, 2 text, 4 extra, 8 array), u32 class hash, [u32 extra], i32 parent, i32 children,
// i32 n, n × i32, [u32 length + cp932 text], [u32 count + i32s]. The game reads it so at 0xB28120 (BT_ReadKtbt).
internal static class AiTreeFile
{
    // The one the CE exe names (0xC0543); bt.bin and bt_ex.bin beside it are older editions in the same form.
    internal static readonly string RelativePath = Path.Combine("0000", "bt_exce.bin");

    private const int MaxParams = 1 << 16;

    static AiTreeFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static AiTree[] Read(string gameFolder) => Parse(File.ReadAllBytes(Path.Combine(gameFolder, RelativePath)));

    // What tells one edition of the file from another (the numbers the editor keeps are for one): its SHA-256.
    internal static string Stamp(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    internal static AiTree[] Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x10 || !data[..4].SequenceEqual("JBTP"u8)) throw new InvalidDataException(RelativePath);
        int chunks = Int(data, 8);
        var trees = new List<AiTree>();
        for (int c = 0; c < chunks; c++)
        {
            int offset = Int(data, 0x10 + c * 12), size = Int(data, 0x14 + c * 12);
            uint hash = UInt(data, 0x18 + c * 12);
            if (offset < 0 || size < 0x20 || offset + size > data.Length || !data.Slice(offset, 8).SequenceEqual("TB1G0000"u8))
                throw new InvalidDataException($"{RelativePath}: chunk {c}");
            ReadOnlySpan<byte> chunk = data.Slice(offset, size);
            for (int p = Int(chunk, 12); p + 0x10 <= chunk.Length;)
            {
                int length = Int(chunk, p + 8);
                if (length <= 0 || p + length > chunk.Length) throw new InvalidDataException($"{RelativePath}: chunk {c} block at {p:X}");
                if (chunk.Slice(p, 8).SequenceEqual("ITBG0000"u8))
                    trees.Add(ParseTree((AiTreeKind)trees.Count, hash, chunk.Slice(p + 0x10, length - 0x10)));
                p += length;
            }
        }
        if (trees.Count != 4) throw new InvalidDataException($"{RelativePath}: {trees.Count} trees");
        return trees.ToArray();
    }

    private static AiTree ParseTree(AiTreeKind kind, uint chunkHash, ReadOnlySpan<byte> d)
    {
        if (d.Length < 20 || !d[..4].SequenceEqual("KTBT"u8)) throw new InvalidDataException($"{RelativePath}: {kind}");
        uint treeHash = UInt(d, 8);
        int count = Int(d, 16), q = 20;
        var nodes = new AiNode[count];
        for (int i = 0; i < count; i++)
        {
            uint flags = UInt(d, q), cls = UInt(d, q + 4);
            q += 8;
            uint? extra = null;
            if ((flags & 4) != 0) { extra = UInt(d, q); q += 4; }
            int parent = Int(d, q), children = Int(d, q + 4), n = Int(d, q + 8);
            q += 12;
            if (n < 0 || n > MaxParams || parent >= i || parent < -1) throw new InvalidDataException($"{RelativePath}: {kind} #{i}");
            int[] parameters = Ints(d, q, n);
            q += 4 * n;
            string? text = null;
            if ((flags & 2) != 0)
            {
                int length = Int(d, q);
                text = Encoding.GetEncoding(932).GetString(d.Slice(q + 4, length));
                q += 4 + length;
            }
            int[]? array = null;
            if ((flags & 8) != 0)
            {
                int length = Int(d, q);
                array = Ints(d, q + 4, length);
                q += 4 + 4 * length;
            }
            nodes[i] = new AiNode(i, flags, cls, extra, parent, children, parameters, text, array);
        }
        foreach (AiNode node in nodes)
            if (node.Parent >= 0) nodes[node.Parent].Children.Add(node);
        return new AiTree(kind, chunkHash, treeHash, nodes);
    }

    private static int Int(ReadOnlySpan<byte> d, int at) =>
        at >= 0 && at + 4 <= d.Length ? BinaryPrimitives.ReadInt32LittleEndian(d[at..]) : throw new InvalidDataException($"{RelativePath}: {at:X}");

    private static uint UInt(ReadOnlySpan<byte> d, int at) => unchecked((uint)Int(d, at));

    private static int[] Ints(ReadOnlySpan<byte> d, int at, int count)
    {
        if (count < 0 || at < 0 || at + 4L * count > d.Length) throw new InvalidDataException($"{RelativePath}: {at:X}");
        var values = new int[count];
        for (int k = 0; k < count; k++) values[k] = BinaryPrimitives.ReadInt32LittleEndian(d[(at + 4 * k)..]);
        return values;
    }
}

// How a Sequential node runs its children (0xB2BC40): in file order, by each child's priority (0xB2C360) or shuffled
// (0xB2C460); stopping at the first success (Selector), at the first failure (Sequence) or never (All); and all of them in
// one tick or one a tick. Its params: order (+0x28), rule (+0x2C), whole tick (+0x30).
internal enum AiSequenceOrder { InOrder, ByPriority, Shuffled }
internal enum AiSequenceRule { Selector, Sequence, All }

internal readonly record struct AiSequence(AiSequenceOrder Order, AiSequenceRule Rule, bool WholeTick)
{
    internal static AiSequence Of(int[] p) => new((AiSequenceOrder)p[0], (AiSequenceRule)p[1], p[2] != 0);
}

// A term of a condition: an AND or OR of the next Group terms, or one predicate (AiCatalog.Predicate) on its args.
internal enum AiTermKind { All, Any, Predicate }

// FirstArg is where its args start among the node's params, so a number in a condition is a param like any other.
internal readonly record struct AiTerm(AiTermKind Kind, bool Negated, int Function, int Group, int[] Args, int FirstArg);

// How a condition node runs its child (0xB2F880): once if it holds (If, failing when it does not), again while it holds
// (While) or until it holds (Until).
internal enum AiConditionMode { If, While, Until }

// A ConditionDecorator's params: inverted (+0x28), all must hold (+0x29; else one is enough), mode (+0x2A), the number
// of terms (+0x2B), then each term as (kind, negated, function, group, argc, args...). 0xB2F0D0 judges it.
internal sealed record AiCondition(bool Inverted, bool AllMustHold, AiConditionMode Mode, AiTerm[] Terms)
{
    internal const int HeadLength = 4, TermHeadLength = 5;

    internal static AiCondition Of(int[] p)
    {
        if (p.Length < HeadLength || p[3] < 0) throw new InvalidDataException("condition");
        var terms = new AiTerm[p[3]];
        int q = HeadLength;
        for (int t = 0; t < terms.Length; t++)
        {
            if (q + TermHeadLength > p.Length) throw new InvalidDataException("condition term");
            int argc = p[q + 4];
            if (argc < 0 || q + TermHeadLength + argc > p.Length) throw new InvalidDataException("condition term");
            terms[t] = new AiTerm((AiTermKind)p[q], p[q + 1] != 0, p[q + 2], p[q + 3], p[(q + TermHeadLength)..(q + TermHeadLength + argc)], q + TermHeadLength);
            q += TermHeadLength + argc;
        }
        if (q != p.Length) throw new InvalidDataException("condition length");
        return new AiCondition(p[0] != 0, p[1] != 0, (AiConditionMode)p[2], terms);
    }

    // Which term and which of its args a param of the node is; null for the head or a term's own fields.
    internal (int Term, int Arg)? ArgAt(int param)
    {
        for (int t = 0; t < Terms.Length; t++)
        {
            int a = param - Terms[t].FirstArg;
            if (a >= 0 && a < Terms[t].Args.Length) return (t, a);
        }
        return null;
    }

    // Which term's own field (0 kind, 1 negated, 2 function, 3 group, 4 argc) a param is; null when it is not one.
    internal (int Term, int Field)? TermFieldAt(int param)
    {
        for (int t = 0; t < Terms.Length; t++)
        {
            int f = param - (Terms[t].FirstArg - TermHeadLength);
            if (f >= 0 && f < TermHeadLength) return (t, f);
        }
        return null;
    }
}

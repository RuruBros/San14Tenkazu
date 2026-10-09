using System.Buffers.Binary;

namespace San14Tenkazu;

// A tree in the running game: its root and every node's address, in file order.
internal sealed record AiLiveTree(AiTree File, ulong Root, ulong[] Nodes);

// Where one param of a node lives on the heap, and how wide it is there (1 or 4 bytes).
internal readonly record struct AiCell(ulong Address, int Size);

// The trees on the game's heap (source/Reverse/notes/ai.md). The game builds them once, when it starts (AI_Init
// 0xC0150), from its file; every AI force runs them from there, so a number changed there is what the next decision
// reads. exe + ManagerPointerRva holds the CAIManager, whose +8 is the CJuicyBehaviorTreeManager with the four roots at
// +0x10.. in file order; a root keeps its node count at +0x38 and the nodes' addresses at +0x40, in file order; each
// node its class hash at +8. Where a node keeps its params: a leaf by its class (AiClass.Offsets), a Sequential or a
// StaticLink as ints from +0x28, a condition its head as bytes from +0x28 and its terms at [+0x30], 0x18 bytes each
// (+8 kind, +9 negated, +0xA group, +0xB argc as bytes, +0xC function, +0x10 the address of its args).
internal static class AiMemory
{
    internal const ulong ManagerPointerRva = 0x1B9A4A0;
    private const int ManagerOffset = 8, RootsOffset = 0x10, RootCountOffset = 0x38, RootNodesOffset = 0x40, NodeClassOffset = 8;
    private const int CompositeParams = 0x28, ConditionTerms = 0x30, TermSize = 0x18, TermArgs = 0x10;
    private static readonly (int Offset, int Size)[] TermFields = [(8, 1), (9, 1), (0xC, 4), (0xA, 1), (0xB, 1)];

    // The four trees, each node checked to be of the class the file gives it; InvalidDataException when they are not
    // the file's (another build, or the game still starting).
    internal static AiLiveTree[] Read(IRecordMemory memory, ulong moduleBase, AiTree[] file)
    {
        ulong manager = Pointer(memory, Pointer(memory, moduleBase + ManagerPointerRva) + ManagerOffset);
        var trees = new AiLiveTree[file.Length];
        for (int k = 0; k < file.Length; k++)
        {
            ulong root = Pointer(memory, manager + RootsOffset + (ulong)(8 * k));
            ulong count = BitConverter.ToUInt64(memory.Read(root + RootCountOffset, 8));
            if (count != (ulong)file[k].Nodes.Length) throw new InvalidDataException($"AI tree {k}: {count} nodes, the file has {file[k].Nodes.Length}");
            byte[] table = memory.Read(Pointer(memory, root + RootNodesOffset), 8 * (int)count);
            var nodes = new ulong[count];
            for (int i = 0; i < nodes.Length; i++)
            {
                nodes[i] = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(8 * i));
                uint cls = BinaryPrimitives.ReadUInt32LittleEndian(memory.Read(nodes[i] + NodeClassOffset, 4));
                if (cls != file[k].Nodes[i].Class) throw new InvalidDataException($"AI tree {k} #{i}: class {cls:X8}, the file has {file[k].Nodes[i].Class:X8}");
            }
            if (nodes[0] != root) throw new InvalidDataException($"AI tree {k}: the root is not its first node");
            trees[k] = new AiLiveTree(file[k], root, nodes);
        }
        return trees;
    }

    // Where a param of a node lives; null where the node keeps it its own way (a leaf whose loader is its own).
    internal static AiCell? Cell(IRecordMemory memory, AiLiveTree tree, int node, int param)
    {
        AiNode n = tree.File.Nodes[node];
        ulong at = tree.Nodes[node];
        if (param < 0 || param >= n.Params.Length) return null;
        if (n.Class == AiCatalog.Condition)
        {
            if (param < AiCondition.HeadLength) return new AiCell(at + CompositeParams + (ulong)param, 1);
            AiCondition condition = AiCondition.Of(n.Params);
            ulong terms = Pointer(memory, at + ConditionTerms);
            if (condition.ArgAt(param) is (int t, int a))
                return new AiCell(Pointer(memory, terms + (ulong)(t * TermSize + TermArgs)) + (ulong)(4 * a), 4);
            if (condition.TermFieldAt(param) is (int term, int field))
                return new AiCell(terms + (ulong)(term * TermSize + TermFields[field].Offset), TermFields[field].Size);
            return null;
        }
        if (n.Class is AiCatalog.Sequential or AiCatalog.StaticLink) return new AiCell(at + CompositeParams + (ulong)(4 * param), 4);
        // These loaders copy consecutive ints from +0x38, but gate optional fields by the file's param count.
        // 0x92AE0: alliance p5/6 only for 7; 0x92B10: development p2..4 only for 5;
        // 0x92BC0: strength p3/4/5 only when there are at least 4/5/6. See ai_decisions.py loaders/live.
        int loaded = n.Class switch
        {
            0xBB940C54 => n.Params.Length == 7 ? 7 : 5, // CAllianceSuccessConditionNode
            0x874694D6 => n.Params.Length == 5 ? 5 : 2, // CDomesticPriorityDevelopmentSelectNode
            0x184B7500 => Math.Min(n.Params.Length, 6), // CGetFootholdFightingStrengthNode
            _ => -1,
        };
        if (loaded >= 0) return param < loaded ? new AiCell(at + 0x38 + (ulong)(4 * param), 4) : null;
        return AiCatalog.Class(n.Class)?.Offsets is { } offsets && param < offsets.Length ? new AiCell(at + (ulong)offsets[param], 4) : null;
    }

    // A param as the game holds it now; null where it cannot be told.
    internal static int? Value(IRecordMemory memory, AiLiveTree tree, int node, int param) =>
        Cell(memory, tree, node, param) is { } cell ? Read(memory, cell) : null;

    internal static int Read(IRecordMemory memory, AiCell cell)
    {
        byte[] data = memory.Read(cell.Address, cell.Size);
        return cell.Size == 1 ? data[0] : BinaryPrimitives.ReadInt32LittleEndian(data);
    }

    // Every param the game holds otherwise than the file, where it can be told: what was changed in this run.
    internal static List<(int Node, int Param, int File, int Memory)> Differences(IRecordMemory memory, AiLiveTree tree)
    {
        var found = new List<(int, int, int, int)>();
        foreach (AiNode node in tree.File.Nodes)
            for (int p = 0; p < node.Params.Length; p++)
                if (Value(memory, tree, node.Index, p) is int now && now != Expected(node, p))
                    found.Add((node.Index, p, node.Params[p], now));
        return found;
    }

    // A param as the file gives it, read back the width the game keeps it in (a condition's bytes).
    private static int Expected(AiNode node, int param) =>
        node.Class == AiCatalog.Condition && (param < AiCondition.HeadLength || AiCondition.Of(node.Params).TermFieldAt(param) is (_, not 2))
            ? node.Params[param] & 0xFF : node.Params[param];

    private static ulong Pointer(IRecordMemory memory, ulong address)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(memory.Read(address, 8));
        return value != 0 ? value : throw new InvalidDataException($"AI: null pointer at {address:X}");
    }
}

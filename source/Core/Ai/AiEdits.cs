using System.Buffers.Binary;

namespace San14Tenkazu;

// Writing the guide's numbers (AiGuide) to the trees on the game's heap, through the editor's own write path
// (GameMemory.Apply: each record checked to be the one read; [되돌리기] after). A number goes to every place the guide
// names it and to every copy of its block in the other corps types' branches (AiValues.Counterparts), so one rule stays
// one rule whatever type a corps has. A leaf's param is a field of its node (RecordKind.AiNode); a condition's arg one
// of its term's args, kept apart from the node (RecordKind.AiArgs), so the node is checked here instead.
internal static class AiEdits
{
    internal const int NodeIdentity = 0x10;   // a node's vtable, class hash and flags
    private const int NodeRead = 0x60;        // a leaf's params from +0x38: the guide's reach p7 at +0x54

    // What may be typed for a number: 0 to ten times the file's value (at least 100), as the guide reads it.
    internal static (long Min, long Max) Range(AiGuideValue value, int fileRaw) => (value.Shown(0), value.Shown(MaxRaw(fileRaw)));

    private static int MaxRaw(int fileRaw) => Math.Max(fileRaw * 10, 100);

    // The value the trees hold for one shown; null between the steps of a scaled one (85 for a number shown ×10).
    internal static int? Raw(AiGuideValue value, long shown)
    {
        long raw = shown - value.Shift;
        if (raw % value.Scale != 0) return null;
        raw /= value.Scale;
        return raw is >= int.MinValue and <= int.MaxValue ? (int)raw : null;
    }

    // Every place a number is kept: the guide's anchors and their copies, each once.
    internal static List<AiSlot> Slots(AiTree[] trees, AiGuideValue value)
    {
        var slots = new List<AiSlot>();
        foreach (AiAnchor anchor in value.Anchors)
            if (anchor.Slot(trees) is { } slot)
                foreach (AiSlot s in AiValues.Counterparts(trees[(int)slot.Tree], slot).Prepend(slot))
                    if (!slots.Contains(s)) slots.Add(s);
        return slots;
    }

    // The file's value of a number (its first place).
    internal static int? FileRaw(AiTree[] trees, AiGuideValue value) =>
        value.Anchors[0].Slot(trees) is { } s ? trees[(int)s.Tree].Nodes[s.Node].Params[s.Param] : null;

    // A number as the game holds it in every place; null when they disagree or one cannot be read.
    internal static int? GameRaw(IRecordMemory memory, AiTree[] trees, AiLiveTree[] live, AiGuideValue value)
    {
        int?[] now = Slots(trees, value).Select(s => AiMemory.Value(memory, live[(int)s.Tree], s.Node, s.Param)).Distinct().ToArray();
        return now.Length == 1 ? now[0] : null;
    }

    // The edits that put these raw values in every place of their numbers; a place already holding its value is left.
    // label names the write in the log and the question (the decision).
    internal static EditPlan Plan(IRecordMemory memory, AiTree[] trees, AiLiveTree[] live, string label, IReadOnlyList<(AiGuideValue Value, int Raw)> requests)
    {
        var records = new Dictionary<ulong, (RecordKind Kind, byte[] Expected, List<FieldChange> Changes)>();
        var lines = new List<string>();
        foreach ((AiGuideValue value, int raw) in requests)
        {
            List<AiSlot> slots = Slots(trees, value);
            if (slots.Count == 0 || FileRaw(trees, value) is not int fileRaw) return EditPlan.Refuse(L.Ai.ValueNotFound(value.Label));
            if (raw < 0 || raw > MaxRaw(fileRaw)) return EditPlan.Refuse(L.Ai.OutOfRange(value.Label, value.Shown(0), value.Shown(MaxRaw(fileRaw))));
            int? before = GameRaw(memory, trees, live, value);
            foreach (AiSlot slot in slots)
            {
                AiLiveTree tree = live[(int)slot.Tree];
                AiNode node = tree.File.Nodes[slot.Node];
                ulong at = tree.Nodes[slot.Node];
                if (BinaryPrimitives.ReadUInt32LittleEndian(memory.Read(at + 8, 4)) != node.Class) return EditPlan.Refuse(L.Ai.TreeChanged);
                if (AiMemory.Cell(memory, tree, slot.Node, slot.Param) is not { Size: 4 } cell) return EditPlan.Refuse(L.Ai.ValueNotFound(value.Label));
                (RecordKind kind, ulong address, int length) = (RecordKind.AiNode, at, NodeRead);
                if (node.Class == AiCatalog.Condition)
                {
                    AiCondition condition = AiCondition.Of(node.Params);
                    if (condition.ArgAt(slot.Param) is not (int t, int a)) return EditPlan.Refuse(L.Ai.ValueNotFound(value.Label));
                    (kind, address, length) = (RecordKind.AiArgs, cell.Address - (ulong)(4 * a), 4 * condition.Terms[t].Args.Length);
                }
                int offset = (int)(cell.Address - address);
                if (offset < 0 || offset + 4 > length) return EditPlan.Refuse(L.Ai.ValueNotFound(value.Label));
                if (!records.TryGetValue(address, out var record)) records[address] = record = (kind, memory.Read(address, length), []);
                var field = new FieldDef(value.Label, kind, offset, FieldKind.Int32, 0, MaxRaw(fileRaw));
                if (field.Read(record.Expected) != raw) record.Changes.Add(new FieldChange(field, raw));
            }
            string from = before is int b ? AiText.Number(value.Shown(b)) + value.Unit : L.AiGuide.Differs;
            lines.Add(L.Ai.EditLine(value.Label, from, AiText.Number(value.Shown(raw)) + value.Unit, slots.Count));
        }
        List<RecordEdit> edits = records.Where(r => r.Value.Changes.Count > 0)
            .Select(r => new RecordEdit(r.Value.Kind, r.Key, r.Value.Expected, label, r.Value.Changes)).ToList();
        return new EditPlan(edits, string.Join("\n", lines));
    }

    // The numbers whose game value is not the file's, as raw values by key (AiAdjustments.Key): what to keep.
    internal static Dictionary<string, int> Changed(IRecordMemory memory, AiTree[] trees, AiLiveTree[] live)
    {
        var changed = new Dictionary<string, int>();
        foreach (AiGuideValue value in AiGuide.Values)
            if (GameRaw(memory, trees, live, value) is int now && FileRaw(trees, value) is int file && now != file)
                changed[AiAdjustments.Key(value)] = now;
        return changed;
    }

    // What to write again of the numbers kept: those the game holds at the file's value again (it started over from
    // its file); and those it holds at a third value, left as they are and told.
    internal static (List<(AiGuideValue Value, int Raw)> Write, List<(AiGuideValue Value, int? Now, int Kept)> Other) Reapply(
        IRecordMemory memory, AiTree[] trees, AiLiveTree[] live, IReadOnlyDictionary<string, int> kept)
    {
        var write = new List<(AiGuideValue, int)>();
        var other = new List<(AiGuideValue, int?, int)>();
        foreach (AiGuideValue value in AiGuide.Values)
        {
            if (!kept.TryGetValue(AiAdjustments.Key(value), out int raw)) continue;
            int? now = GameRaw(memory, trees, live, value);
            if (now == raw) continue;
            if (now is int n && n == FileRaw(trees, value)) write.Add((value, raw));
            else other.Add((value, now, raw));
        }
        return (write, other);
    }
}

// The guide numbers the user changed, kept beside the editor so they go in again when the game starts over from its
// file (the trees are built once, when the game starts). The first line is the game file's stamp (AiTreeFile.Stamp):
// numbers kept for another edition of the file are not used. Then "<key> <raw>" a number, the key being where the
// guide first finds it (tree:node:term:arg).
internal sealed class AiAdjustments(string path)
{
    internal static string Key(AiGuideValue value)
    {
        AiAnchor a = value.Anchors[0];
        return $"{(int)a.Tree}:{a.Node}:{a.Term}:{a.Arg}";
    }

    internal Dictionary<string, int> Load(string stamp)
    {
        var kept = new Dictionary<string, int>();
        try
        {
            if (!File.Exists(path)) return kept;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != stamp) return kept;
            foreach (string line in lines.Skip(1))
                if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var key, var raw] && int.TryParse(raw, out int value)) kept[key] = value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return kept;
    }

    // Throws IOException or UnauthorizedAccessException when it cannot be written.
    internal void Save(string stamp, IReadOnlyDictionary<string, int> kept)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, kept.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}").Prepend(stamp));
    }
}

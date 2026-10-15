namespace San14Tenkazu;

// A card owns its table inputs, including inputs whose row is temporarily absent or in another status group.
// Keep the actual NumberField so invalid text survives navigation just like the card's regular inputs.
internal sealed class TableDrafts
{
    private readonly Dictionary<(EditorTarget Target, FieldDef Field), NumberField> numbers = [];
    internal event Action? Changed;
    internal int Pending => numbers.Values.Count(n => n.IsChanged);
    internal bool HasKeyboardFocus => numbers.Values.Any(n => n.Box.IsKeyboardFocusWithin);
    internal (EditorTarget Target, FieldDef Field)? InvalidCell { get; private set; }

    internal NumberField Get(EditorTarget target, FieldDef field, long current)
    {
        if (numbers.TryGetValue((target, field), out var number)) return number;
        number = new NumberField(field);
        number.Show(current);
        number.Edited += () => Changed?.Invoke();
        numbers.Add((target, field), number);
        return number;
    }

    internal NumberField? Find(EditorTarget target, FieldDef field) => numbers.GetValueOrDefault((target, field));

    internal void Revert(NumberField number)
    {
        number.Show(number.Current);
        Changed?.Invoke();
    }

    internal void Follow(EditorTarget target, FieldDef field, long current)
    {
        if (Find(target, field) is not { } number) return;
        bool changed = number.IsChanged;
        number.Follow(current);
        if (changed != number.IsChanged) Changed?.Invoke();
    }

    internal void Clear()
    {
        InvalidCell = null;
        numbers.Clear();
        Changed?.Invoke();
    }

    internal List<(EditorTarget Target, List<FieldChange> Changes)>? Changes(out string error)
    {
        error = "";
        InvalidCell = null;
        var changes = new Dictionary<EditorTarget, List<FieldChange>>();
        foreach (var (key, number) in numbers.Where(p => p.Value.IsChanged))
        {
            if (!number.TryRead(out long value, out error)) { InvalidCell = key; return null; }
            if (!changes.TryGetValue(key.Target, out var fields)) changes.Add(key.Target, fields = []);
            fields.Add(new(key.Field, value));
        }
        return changes.Select(p => (p.Key, p.Value)).ToList();
    }
}

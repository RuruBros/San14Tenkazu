namespace San14Tenkazu;

// A card owns its table inputs, including inputs whose row is temporarily absent or in another status group.
// Keep the actual NumberField so invalid text survives navigation just like the card's regular inputs.
internal sealed class TableDrafts
{
    private readonly Dictionary<(EditorTarget Target, FieldDef Field), NumberField> numbers = [];
    internal event Action? Changed;
    internal bool HasInputs => numbers.Count > 0;
    internal int Pending => numbers.Values.Count(n => n.IsChanged);
    internal bool HasKeyboardFocus => numbers.Values.Any(n => n.Box.IsKeyboardFocusWithin);
    internal (EditorTarget Target, FieldDef Field)? InvalidCell { get; private set; }

    internal NumberField Get(EditorTarget target, FieldDef field, long current)
    {
        if (numbers.TryGetValue((target, field), out var number)) return number;
        number = new NumberField(field);
        number.Show(current);
        number.Edited += () =>
        {
            Release(target, number);
            Changed?.Invoke();
        };
        numbers.Add((target, field), number);
        return number;
    }

    internal NumberField? Find(EditorTarget target, FieldDef field) => numbers.GetValueOrDefault((target, field));

    // An attached box may still receive keyboard input even if its current text matches the game.
    internal void Release(EditorTarget target, NumberField number)
    {
        if (number.Box.Parent is null && !number.IsChanged && ReferenceEquals(Find(target, number.Field), number))
            numbers.Remove((target, number.Field));
    }

    internal void Revert(NumberField number)
    {
        number.Show(number.Current);
        Changed?.Invoke();
    }

    internal void Follow(EditorTarget target, FieldDef field, long current)
    {
        if (Find(target, field) is not { } number || number.Current == current) return;
        bool changed = number.IsChanged;
        number.Follow(current);
        Release(target, number);
        if (changed != number.IsChanged) Changed?.Invoke();
    }

    // Updating live values is independent of materializing table rows. Missing targets keep their
    // input so normal apply validation can reject the complete batch without losing the draft.
    internal void Follow(Func<EditorTarget, FieldDef, long?> read)
    {
        bool changed = false;
        foreach (var (key, number) in numbers)
            if (read(key.Target, key.Field) is { } current && current != number.Current)
            {
                bool before = number.IsChanged;
                number.Follow(current);
                Release(key.Target, number);
                changed |= before != number.IsChanged;
            }
        if (changed) Changed?.Invoke();
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

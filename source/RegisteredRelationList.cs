using W = System.Windows;
using WC = System.Windows.Controls;

namespace San14Tenkazu;

// One name picker adds to a visible list, as the live officer editor does. A picker choice
// alone is not a change: the explicit Add button commits it to the window's pending records.
internal sealed class RegisteredRelationList
{
    internal readonly WC.StackPanel Panel = new();
    internal readonly NameChoice Add = new(220);
    internal readonly PlateButton Take = new(L.Registered.AddRelation, icon: Icons.Apply);
    internal readonly WC.WrapPanel Members = new();
    private readonly WC.Border frame = new() { BorderBrush = CardSkin.Rule, BorderThickness = new W.Thickness(1), Padding = new W.Thickness(6), MinHeight = 32 };
    private Action<int>? addMember;

    internal RegisteredRelationList()
    {
        var row = new WC.WrapPanel { Margin = new W.Thickness(0, 4, 0, 4) };
        row.Children.Add(Add.Box);
        row.Children.Add(Take);
        Panel.Children.Add(row);
        frame.Child = Members;
        Panel.Children.Add(frame);
        Take.Click += () => { if (Add.Picked > 0) addMember?.Invoke(Add.Picked); };
        Add.Box.SelectionChanged += (_, _) => Take.IsEnabled = Add.Picked > 0;
    }

    internal void Show(GameName[] names, IEnumerable<int> members, IEnumerable<int> original, Action<int> add, Action<int> remove)
    {
        int[] ids = members.ToArray(), baseline = original.ToArray();
        addMember = add;
        Add.SetNames(names.Where(n => !ids.Contains(n.Id)));
        Add.Show(0);
        Take.IsEnabled = false;
        frame.Background = ids.Order().SequenceEqual(baseline.Order()) ? CardSkin.Paper : CardSkin.Pending;
        Members.Children.Clear();
        foreach (int id in ids)
        {
            int member = id;
            string label = GameCatalog.NameOf(names, id) is { Length: > 0 } name ? name : "#" + id;
            WC.Border chip = EditorCard.Chip(label, "", !baseline.Contains(id), () => remove(member));
            chip.Tag = member;
            Members.Children.Add(chip);
        }
        if (ids.Length == 0) Members.Children.Add(new WC.TextBlock { Text = L.Terms.None, Foreground = CardSkin.InkMuted });
    }
}

using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;

namespace San14Tenkazu;

// A 명품's editor card: the ability it adds and by how much (ItemEdits; 없음 takes the bonus to 0). Who holds it is
// changed on the officer card ([관계·명품]); the holder named under the title opens his card.
internal sealed class ItemEditorCard : EditorCard
{
    // Everything the card shows for a 명품: who holds it (0 none) or where it stands, and what it adds.
    internal sealed record View(int Index, string Name, int Owner, string OwnerName, string State, int Ability, long Bonus);

    private readonly NameChoice ability = new(120, none: false);
    private readonly NumberField bonus = new(ItemFields.Bonus, 100);
    private int pending;

    internal int Item { get; private set; }

    internal override EditorTarget? Target => Item == 0 ? null : new ItemTarget(Item);

    internal override int Pending => pending;

    internal ItemEditorCard()
    {
        Shield.Visibility = W.Visibility.Collapsed;
        ability.SetNames(Enumerable.Range(0, ItemFields.AbilityNames.Length).Select(a => new GameName(a, a == 0 ? L.Terms.None : ItemFields.AbilityNames[a], "")));
        var page = new WC.StackPanel();
        page.Children.Add(Heading(L.Item.AbilityHeading));
        page.Children.Add(Line(L.Item.Ability, ability.Box));
        page.Children.Add(Line(L.Item.Bonus, bonus.Box, new WC.TextBlock { Text = ItemFields.Bonus.Hint, Foreground = CardSkin.InkMuted, VerticalAlignment = W.VerticalAlignment.Center }));
        page.Children.Add(new WC.TextBlock
        {
            Text = L.Item.Note,
            Foreground = CardSkin.InkMuted, TextWrapping = W.TextWrapping.Wrap, Margin = new W.Thickness(0, 6, 0, 0)
        });
        AddPage(L.Item.Page, page);
        ability.Box.SelectionChanged += (_, _) => Mark();
        bonus.Edited += Mark;
    }

    internal void Open(View view)
    {
        if (view.Index != Item) StartOver();
        Item = view.Index;
        Title.Text = view.Name;
        ability.Show(view.Ability);
        bonus.Show(view.Bonus);
        Show(view);
        Mark();
    }

    // The game's values now: a box nobody typed in follows them.
    internal void Follow(View view)
    {
        if (view.Index != Item) return;
        bonus.Follow(view.Bonus);
        Show(view);
        Mark();
    }

    private void Show(View view) => Fill(Subtitle, new WD.Inline[]
    {
        new WD.Run(L.Item.Kind),
        view.Owner > 0 && view.OwnerName.Length > 0 ? Link(view.OwnerName, new OfficerTarget(view.Owner)) : new WD.Run(view.State)
    });

    // What to write, or null (with the reason in error and the box focused) when something typed is not valid.
    internal List<FieldChange>? Changes(out string error)
    {
        error = "";
        var changes = new List<FieldChange>();
        if (ability.Picked != ability.Current) changes.Add(new FieldChange(ItemFields.Ability, ability.Picked));
        if (bonus.IsChanged)
        {
            if (!bonus.TryRead(out long value, out error)) return null;
            changes.Add(new FieldChange(ItemFields.Bonus, value));
        }
        return changes;
    }

    private void Mark()
    {
        pending = (ability.Mark() ? 1 : 0) + (bonus.IsChanged ? 1 : 0);
        Count();
    }
}

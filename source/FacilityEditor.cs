using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;

namespace San14Tenkazu;

// The facility's current durability, reached by pressing its model, icon or label on the map.
internal sealed class FacilityEditorCard : EditorCard
{
    internal sealed record View(Facility Facility, string ForceName, System.Drawing.Color Colour);
    private readonly NumberField durability = new(FacilityFields.Durability, 110);
    internal FacilityTarget? Identity { get; private set; }
    internal string DisplayName => Title.Text;
    internal override EditorTarget? Target => Identity;
    internal override int Pending => durability.IsChanged ? 1 : 0;

    internal FacilityEditorCard()
    {
        OffersGoTo();
        var page = new WC.StackPanel();
        page.Children.Add(Line(FacilityFields.Durability.Label, durability.Box, new WC.TextBlock
        {
            Text = FacilityFields.Durability.Hint, Foreground = CardSkin.InkMuted,
            VerticalAlignment = W.VerticalAlignment.Center, TextWrapping = W.TextWrapping.Wrap, MaxWidth = 200
        }));
        page.Children.Add(Shortcut(L.Card.RevertInput, L.Card.RevertInputTip, () => { durability.Show(durability.Current); Count(); }, Icons.Revert));
        AddPage(L.Facility.Page, page);
        durability.Edited += Count;
    }

    internal void Open(View view)
    {
        FacilityTarget target = FacilityTarget.Of(view.Facility);
        if (Identity != target) StartOver();
        Identity = target;
        ShowSummary(view);
        durability.Show(view.Facility.Durability);
        Count();
    }

    // Preserve a typed value while the unedited box follows the game.
    internal void Follow(View view)
    {
        if (Identity?.Matches(view.Facility) != true) return;
        ShowSummary(view);
        durability.Follow(view.Facility.Durability);
        Count();
    }

    private void ShowSummary(View view)
    {
        Facility facility = view.Facility;
        Title.Text = $"{MapArt.FacilityName(facility.Kind)} · #{facility.Index}";
        Shield.Colour = view.Colour;
        var parts = new List<WD.Inline>
        {
            facility.Force > 0 ? Link(view.ForceName, new ForceTarget(facility.Force)) : new WD.Run(view.ForceName)
        };
        if (!facility.Built) parts.Add(new WD.Run(L.Map.Building));
        Fill(Subtitle, parts);
    }

    internal long? Changes(out string error) => durability.TryRead(out long value, out error) ? value : null;
}

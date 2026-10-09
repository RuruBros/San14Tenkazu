using W = System.Windows;

namespace San14Tenkazu;

// Where the shield beside each unit's token goes: of the places it could have, the one that costs least.
// Costs are counted in the shield's own area: 1 is all of it over an obstacle.
internal static class TokenPlaces
{
    internal readonly record struct Disc(W.Point Centre, double Radius);

    // The part of a token that shields keep off (a little less than its disc).
    internal static W.Rect Area(Disc token)
    {
        double r = token.Radius * 0.85;
        return new W.Rect(token.Centre.X - r, token.Centre.Y - r, r * 2, r * 2);
    }

    // A shield: at the token's upper left as usual, else the upper right, left or right. All four are close
    // to the token, so their order only decides between equal places.
    internal static (W.Rect Area, double Cost) Shield(Disc token, double width, double height, bool army,
        IReadOnlyCollection<W.Rect> obstacles, IReadOnlyCollection<W.Rect> placed)
        => Shield(token, width, height, army, obstacles, placed, out _);

    internal static (W.Rect Area, double Cost) Shield(Disc token, double width, double height, bool army,
        IReadOnlyCollection<W.Rect> obstacles, IReadOnlyCollection<W.Rect> placed, out int choice)
    {
        (W.Point p, double r) = token;
        double side = r * 0.95, rise = r * 0.85;
        Span<W.Rect> choices = stackalloc W.Rect[4];
        choices[0] = new(p.X - side - width / 2, p.Y - rise - height / 2, width, height);
        choices[1] = new(p.X + side - width / 2, p.Y - rise - height / 2, width, height);
        choices[2] = new(p.X - r * 1.25 - width / 2, p.Y - height / 2, width, height);
        choices[3] = new(p.X + r * 1.25 - width / 2, p.Y - height / 2, width, height);
        if (!army) { choices[1] = choices[2]; choices[2] = choices[3]; choices = choices[..3]; }
        W.Rect own = Area(token), best = choices[0];
        double least = double.PositiveInfinity;
        choice = 0;
        for (int i = 0; i < choices.Length; i++)
        {
            W.Rect area = choices[i];
            double covered = 0;
            Covered(area, own, obstacles, 1, ref covered);
            Covered(area, W.Rect.Empty, placed, 3, ref covered);
            double cost = i * 0.001 + covered / (area.Width * area.Height);
            if (cost < least) { least = cost; best = area; choice = army || i == 0 ? i : i + 1; }
        }
        return (best, least);
    }

    internal static W.Rect AtChoice(Disc token, double width, double height, int choice)
    {
        (W.Point p, double r) = token;
        double side = r * 0.95, rise = r * 0.85;
        return choice switch
        {
            0 => new(p.X - side - width / 2, p.Y - rise - height / 2, width, height),
            1 => new(p.X + side - width / 2, p.Y - rise - height / 2, width, height),
            2 => new(p.X - r * 1.25 - width / 2, p.Y - height / 2, width, height),
            3 => new(p.X + r * 1.25 - width / 2, p.Y - height / 2, width, height),
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };
    }

    private static void Covered(W.Rect area, W.Rect own, IReadOnlyCollection<W.Rect> taken, double weight, ref double covered)
    {
        if (taken is MapAreas indexed)
        {
            foreach (W.Rect other in indexed.Near(area))
                if (other != own && W.Rect.Intersect(other, area) is { IsEmpty: false } both)
                    covered += both.Width * both.Height * weight;
        }
        else
            foreach (W.Rect other in taken)
                if (other != own && W.Rect.Intersect(other, area) is { IsEmpty: false } both)
                    covered += both.Width * both.Height * weight;
    }
}

namespace San14Tenkazu;

// Lighter and darker shades of a colour, worked out as Windows Forms' ControlPaint.Light and Dark do (hue, light and
// saturation on a 0~240 scale, moved towards the highlight or the shadow): the colours the editor has always had.
internal static class Shade
{
    private const int Range = 240, RgbMax = 255, Undefined = Range * 2 / 3, ShadowAdjust = -333, HighlightAdjust = 500;

    // percent 0: the colour itself; 1: the light end (Light) or black (Dark).
    internal static Color Light(Color color, float percent)
    {
        (int hue, int light, int saturation) = Hls(color);
        int one = Scaled(light, HighlightAdjust);
        return FromHls(hue, light + (int)((one - light) * percent), saturation);
    }

    internal static Color Dark(Color color, float percent)
    {
        (int hue, int light, int saturation) = Hls(color);
        int zero = Scaled(light, ShadowAdjust);
        return FromHls(hue, zero - (int)(zero * percent), saturation);
    }

    private static int Scaled(int light, int n) =>
        n > 0 ? (int)((light * (1000 - n) + (Range + 1L) * n) / 1000) : light * (n + 1000) / 1000;

    private static (int Hue, int Light, int Saturation) Hls(Color color)
    {
        int r = color.R, g = color.G, b = color.B;
        int max = Math.Max(Math.Max(r, g), b), min = Math.Min(Math.Min(r, g), b), sum = max + min, dif = max - min;
        int light = (sum * Range + RgbMax) / (2 * RgbMax);
        if (dif == 0) return (Undefined, light, 0);
        int saturation = light <= Range / 2 ? (dif * Range + sum / 2) / sum : (dif * Range + (2 * RgbMax - sum) / 2) / (2 * RgbMax - sum);
        int rd = ((max - r) * (Range / 6) + dif / 2) / dif, gd = ((max - g) * (Range / 6) + dif / 2) / dif, bd = ((max - b) * (Range / 6) + dif / 2) / dif;
        int hue = r == max ? bd - gd : g == max ? Range / 3 + rd - bd : 2 * Range / 3 + gd - rd;
        if (hue < 0) hue += Range;
        if (hue > Range) hue -= Range;
        return (hue, light, saturation);
    }

    private static Color FromHls(int hue, int light, int saturation)
    {
        if (saturation == 0)
        {
            int grey = light * RgbMax / Range;
            return Color.FromArgb((byte)grey, (byte)grey, (byte)grey);
        }
        int magic2 = light <= Range / 2 ? (light * (Range + saturation) + Range / 2) / Range : light + saturation - (light * saturation + Range / 2) / Range;
        int magic1 = 2 * light - magic2;
        byte Channel(int h) => (byte)((HueToRgb(magic1, magic2, h) * RgbMax + Range / 2) / Range);
        return Color.FromArgb(Channel(hue + Range / 3), Channel(hue), Channel(hue - Range / 3));
    }

    private static int HueToRgb(int n1, int n2, int hue)
    {
        if (hue < 0) hue += Range;
        if (hue > Range) hue -= Range;
        if (hue < Range / 6) return n1 + ((n2 - n1) * hue + Range / 12) / (Range / 6);
        if (hue < Range / 2) return n2;
        if (hue < Range * 2 / 3) return n1 + ((n2 - n1) * (Range * 2 / 3 - hue) + Range / 12) / (Range / 6);
        return n1;
    }
}

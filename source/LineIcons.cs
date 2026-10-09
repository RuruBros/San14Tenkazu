using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

namespace San14Tenkazu;

// Line icons for the buttons the game's art has no picture for, from Lucide (lucide.dev): check, chevrons-up
// and plus come from Feather (MIT, Cole Bemis), the others are Lucide's own (ISC). Their licences are in
// THIRD-PARTY-NOTICES.txt beside the editor. Each is SVG path data on a 24 x 24 grid, drawn as a white
// 2-unit round-capped line, like the game's white pictograms, so buttons tint them the same way.
internal static class LineIcons
{
    internal const string Prefix = "line:";

    private static readonly Dictionary<string, string[]> Paths = new()
    {
        ["plug"] = ["M12 22v-5", "M15 8V2", "M17 8a1 1 0 0 1 1 1v4a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V9a1 1 0 0 1 1-1z", "M9 8V2"],
        ["undo"] = ["M9 14 4 9l5-5", "M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5a5.5 5.5 0 0 1-5.5 5.5H11"],
        ["check"] = ["M20 6 9 17l-5-5"],
        ["calendar"] = ["M8 2v4", "M16 2v4", "M3 10h18", "M5 4h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z"],
        // Lucide's notebook-pen ([목록]) and flag (세력).
        ["notebook"] = ["M13.4 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-7.4", "M2 6h4", "M2 10h4", "M2 14h4", "M2 18h4",
            "M21.378 5.626a1 1 0 1 0-3.004-3.004l-5.01 5.012a2 2 0 0 0-.506.854l-.837 2.87a.5.5 0 0 0 .62.62l2.87-.837a2 2 0 0 0 .854-.506z"],
        ["flag"] = ["M4 22V4a1 1 0 0 1 .4-.8A6 6 0 0 1 8 2c3 0 5 2 7.333 2q2 0 3.067-.8A1 1 0 0 1 20 4v10a1 1 0 0 1-.4.8A6 6 0 0 1 16 16c-3 0-5-2-8-2a6 6 0 0 0-4 1.528"],
        // As Lucide's chevrons-up, rotate-ccw, maximize-2 and plus.
        ["fill"] = ["m17 11-5-5-5 5", "m17 18-5-5-5 5"],
        ["revert"] = ["M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8", "M3 3v5h5"],
        ["details"] = ["M15 3h6v6", "M9 21H3v-6", "M21 3l-7 7", "M3 21l7-7"],
        ["heal"] = ["M5 12h14", "M12 5v14"],
        // Lucide's sparkles (개성) and gem (명품).
        ["sparkles"] = ["m12 3-1.912 5.813a2 2 0 0 1-1.275 1.275L3 12l5.813 1.912a2 2 0 0 1 1.275 1.275L12 21l1.912-5.813a2 2 0 0 1 1.275-1.275L21 12l-5.813-1.912a2 2 0 0 1-1.275-1.275L12 3Z",
            "M5 3v4", "M19 17v4", "M3 5h4", "M17 19h4"],
        ["gem"] = ["M6 3h12l4 6-10 13L2 9Z", "M11 3 8 9l4 13 4-13-3-6", "M2 9h20"],
        // Lucide's scroll-text (고대무장) and lock-open ([모두 해금]).
        ["scroll"] = ["M15 12h-5", "M15 8h-5", "M19 17V5a2 2 0 0 0-2-2H4",
            "M8 21h12a2 2 0 0 0 2-2v-1a1 1 0 0 0-1-1H11a1 1 0 0 0-1 1v1a2 2 0 1 1-4 0V5a2 2 0 1 0-4 0v2a1 1 0 0 0 1 1h3"],
        ["unlock"] = ["M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2z", "M7 11V7a5 5 0 0 1 9.9-1"],
        // Lucide's locate-fixed ([위치로 가기]).
        ["locate"] = ["M2 12h3", "M19 12h3", "M12 2v3", "M12 19v3", "M19 12a7 7 0 1 1-14 0a7 7 0 1 1 14 0", "M15 12a3 3 0 1 1-6 0a3 3 0 1 1 6 0"],
        // Lucide's network ([AI]: the behaviour trees), its rectangles as paths.
        ["network"] = ["M17 16h4a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1h-4a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1z", "M3 16h4a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1z",
            "M10 2h4a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1h-4a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1z", "M5 16v-3a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v3", "M12 12V8"],
    };

    private const int Size = 48;   // pixels; buttons draw them smaller with smooth scaling

    // A white icon on clear, or null for an unknown name.
    internal static Bitmap? Render(string name)
    {
        if (!Paths.TryGetValue(name, out string[]? paths)) return null;
        var visual = new WM.DrawingVisual();
        using (WM.DrawingContext dc = visual.RenderOpen())
        {
            dc.PushTransform(new WM.ScaleTransform(Size / 24.0, Size / 24.0));
            var pen = new WM.Pen(WM.Brushes.White, 2) { StartLineCap = WM.PenLineCap.Round, EndLineCap = WM.PenLineCap.Round, LineJoin = WM.PenLineJoin.Round };
            foreach (string path in paths) dc.DrawGeometry(null, pen, WM.Geometry.Parse(path));
        }
        var target = new WMI.RenderTargetBitmap(Size, Size, 96, 96, WM.PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[Size * Size * 4];
        target.CopyPixels(pixels, Size * 4, 0);
        var bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppPArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
}

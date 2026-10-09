using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using W = System.Windows;
using WM = System.Windows.Media;

internal static partial class RenderChecks
{
    // Sample the actual native test window, independently of the private/shared GPU texture.
    private static (int Land, string Hash) CaptureMap(W.FrameworkElement surface, string? path)
    {
        W.Point topLeft = surface.PointToScreen(new W.Point(surface.ActualWidth / 4, surface.ActualHeight / 4));
        W.Point bottomRight = surface.PointToScreen(new W.Point(surface.ActualWidth * 3 / 4, surface.ActualHeight * 3 / 4));
        int width = (int)(bottomRight.X - topLeft.X), height = (int)(bottomRight.Y - topLeft.Y);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen((int)topLeft.X, (int)topLeft.Y, 0, 0, new Size(width, height));
        if (path is not null) bitmap.Save(path, ImageFormat.Png);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new byte[data.Stride * height];
        try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
        WM.Color background = ((WM.SolidColorBrush)surface.GetValue(System.Windows.Controls.Panel.BackgroundProperty)).Color;
        int land = 0;
        for (int y = 0; y < height; y += 12)
        for (int x = 0; x < width; x += 12)
        {
            int p = y * data.Stride + x * 4;
            if (Math.Abs(pixels[p] - background.B) > 2 || Math.Abs(pixels[p + 1] - background.G) > 2 || Math.Abs(pixels[p + 2] - background.R) > 2) land++;
        }
        return (land, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)));
    }
}

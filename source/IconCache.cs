using System.IO.Compression;
using System.Text;

namespace San14Tenkazu;

// Officer list icons made from the game's portraits, prepared once on this PC.
// %LOCALAPPDATA%\San14Tenkazu\icons_<size>.bin holds compressed BGRA pixels per face and the
// game's resource stamp; a different stamp (game updated) starts over. Faces from 7000 are the
// player's own portraits, which can change at any time, so they are never stored.
internal static class IconCache
{
    private const string Magic = "S14ICON2";

    // BGRA pixels, size by size (null when the portrait could not be read): the map makes brushes of them.
    internal sealed class Icon(byte[]? pixels, int size)
    {
        internal byte[]? Pixels { get; } = pixels;
        internal int Size { get; } = size;
    }

    private static string PathFor(int size) => Path.Combine(Program.DataFolder, $"icons_{size}.bin");

    internal static Dictionary<int, Icon> Load(string stamp, int size)
    {
        var icons = new Dictionary<int, Icon>();
        try
        {
            string path = PathFor(size);
            if (!File.Exists(path)) return icons;
            using var reader = new BinaryReader(new ZLibStream(File.OpenRead(path), CompressionMode.Decompress), Encoding.UTF8);
            if (reader.ReadString() != Magic || reader.ReadString() != stamp || reader.ReadInt32() != size) return icons;
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int face = reader.ReadInt32();
                byte[] pixels = reader.ReadBytes(size * size * 4);
                if (pixels.Length != size * size * 4) break;
                icons[face] = new Icon(pixels, size);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        return icons;
    }

    internal static void Save(string stamp, int size, IReadOnlyList<(int Face, byte[]? Pixels)> icons)
    {
        try
        {
            string path = PathFor(size);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            // Missing icons are not stored, so a face that failed to decode is tried again next time.
            var stored = icons.Where(i => i.Face < FaceLibrary.CustomFaceBase && i.Pixels is not null).ToList();
            using (var writer = new BinaryWriter(new ZLibStream(File.Create(temp), CompressionLevel.Fastest), Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(stamp);
                writer.Write(size);
                writer.Write(stored.Count);
                foreach ((int face, byte[]? pixels) in stored)
                {
                    writer.Write(face);
                    writer.Write(pixels!);
                }
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) { /* Only an optimisation: icons are decoded again next time. */ }
        catch (UnauthorizedAccessException) { }
    }
}

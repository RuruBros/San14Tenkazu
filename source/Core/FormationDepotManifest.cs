using static San14Tenkazu.SteamManifestWire;
using System.Text;

namespace San14Tenkazu;

// Read just the Korean fixed-data entry and the identity of a locally cached Steam depot manifest.
internal sealed record FormationDepotManifest(ulong Id, ulong Size, string ContentSha1)
{
    internal bool Matches(FormationFileData file) => !file.Decoded && Size == (ulong)file.Length && ContentSha1 == file.Sha1;

    internal static Dictionary<ulong, string> Find(string? directory)
    {
        var paths = new Dictionary<ulong, string>();
        foreach (string root in SteamInstallation.FindRoots(directory))
        foreach (string cache in new[] { Path.Combine(root, "depotcache"), Path.Combine(root, "steamapps", "depotcache") })
        {
            if (!Directory.Exists(cache)) continue;
            foreach (string file in Directory.EnumerateFiles(cache, $"{OfficialFormation.Depot}_*.manifest"))
            {
                string suffix = Path.GetFileNameWithoutExtension(file).Split('_')[1];
                if (ulong.TryParse(suffix, out ulong id)) paths.TryAdd(id, file);
            }
        }
        return paths;
    }

    internal static FormationDepotManifest Read(string path, ulong expected)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 16 or > 32 * 1024 * 1024) throw Invalid();
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        ReadOnlySpan<byte> rest = data;
        var files = new Reader(Section(ref rest, 0x71f617d0));
        var metadata = new Reader(Section(ref rest, 0x1f4812be));
        ulong depot = 0, id = 0;
        while (metadata.Next(out int tag, out var number, out _))
            switch (tag)
            {
                case 8: depot = number; break;
                case 16: id = number; break;
                case 32 when number != 0: throw Invalid(); // encrypted names
            }
        if (depot != OfficialFormation.Depot || id != expected) throw Invalid();
        FormationDepotManifest? found = null;
        while (files.Next(out int tag, out _, out var record))
        {
            if (tag != 10) continue;
            var row = new Reader(record);
            bool wanted = false;
            ulong size = 0;
            string? sha1 = null;
            while (row.Next(out int key, out ulong number, out var value))
                switch (key)
                {
                    case 10: wanted = Encoding.UTF8.GetString(value).Replace('\\', '/')
                        .Equals(FormationFileData.RelativePath, StringComparison.OrdinalIgnoreCase); break;
                    case 16: size = number; break;
                    case 42 when value.Length == 20: sha1 = Convert.ToHexString(value); break;
                }
            if (!wanted) continue;
            if (found is not null || sha1 is null || size == 0) throw Invalid();
            found = new(id, size, sha1);
        }
        return found ?? throw new InvalidDataException(L.FileComparison.MissingEntry);
    }

    private static InvalidDataException Invalid() => new(L.FileComparison.ManifestError);
}

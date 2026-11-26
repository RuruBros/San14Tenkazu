using System.Globalization;
using System.Text;

namespace San14Tenkazu;

// A snapshot of specific paths beside the connected executable. A file name or an INI value
// is useful for explaining a range, but neither establishes that the game used that range.
internal sealed record FeatureRangeEvidence(string[] Files, string[] Settings, bool Incomplete)
{
    internal static readonly FeatureRangeEvidence Unknown = new([], [], true);
    internal bool HasClues => Incomplete || Files.Length != 0 || Settings.Length != 0;
}

internal sealed record EditorRangeEvidence(FeatureRangeEvidence City, FeatureRangeEvidence Formation, FeatureRangeEvidence Personality)
{
    internal static readonly EditorRangeEvidence Unknown = new(FeatureRangeEvidence.Unknown, FeatureRangeEvidence.Unknown, FeatureRangeEvidence.Unknown);

    internal static EditorRangeEvidence AtConnection(string? executable)
    {
        if (string.IsNullOrEmpty(executable) || !Path.IsPathFullyQualified(executable)
            || !File.Exists(executable) || Path.GetDirectoryName(executable) is not { } root) return Unknown;
        return new(
            Read(root, ["SAN14_CityCaps.dll", "DLC/UserPatch/SAN14_CityCap.dll"],
                [("CityCaps", ["Mod.Enabled", "CityCaps.Mode"])]),
            Read(root, ["SAN14_FormationCap100.dll"],
                [("FormationCap100", ["Mod.Enabled", "Limits.Attack100", "Limits.Defense100"])]),
            Read(root, ["SAN14_Effect127.dll", "SAN14_Effect127Shield.dll", "SAN14_Effect128.dll"],
                [("Effect127", ["Mod.Enabled"]), ("Effect127Shield", ["Mod.Enabled", "Shield.Mode"]), ("Effect128", ["Mod.Enabled"])]));
    }

    private static FeatureRangeEvidence Read(string root, string[] modules, (string Name, string[] Keys)[] configs)
    {
        List<string> files = [], settings = [];
        bool incomplete = false;
        foreach (var path in modules) Probe(path, []);
        foreach (var (name, keys) in configs) Probe($"SAN14Mods/modules/{name}.ini", keys);
        return new([.. files], [.. settings], incomplete);

        void Probe(string relative, string[] keys)
        {
            try
            {
                string path = Path.Combine(root, relative);
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    incomplete = true;
                    return;
                }
                files.Add(relative);
                if (keys.Length == 0) return;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 64 * 1024) { incomplete = true; return; }
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                var entries = ReadSettings(reader, keys);
                foreach (string key in keys)
                    if (entries.TryGetValue(key, out int? value))
                    {
                        if (value.HasValue) settings.Add($"{Path.GetFileName(relative)} [{key[..key.IndexOf('.')]}] {key[(key.IndexOf('.') + 1)..]}={value.Value}");
                        else incomplete = true;
                    }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or System.Security.SecurityException)
            {
                incomplete = true;
            }
        }
    }

    private static Dictionary<string, int?> ReadSettings(TextReader reader, string[] keys)
    {
        var entries = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.StartsWith('['))
            {
                section = line.EndsWith(']') ? line[1..^1].Trim() : "";
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string key = section + "." + line[..equals].Trim();
            if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            entries[key] = !entries.ContainsKey(key) && int.TryParse(line[(equals + 1)..].Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
        }
        return entries;
    }
}

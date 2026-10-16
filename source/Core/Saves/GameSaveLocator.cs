using System.Collections.Concurrent;

namespace San14Tenkazu;

internal sealed record LoadedSaveMatch(GameSaveOfficers Save, int Matched, int Compared)
{
    internal double Ratio => Compared == 0 ? 0 : (double)Matched / Compared;
}

// Which Steam cloud save the running game was loaded from: the one whose officer records equal the
// live ones on everything the loader leaves alone. Files are only read, and each parsed file is kept
// by path, time and size so the periodic check does not decode it again.
internal static class GameSaveLocator
{
    internal const double MinimumMatch = 0.98;
    internal const double SettledMatch = 0.995;
    internal const int CandidateLimit = 16;
    private static readonly ConcurrentDictionary<(string Path, long Ticks, long Length), GameSaveOfficers?> cache = new();

    internal static IEnumerable<FileInfo> CandidateFiles(IEnumerable<string>? roots = null)
    {
        var files = new List<FileInfo>();
        foreach (string root in roots ?? SteamInstallation.FindRoots())
        {
            string userdata = Path.Combine(root, "userdata");
            if (!Directory.Exists(userdata)) continue;
            foreach (string account in Directory.EnumerateDirectories(userdata))
            {
                string remote = Path.Combine(account, SteamInstallation.AppId, "remote");
                if (!Directory.Exists(remote)) continue;
                files.AddRange(new DirectoryInfo(remote).EnumerateFiles("*.s14"));
            }
        }
        return files.OrderByDescending(f => f.LastWriteTimeUtc);
    }

    internal static GameSaveOfficers? Parse(FileInfo file, int officerCount)
    {
        var key = (file.FullName, file.LastWriteTimeUtc.Ticks, file.Length);
        if (cache.TryGetValue(key, out var cached)) return cached;
        GameSaveOfficers? parsed = null;
        try
        {
            byte[] bytes = File.ReadAllBytes(file.FullName);
            if (GameSaveFile.LooksLikeSave(bytes)) parsed = GameSaveFile.Read(bytes, file.FullName, file.LastWriteTimeUtc, officerCount);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException) { }
        if (cache.Count > 64) cache.Clear();
        cache[key] = parsed;
        return parsed;
    }

    internal static LoadedSaveMatch? Score(IReadOnlyList<byte[]?> records, GameSaveOfficers save)
    {
        if (save.Officers.Length != records.Count) return null;
        int matched = 0, compared = 0;
        for (int slot = 1; slot < records.Count; slot++)
        {
            if (records[slot] is not { } record) continue;
            compared++;
            if (GameSaveFile.SameOfficer(record, save.Officers[slot])) matched++;
        }
        return new(save, matched, compared);
    }

    // records: the live officer table by slot (null entries are skipped), slot 0 unused.
    internal static LoadedSaveMatch? FindLoaded(IReadOnlyList<byte[]?> records, IEnumerable<FileInfo>? candidates = null)
    {
        LoadedSaveMatch? best = null;
        foreach (var file in (candidates ?? CandidateFiles()).Take(CandidateLimit))
        {
            if (Parse(file, records.Count) is not { } save || Score(records, save) is not { } score) continue;
            if (best is null || score.Matched > best.Matched) best = score;
            if (best.Ratio >= SettledMatch) break;
        }
        return best is { } found && found.Ratio >= MinimumMatch ? found : null;
    }
}

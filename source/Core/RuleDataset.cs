using System.Security.Cryptography;

namespace San14Tenkazu;

internal static class RuleDataset
{
    internal static string Read(GameIdentity game)
        => Fingerprint(PathFor(game), game.StartTimeUtc);

    private static string PathFor(GameIdentity game)
    {
        string? executable = GameMemory.ExecutablePath(game);
        if (executable is null || !GameMemory.IsSameGame(game)) throw new IOException(L.Compatibility.RuleDatasetChanged);
        var directory = Path.GetDirectoryName(executable)!;
        return Path.Combine(directory, FormationFileData.RelativePath);
    }

    internal static string Fingerprint(string file, DateTime started)
    {
        using var input = Open(file, started);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static FileStream Open(string file, DateTime started)
    {
        var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (File.GetLastWriteTimeUtc(file) > started || input.Length == 0)
                throw new IOException(L.Compatibility.RuleDatasetChanged);
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    internal static IDisposable Guard(GameIdentity game, string digest) => Guard(PathFor(game), game.StartTimeUtc, digest);
    internal static IDisposable Guard(string file, DateTime started, string digest)
    {
        var input = Open(file, started);
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(digest, StringComparison.OrdinalIgnoreCase))
                throw new IOException(L.Compatibility.RuleDatasetChanged);
            return input;
        }
        catch { input.Dispose(); throw; }
    }
}

namespace San14Tenkazu;

// What the editor keeps about game builds the offsets were not verified on, by the exe's SHA-256, in its data folder:
// whether the user allowed writing to the build (allowed_builds.txt, one hash a line), and the exe's static pointers
// that lead to its tables (build_pointers.txt, "<hash> <rva>:<delta> ..."), so only its first connection searches
// memory. A file that cannot be read counts as empty; a failed save throws (IOException, UnauthorizedAccessException).
internal sealed class KnownBuilds(string folder)
{
    private string AllowedFile => Path.Combine(folder, "allowed_builds.txt");
    private string PointersFile => Path.Combine(folder, "build_pointers.txt");

    internal bool IsAllowed(string hash) => Lines(AllowedFile).Any(line => line.Trim().Equals(hash, StringComparison.OrdinalIgnoreCase));

    internal void Allow(string hash)
    {
        if (IsAllowed(hash)) return;
        Directory.CreateDirectory(folder);
        File.AppendAllLines(AllowedFile, [hash]);
    }

    internal List<BasePointer> Pointers(string hash)
    {
        try
        {
            string? line = Lines(PointersFile).FirstOrDefault(l => l.StartsWith(hash + " ", StringComparison.OrdinalIgnoreCase));
            return line is null ? [] : line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(part => part.Split(':'))
                .Select(p => new BasePointer(Convert.ToUInt64(p[0], 16), Convert.ToUInt64(p[1], 16))).ToList();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or IndexOutOfRangeException) { return []; }
    }

    // The pointers a connection found for the build, in place of what was kept before.
    internal void Remember(string hash, IReadOnlyList<BasePointer> pointers)
    {
        Directory.CreateDirectory(folder);
        List<string> lines = Lines(PointersFile).Where(l => !l.StartsWith(hash + " ", StringComparison.OrdinalIgnoreCase)).ToList();
        lines.Add(hash + " " + string.Join(" ", pointers.Select(p => $"{p.Rva:X}:{p.Delta:X}")));
        File.WriteAllLines(PointersFile, lines);
    }

    private static string[] Lines(string file)
    {
        try { return File.Exists(file) ? File.ReadAllLines(file) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}

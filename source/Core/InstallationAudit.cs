using System.Security;
using System.Text;
using T = San14Tenkazu.L.InstallationAudit;

namespace San14Tenkazu;

internal enum InstalledFileStatus { Matches, Changed, Added, Missing, Unknown }
internal enum PackageContentStatus { Found, NoMatch, Unknown }
internal enum DllOptionEvidence { Unobserved }

internal sealed record InstalledFileResult(string Path, InstalledFileStatus Status, FileProof? Disk,
    List<DepotEntry> Owners, string? Reason)
{
    public DllOptionEvidence? Runtime => InstallationAudit.RuntimeEvidence(Path);
}
internal sealed record PackageMember(string Root, FileProof File);
internal sealed record PackageFileResult(PackageMember Member, PackageContentStatus Status, string[] ContentPaths,
    string[] SameNamePaths, PackageMember[] OtherPackages)
{
    public DllOptionEvidence? Runtime => InstallationAudit.RuntimeEvidence(Member.File.Path);
}

internal sealed record InstallationAudit(InstallationDepotIndex Depots, InstalledFileResult[] Installed,
    PackageFileResult[] Packages, List<AuditIssue> Issues)
{
    internal static DllOptionEvidence? RuntimeEvidence(string path) =>
        Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? DllOptionEvidence.Unobserved : null;

    internal static bool IsReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException
        or InvalidDataException or DecoderFallbackException;

    internal static InstallationAudit Scan(string gameDirectory, IEnumerable<string> packageDirectories,
        CancellationToken token = default, Action<string>? progress = null, IEnumerable<string>? steamRoots = null)
    {
        token.ThrowIfCancellationRequested();
        string game = Normalize(gameDirectory);
        string[] packages = packageDirectories.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (packages.Any(p => Contains(p, game) || Contains(game, p)) ||
            packages.Any(p => packages.Any(q => p != q && Contains(p, q))))
            throw new ArgumentException(T.OverlappingRoots);

        var depots = InstallationDepotIndex.Load(game, steamRoots, token);
        var issues = new List<AuditIssue>(depots.Issues);
        var members = new List<PackageMember>();
        foreach (string folder in packages)
        {
            var package = InstallationFiles.Read(folder, null, token, progress);
            members.AddRange(package.Files.Values.Select(f => new PackageMember(folder, f)));
            AddIssues(folder, package);
        }
        var sizes = members.Where(p => p.File.Size.HasValue).Select(p => p.File.Size!.Value).ToHashSet();
        var disk = InstallationFiles.Read(game, sizes, token, progress);
        AddIssues(game, disk);
        depots.CheckRevision();

        var installed = new List<InstalledFileResult>();
        foreach (string path in depots.Files.Keys.Union(disk.Files.Keys, StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var owners = depots.Files.GetValueOrDefault(path) ?? [];
            var file = disk.Files.GetValueOrDefault(path);
            (InstalledFileStatus status, string? reason) = Compare(depots, disk, owners, file, path);
            installed.Add(new(path, status, file, owners, reason));
        }
        var byHash = disk.Files.Values.Where(f => f.Sha256 != null).ToLookup(f => (f.Size, f.Sha256));
        var byName = disk.Files.Values.ToLookup(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase);
        var packageNames = members.ToLookup(p => Path.GetFileName(p.File.Path), StringComparer.OrdinalIgnoreCase);
        bool fullDisk = disk.IsComplete;
        var compared = new List<PackageFileResult>();
        foreach (var member in members.OrderBy(p => p.Root, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(p => p.File.Path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var file = member.File;
            string name = Path.GetFileName(file.Path);
            string[] matches = file.Sha256 == null ? [] : byHash[(file.Size, file.Sha256)].Select(f => f.Path).ToArray();
            var status = matches.Length > 0 ? PackageContentStatus.Found :
                file.Error != null || !fullDisk ? PackageContentStatus.Unknown : PackageContentStatus.NoMatch;
            compared.Add(new(member, status, matches, byName[name].Select(f => f.Path).ToArray(),
                packageNames[name].Where(p => p.Root != member.Root).ToArray()));
        }
        token.ThrowIfCancellationRequested();
        return new(depots, installed.ToArray(), compared.ToArray(), issues);

        void AddIssues(string root, InstallationFiles tree)
        {
            issues.AddRange(tree.Issues.Select(i => new AuditIssue(Path.Combine(root, i.Source), i.Detail)));
            issues.AddRange(tree.Files.Values.Where(f => f.Error != null)
                .Select(f => new AuditIssue(Path.Combine(root, f.Path), f.Error!)));
        }
    }

    private static (InstalledFileStatus, string?) Compare(InstallationDepotIndex depots, InstallationFiles disk,
        List<DepotEntry> owners, FileProof? file, string path)
    {
        if (file == null && disk.HiddenByIssue(path)) return (InstalledFileStatus.Unknown, T.UnreadablePath);
        if (file?.Error is { } error) return (InstalledFileStatus.Unknown, error);
        if (owners.Any(e => e.Link)) return (InstalledFileStatus.Unknown, T.LinkSkipped);
        if (file == null) return (InstalledFileStatus.Missing, null);
        bool Matches(DepotEntry e) => e.Sha1 != null && e.Size == (ulong)file.Size!.Value && e.Sha1 == file.Sha1;
        if (owners.Any(Matches))
        {
            if (!owners.All(Matches)) return (InstalledFileStatus.Unknown, T.ConflictingClaims);
            return (InstalledFileStatus.Matches, depots.Complete ? null : T.KnownMatch);
        }
        if (!depots.Complete) return (InstalledFileStatus.Unknown, T.IncompleteDepots);
        if (owners.Count == 0) return (InstalledFileStatus.Added, null);
        if (owners.Any(e => e.Sha1 == null && e.Size == (ulong)file.Size!.Value))
            return (InstalledFileStatus.Unknown, T.NoContentHash);
        return (InstalledFileStatus.Changed, null);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Contains(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}

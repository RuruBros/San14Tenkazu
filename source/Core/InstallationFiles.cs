using System.Buffers;
using System.Security.Cryptography;
using T = San14Tenkazu.L.InstallationAudit;

namespace San14Tenkazu;

internal sealed record FileProof(string Path, long? Size, string? Sha1, string? Sha256, string? Error);

// Enumerate each directory separately so an unreadable subtree never turns into a list of missing files.
internal sealed class InstallationFiles
{
    internal readonly Dictionary<string, FileProof> Files = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<AuditIssue> Issues = [];
    internal bool IsComplete => Issues.Count == 0 && Files.Values.All(f => f.Error is null);
    internal bool HiddenByIssue(string path) => Issues.Any(i => i.Source.Length == 0
        || path.Equals(i.Source, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(i.Source + "/", StringComparison.OrdinalIgnoreCase));

    // null candidateSizes means a package tree. Every package file gets SHA-256.
    // Installed files get Steam's SHA-1 and, if their size occurs in a package, SHA-256 in the same pass.
    internal static InstallationFiles Read(string root, HashSet<long>? candidateSizes, CancellationToken token,
        Action<string>? progress)
    {
        var result = new InstallationFiles();
        var pending = new Stack<string>();
        pending.Push(root);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
                if (relative == ".") relative = "";
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        result.Issues.Add(new(relative, T.LinkSkipped));
                        continue;
                    }
                    foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions
                             { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false }))
                    {
                        token.ThrowIfCancellationRequested();
                        string path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                        try
                        {
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) result.Issues.Add(new(path, T.LinkSkipped));
                            else if (entry is DirectoryInfo) pending.Push(entry.FullName);
                            else
                            {
                                progress?.Invoke(entry.FullName);
                                result.Files.Add(path, Fingerprint(entry.FullName, path, candidateSizes, buffer, token));
                            }
                        }
                        catch (Exception ex) when (InstallationAudit.IsReadFailure(ex))
                        {
                            result.Issues.Add(new(path, ex.Message));
                        }
                    }
                }
                catch (Exception ex) when (InstallationAudit.IsReadFailure(ex)) { result.Issues.Add(new(relative, ex.Message)); }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return result;
    }

    private static FileProof Fingerprint(string fullPath, string path, HashSet<long>? sizes, byte[] buffer, CancellationToken token)
    {
        long? length = null;
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            length = stream.Length;
            using var sha1 = sizes != null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
            using var sha256 = sizes == null || sizes.Contains(length.Value) ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                sha1?.AppendData(buffer, 0, read);
                sha256?.AppendData(buffer, 0, read);
            }
            if (length != stream.Position) throw new IOException(T.FileChanged);
            return new(path, length, sha1 is null ? null : Convert.ToHexString(sha1.GetHashAndReset()),
                sha256 is null ? null : Convert.ToHexString(sha256.GetHashAndReset()), null);
        }
        catch (Exception ex) when (InstallationAudit.IsReadFailure(ex)) { return new(path, length, null, null, ex.Message); }
    }
}

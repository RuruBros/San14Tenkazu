using System.Buffers.Binary;
using System.Text;
using T = San14Tenkazu.L.InstallationAudit;
using static San14Tenkazu.SteamManifestWire;

namespace San14Tenkazu;

internal readonly record struct DepotPin(uint Depot, ulong Manifest)
{
    public override string ToString() => $"{Depot}_{Manifest}";
}

internal sealed record DepotEntry(DepotPin Pin, string Path, ulong Size, string? Sha1, bool Link);
internal sealed record DepotSource(DepotPin Pin, string Path, int? Files, string? Error);
internal sealed record SharedDepotSource(string Depot, string? Owner, string? Directory, string Source);
internal sealed record AuditIssue(string Source, string Detail);

internal sealed class InstallationDepotIndex
{
    internal readonly Dictionary<string, List<DepotEntry>> Files = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<DepotSource> Sources = [];
    internal readonly List<AuditIssue> Issues = [];
    internal readonly string AppManifest;
    internal string? Build { get; private set; }
    internal readonly List<SharedDepotSource> SharedDepots = [];
    private readonly Dictionary<string, string?[]> revisions = new(StringComparer.OrdinalIgnoreCase);
    internal bool Complete => Sources.Count != 0 && Issues.Count == 0 && Sources.All(s => s.Error is null);

    private InstallationDepotIndex(string acf) => AppManifest = acf;

    internal static InstallationDepotIndex Load(string directory, IEnumerable<string>? roots, CancellationToken token)
    {
        var game = new DirectoryInfo(directory);
        var apps = game.Parent?.Parent;
        string acf = Path.Combine(apps?.FullName ?? directory, $"appmanifest_{SteamInstallation.AppId}.acf");
        var index = new InstallationDepotIndex(acf);
        if (apps is null || !apps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)
            || !game.Parent!.Name.Equals("common", StringComparison.OrdinalIgnoreCase))
        {
            index.Issues.Add(new(acf, T.WrongInstallation));
            return index;
        }
        var app = SteamInstallation.ReadMetadata(acf, ex => index.Issues.Add(new(acf, ex.Message)))?.Child("AppState");
        if (app?.Child("appid")?.Value != SteamInstallation.AppId ||
            !game.Name.Equals(app.Child("installdir")?.Value, StringComparison.OrdinalIgnoreCase))
        {
            if (index.Issues.Count == 0) index.Issues.Add(new(acf, T.WrongInstallation));
            return index;
        }
        index.Build = app.Child("buildid")?.Value;
        index.revisions.Add(acf, Revision(app));
        if (app.Child("StateFlags")?.Value is { } state && state != "4")
            index.Issues.Add(new(acf, $"{T.SteamState} (StateFlags {state})"));
        var depots = app.Child("InstalledDepots");
        if (depots is null || depots.Children.Count == 0) index.Issues.Add(new(acf, T.NoDepots));
        string[] libraries = SteamInstallation.FindLibraries(SteamInstallation.FindRoots(directory, roots)).ToArray();
        var caches = libraries.SelectMany(r =>
            new[] { Path.Combine(r, "depotcache"), Path.Combine(r, "steamapps", "depotcache") }).ToArray();
        var seen = new HashSet<uint>();
        foreach (var depot in depots?.Children ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (!uint.TryParse(depot.Key, out uint id) || id == 0 || !seen.Add(id)
                || !ulong.TryParse(depot.Child("manifest")?.Value, out ulong gid) || gid == 0)
            {
                index.Issues.Add(new(acf, $"{T.InvalidPin} ({depot.Key})"));
                continue;
            }
            index.ReadDepot(new(id, gid), caches, token);
        }
        foreach (var shared in app.Child("SharedDepots")?.Children ?? [])
        {
            token.ThrowIfCancellationRequested();
            index.ReadShared(shared, directory, apps.FullName, libraries, caches, token);
        }
        return index;
    }

    private void ReadShared(SteamInstallation.Node shared, string game, string apps, string[] libraries, string[] caches, CancellationToken token)
    {
        if (!uint.TryParse(shared.Key, out uint depot) || !uint.TryParse(shared.Value, out uint owner) || depot == 0 || owner == 0)
        {
            Issues.Add(new(AppManifest, $"{T.InvalidPin} ({shared.Key} → {shared.Value})"));
            return;
        }
        string name = $"appmanifest_{owner}.acf";
        var candidates = new[] { apps }.Concat(libraries.Select(l => Path.Combine(l, "steamapps"))).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in candidates)
        {
            string path = Path.Combine(folder, name);
            var app = SteamInstallation.ReadMetadata(path, ex =>
            {
                if (ex is not (FileNotFoundException or DirectoryNotFoundException)) Issues.Add(new(path, ex.Message));
            })?.Child("AppState");
            if (app?.Child("appid")?.Value != shared.Value || app.Child("installdir")?.Value is not { } install
                || install is "" or "." or ".." || install.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            revisions.TryAdd(path, Revision(app));
            string directory = Path.GetFullPath(Path.Combine(folder, "common", install));
            SharedDepots.Add(new(shared.Key, shared.Value, directory, path));
            if (directory.Equals(game, StringComparison.OrdinalIgnoreCase))
            {
                if (!ulong.TryParse(app.Child("InstalledDepots")?.Child(shared.Key)?.Child("manifest")?.Value, out ulong gid) || gid == 0)
                    Issues.Add(new(path, $"{T.InvalidPin} ({depot})"));
                else if (!Sources.Any(s => s.Pin == new DepotPin(depot, gid))) ReadDepot(new(depot, gid), caches, token);
            }
            return;
        }
        SharedDepots.Add(new(shared.Key, shared.Value, null, name));
        Issues.Add(new(name, T.SharedUnresolved));
    }

    private static string?[] Revision(SteamInstallation.Node app) =>
        [app.Child("appid")?.Value, app.Child("installdir")?.Value, app.Child("buildid")?.Value,
         app.Child("StateFlags")?.Value, .. (app.Child("InstalledDepots")?.Children ?? [])
             .SelectMany(n => new[] { n.Key, n.Child("manifest")?.Value }), "SharedDepots",
         .. (app.Child("SharedDepots")?.Children ?? []).SelectMany(n => new[] { n.Key, n.Value })];

    internal void CheckRevision()
    {
        foreach (var (path, revision) in revisions)
        {
            var current = SteamInstallation.ReadMetadata(path)?.Child("AppState");
            if (current is null || !revision.SequenceEqual(Revision(current))) throw new IOException(T.InstallationChanged);
        }
    }

    private void ReadDepot(DepotPin pin, string[] caches, CancellationToken token)
    {
        var failures = new List<string>();
        string name = $"{pin}.manifest";
        foreach (string cache in caches)
        {
            string path = Path.Combine(cache, name);
            try
            {
                var entries = ReadManifest(path, pin, token);
                Sources.Add(new(pin, path, entries.Count, null));
                foreach (var file in entries)
                {
                    if (!Files.TryGetValue(file.Path, out var owners)) Files.Add(file.Path, owners = []);
                    owners.Add(file);
                }
                return;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception ex) when (InstallationAudit.IsReadFailure(ex)) { failures.Add($"{path} · {ex.Message}"); }
        }
        Sources.Add(new(pin, name, null, failures.Count > 0 ? string.Join("\n", failures)
            : $"{T.MissingCache} · {string.Join(", ", caches)}"));
    }

    private static List<DepotEntry> ReadManifest(string path, DepotPin pin, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is < 16 or > 64 * 1024 * 1024) throw Invalid();
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        ReadOnlySpan<byte> rest = bytes;
        var files = new Reader(Section(ref rest, 0x71f617d0));
        var meta = new Reader(Section(ref rest, 0x1f4812be));
        Section(ref rest, 0x1b81b817);
        if (rest.Length != 4 || BinaryPrimitives.ReadUInt32LittleEndian(rest) != 0x32c415ab) throw Invalid();
        ulong depot = 0, manifest = 0;
        while (meta.Next(out int key, out ulong number, out _))
            switch (key)
            {
                case 8: depot = number; break;
                case 16: manifest = number; break;
                case 32 when number != 0: throw new InvalidDataException(T.Encrypted);
            }
        if (depot != pin.Depot || manifest != pin.Manifest) throw new InvalidDataException(T.PinMismatch);

        var entries = new List<DepotEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (files.Next(out int key, out _, out var bytesOfRow))
        {
            if (key != 10) continue;
            token.ThrowIfCancellationRequested();
            var row = new Reader(bytesOfRow);
            string? name = null, sha1 = null;
            ulong size = 0, flags = 0;
            while (row.Next(out int field, out ulong value, out var data))
                switch (field)
                {
                    case 10: name = StrictUtf8.GetString(data).Replace('\\', '/'); break;
                    case 16: size = value; break;
                    case 24: flags = value; break;
                    case 42 when data.Length == 20: sha1 = Convert.ToHexString(data); break;
                }
            if (name == null || name.Split('/').Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                || !seen.Add(name)) throw Invalid();
            if ((flags & 64) == 0 || (flags & 512) != 0)
                entries.Add(new(pin, name, size, sha1, (flags & 512) != 0));
        }
        return entries;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
}

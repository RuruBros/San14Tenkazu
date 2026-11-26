using System.Security.Cryptography;
using System.Text;

namespace San14Tenkazu.Tests;

public sealed class InstallationAuditTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("tenkazu-install-").FullName;
    private string Game => Path.Combine(root, "steamapps", "common", "Fixture");
    private string Acf => Path.Combine(root, "steamapps", $"appmanifest_{SteamInstallation.AppId}.acf");
    private string Cache => Path.Combine(root, "depotcache");
    private const string DefaultPin = "\"12\" { \"manifest\" \"300\" }";

    public InstallationAuditTests()
    {
        Directory.CreateDirectory(Game);
        Directory.CreateDirectory(Cache);
        File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_99.acf"),
            "\"AppState\" { \"appid\" \"99\" \"installdir\" \"Other\" }");
        WriteApp(DefaultPin);
    }

    public void Dispose() => Directory.Delete(root, true);

    private void WriteApp(string pins, string name = "Fixture") => File.WriteAllText(Acf,
        $"\"AppState\" {{ \"appid\" \"{SteamInstallation.AppId}\" \"installdir\" \"{name}\" \"buildid\" \"11\" " +
        $"\"InstalledDepots\" {{ {pins} }} \"SharedDepots\" {{ \"9\" \"99\" }} }}");

    private static byte[] Var(ulong n)
    {
        var bytes = new List<byte>();
        while (n >= 128) { bytes.Add((byte)((n & 127) | 128)); n >>= 7; }
        bytes.Add((byte)n);
        return bytes.ToArray();
    }

    private static byte[] Field(byte key, byte[] value) => [key, .. Var((ulong)value.Length), .. value];
    private static byte[] Section(uint magic, byte[] value) => [.. BitConverter.GetBytes(magic), .. BitConverter.GetBytes(value.Length), .. value];
    private static byte[] Row(string name, string body, bool hash = true, byte flags = 0)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        return Field(10, [.. Field(10, Encoding.UTF8.GetBytes(name)), 16, .. Var((ulong)bytes.Length), 24, .. Var(flags),
            .. (hash ? Field(42, SHA1.HashData(bytes)) : Array.Empty<byte>())]);
    }

    private string Manifest(uint depot, ulong id, params byte[][] rows)
    {
        string path = Path.Combine(Cache, $"{depot}_{id}.manifest");
        WriteManifest(path, depot, id, false, rows);
        return path;
    }

    private static void WriteManifest(string path, uint depot, ulong id, bool encrypted, byte[][] rows)
    {
        byte[] metadata = [8, .. Var(depot), 16, .. Var(id), 32, (byte)(encrypted ? 1 : 0)];
        File.WriteAllBytes(path, [.. Section(0x71f617d0, rows.SelectMany(r => r).ToArray()), .. Section(0x1f4812be, metadata),
            .. Section(0x1b81b817, []), .. BitConverter.GetBytes(0x32c415abu)]);
    }

    private static string Put(string folder, string path, string content)
    {
        string full = Path.Combine(folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private InstallationAudit Scan(params string[] packages) => InstallationAudit.Scan(Game, packages, steamRoots: [root]);

    [Fact]
    public void AuditCanDiscoverBoundDirectoryEvenWhenResourceMarkerIsMissing()
    {
        Assert.Null(SteamInstallation.FindGameDirectory(null, [root]));
        Assert.Equal(Game, SteamInstallation.FindGameDirectory(null, [root], requireResources: false));
    }

    [Fact]
    public void ComparesInstalledPinsAllFileStatesAndEveryOverlappingOwner()
    {
        WriteApp(DefaultPin + " \"94\" { \"manifest\" \"72\" }");
        Manifest(12, 300, Row(@"data\a.bin", "OK"), Row("data", "", flags: 64), Row("b.bin", "aa"),
            Row("empty", ""), Row("missing", "M"), Row("shared", "OLD"));
        Manifest(94, 72, Row("SHARED", "NEW"));
        Manifest(12, 299, Row("foreign", "X")); // historical cache must have no influence
        Put(Game, "data/a.bin", "OK"); Put(Game, "b.bin", "bb"); Put(Game, "empty", "");
        Put(Game, "foreign", "X"); Put(Game, "shared", "NEW");
        var report = Scan();
        Assert.True(report.Depots.Complete);
        var map = report.Installed.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(6, map.Count);
        Assert.Equal(InstalledFileStatus.Matches, map["data/a.bin"].Status);
        Assert.Equal(InstalledFileStatus.Matches, map["empty"].Status);
        Assert.Equal(InstalledFileStatus.Changed, map["b.bin"].Status);
        Assert.Equal(InstalledFileStatus.Missing, map["missing"].Status);
        Assert.Equal(InstalledFileStatus.Added, map["foreign"].Status);
        Assert.Equal(InstalledFileStatus.Unknown, map["shared"].Status);
        Assert.Equal(L.InstallationAudit.ConflictingClaims, map["shared"].Reason);
        Assert.Equal(2, map["shared"].Owners.Count);
        Assert.Equal(2, map["shared"].Owners.Select(o => o.Sha1).Distinct().Count());
        Assert.Equal("99", Assert.Single(report.Depots.SharedDepots).Owner);
    }

    [Fact]
    public void MissingCacheKeepsUnlistedAndMismatchingFilesUnresolved()
    {
        WriteApp(DefaultPin + " \"71\" { \"manifest\" \"82\" }");
        Manifest(12, 300, Row("known", "K"), Row("different", "A"), Row("absent", ""));
        Put(Game, "known", "K"); Put(Game, "different", "D"); Put(Game, "other", "x");
        var report = Scan();
        Assert.False(report.Depots.Complete);
        Assert.Single(report.Depots.Sources, s => s.Pin.Depot == 71 && s.Error!.Contains(L.InstallationAudit.MissingCache));
        Assert.Equal(2, report.Installed.Count(f => f.Status == InstalledFileStatus.Unknown && f.Reason == L.InstallationAudit.IncompleteDepots));
        Assert.Single(report.Installed, f => f.Status == InstalledFileStatus.Missing);
        Assert.Single(report.Installed, f => f.Status == InstalledFileStatus.Matches);
    }

    [Theory]
    [InlineData("encrypted")]
    [InlineData("wrongPin")]
    [InlineData("truncated")]
    [InlineData("unsafePath")]
    [InlineData("duplicatePath")]
    public void InvalidManifestProvidesReasonAndNeverProducesPartialClaims(string kind)
    {
        string path = Manifest(12, 300, Row("valid", "hello"));
        switch (kind)
        {
            case "encrypted": WriteManifest(path, 12, 300, true, []); break;
            case "wrongPin": WriteManifest(path, 13, 300, false, []); break;
            case "truncated": File.WriteAllBytes(path, File.ReadAllBytes(path)[..^1]); break;
            case "unsafePath": Manifest(12, 300, Row("valid", "hello"), Row(@"..\other", "")); break;
            case "duplicatePath": Manifest(12, 300, Row("valid", "hello"), Row("VALID", "")); break;
        }
        Put(Game, "valid", "hello");
        var report = Scan();
        Assert.Empty(report.Depots.Files);
        Assert.NotEmpty(Assert.Single(report.Depots.Sources).Error!);
        Assert.Equal(InstalledFileStatus.Unknown, Assert.Single(report.Installed).Status);
    }

    [Fact]
    public void AccountsForHashesMissingFromManifestAndUnsupportedLinkEntries()
    {
        byte[] link = Field(10, [.. Field(10, "link"u8.ToArray()), 24, .. Var(512)]);
        Manifest(12, 300, Row("nohash", "C", false), link);
        Put(Game, "nohash", "C");
        var report = Scan();
        Assert.True(report.Depots.Complete);
        Assert.All(report.Installed, f => Assert.Equal(InstalledFileStatus.Unknown, f.Status));
        Assert.Contains(report.Installed, f => f.Reason == L.InstallationAudit.NoContentHash);
        Assert.Contains(report.Installed, f => f.Reason == L.InstallationAudit.LinkSkipped);
    }

    [Fact]
    public void PackagesUseContentEvenAfterRenamingAndDoNotAttributeRuntimeOrInstallOrder()
    {
        Manifest(12, 300, Row("boot.dll", "stock"));
        Put(Game, "boot.dll", "overwritten"); Put(Game, "another/renamed.dat", "payload");
        string first = Path.Combine(root, "A"), second = Path.Combine(root, "B");
        Put(first, "staged/boot.dll", "payload"); Put(second, "optional/boot.dll", "alternative");
        Put(second, "stock.dll", "stock");
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f));
        var report = Scan(first, second);
        Assert.Equal(3, report.Packages.Length);
        var copied = Assert.Single(report.Packages, p => p.Status == PackageContentStatus.Found);
        Assert.Equal(DllOptionEvidence.Unobserved, copied.Runtime);
        Assert.Equal("another/renamed.dat", Assert.Single(copied.ContentPaths));
        Assert.Equal("boot.dll", Assert.Single(copied.SameNamePaths));
        Assert.Equal("optional/boot.dll", Assert.Single(copied.OtherPackages).File.Path);
        Assert.Equal(2, report.Packages.Count(p => p.Status == PackageContentStatus.NoMatch));
        Assert.Equal(InstalledFileStatus.Changed, Assert.Single(report.Installed, f => f.Path == "boot.dll").Status);
        Assert.Equal(DllOptionEvidence.Unobserved, Assert.Single(report.Installed, f => f.Path == "boot.dll").Runtime);
        Assert.Equal(before, Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f)));
    }

    [Fact]
    public void SharedOwnerInSameInstallationContributesItsPinnedContent()
    {
        File.WriteAllText(Path.Combine(root, "steamapps", "appmanifest_99.acf"),
            "\"AppState\" { \"appid\" \"99\" \"installdir\" \"Fixture\" \"InstalledDepots\" { \"9\" { \"manifest\" \"77\" } } }");
        Manifest(12, 300, Row("base", "A"));
        Manifest(9, 77, Row("shared", "B"));
        Put(Game, "shared", "B");
        var report = Scan();
        Assert.True(report.Depots.Complete);
        Assert.Equal(new DepotPin(9, 77), Assert.Single(report.Installed, f => f.Status == InstalledFileStatus.Matches).Owners.Single().Pin);
        File.Delete(Path.Combine(root, "steamapps", "appmanifest_99.acf"));
        report = Scan();
        Assert.False(report.Depots.Complete);
        Assert.Equal(InstalledFileStatus.Unknown, Assert.Single(report.Installed, f => f.Path == "shared").Status);
    }

    [Fact]
    public void LockedFileMakesNegativePackageEvidenceUnknown()
    {
        string locked = Put(Game, "file", "X"), pkg = Path.Combine(root, "sample");
        Put(pkg, "other", "X");
        Manifest(12, 300, Row("file", "X"));
        using var held = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var report = Scan(pkg);
        Assert.Equal(InstalledFileStatus.Unknown, Assert.Single(report.Installed).Status);
        Assert.Equal(PackageContentStatus.Unknown, Assert.Single(report.Packages).Status);
        Assert.Contains(report.Issues, i => i.Source == locked);
    }

    [Fact]
    public void UnboundDirectoryAndNestedSamplesCannotMasqueradeAsAnInstallation()
    {
        Manifest(12, 300, Row("file", "A"));
        Put(Game, "file", "A");
        WriteApp(DefaultPin, "OtherGame");
        var report = Scan();
        Assert.False(report.Depots.Complete);
        Assert.Equal(L.InstallationAudit.WrongInstallation, Assert.Single(report.Depots.Issues).Detail);
        Assert.Equal(InstalledFileStatus.Unknown, Assert.Single(report.Installed).Status);
        Assert.Throws<ArgumentException>(() => Scan(Game));
        Assert.Throws<ArgumentException>(() => Scan(Path.Combine(Game, "staged")));
    }

    [Fact]
    public void RevisionChangeAndCancellationDoNotPublishACompletedScan()
    {
        Manifest(12, 300, Row("file", "A")); Put(Game, "file", "A");
        var ex = Assert.Throws<IOException>(() => InstallationAudit.Scan(Game, [], progress: _ => WriteApp(""), steamRoots: [root]));
        Assert.Equal(L.InstallationAudit.InstallationChanged, ex.Message);
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => InstallationAudit.Scan(Game, [], cts.Token, _ => cts.Cancel(), [root]));
    }
}

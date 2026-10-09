namespace San14Tenkazu.Tests;

public sealed class SteamInstallationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "tenkazu-steam-" + Guid.NewGuid().ToString("N"));
    private string Folder(string name) { string path = Path.Combine(directory, name); Directory.CreateDirectory(path); return path; }
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static void Metadata(string root, string name, string text)
    { Directory.CreateDirectory(Path.Combine(root, "steamapps")); File.WriteAllText(Path.Combine(root, "steamapps", name), text); }
    private static string Game(string library, string install = "Romance_of_the_Three_Kingdoms_14")
    {
        string game = Path.Combine(library, "steamapps", "common", install);
        Directory.CreateDirectory(Path.Combine(game, "0002"));
        File.WriteAllText(Path.Combine(game, "0002", "ScreenLayout.rdb"), "fixture");
        Metadata(library, "appmanifest_872410.acf", $"\"AppState\" {{ \"appid\" \"872410\" \"installdir\" {Quote(install)} }}");
        return game;
    }

    [Fact]
    public void OfflineGameIsFoundInSeparateLibraryWithSpacesAndKoreanPath()
    {
        string client = Folder("별도 Steam 클라이언트");
        string library = Folder("게임 저장소 한글");
        string game = Game(library, "삼국지 게임");
        Metadata(client, "libraryfolders.vdf", $"\"libraryfolders\" {{ \"0\" {{ \"path\" {Quote(client)} }} \"1\" {{ \"label\" \"}}\" \"path\" {Quote(library)} \"apps\" {{ \"872410\" \"100\" }} }} }}");
        Assert.Equal(game, SteamInstallation.FindGameDirectory(null, [client]));
    }

    [Fact]
    public void ActualRunningExecutableTakesPriorityOverAnotherInstalledCopy()
    {
        string client = Folder("client");
        _ = Game(client);
        string running = Folder("moved game");
        Directory.CreateDirectory(Path.Combine(running, "0002"));
        File.WriteAllText(Path.Combine(running, "0002", "ScreenLayout.rdb"), "fixture");
        Assert.Equal(running, SteamInstallation.FindGameDirectory(Path.Combine(running, "SAN14CE_KO.exe"), [client]));
    }

    [Fact]
    public void LegacyLibraryPairsAndLineCommentsAreSupported()
    {
        string client = Folder("client");
        string library = Folder("older library");
        string game = Game(library);
        Metadata(client, "libraryfolders.vdf", $"// Steam library list\n\"LibraryFolders\" {{ \"TimeNextStatsReport\" \"0\" \"1\" {Quote(library)} // mounted library\n }}");
        Assert.Equal(game, SteamInstallation.FindGameDirectory(null, [client]));
    }

    [Fact]
    public void RegisteredFilesAreReadFromClientUserdataWithAllAccountsPreserved()
    {
        string client = Folder("Steam installed elsewhere");
        string game = Game(Folder("separate game library"));
        string[] expected = new[] { "10", "20" }.Select(account => Path.Combine(client, "userdata", account, "872410", "remote", RegisteredOfficerFile.FileName)).ToArray();
        foreach (string file in expected) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "fixture"); }
        Assert.Equal(expected.Order(), RegisteredOfficerFile.FindFiles(game, [client, client.ToUpperInvariant()]));
    }

    [Fact]
    public void GameAncestryStillFindsUserdataWithoutRegistryEntries()
    {
        string client = Folder("client");
        string game = Game(client);
        string file = Path.Combine(client, "userdata", "10", "872410", "remote", RegisteredOfficerFile.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "fixture");
        Assert.Equal([file], RegisteredOfficerFile.FindFiles(game, []));
    }

    [Fact]
    public void InvalidMetadataDoesNotPreventDiscoveryFromAnotherClient()
    {
        string broken = Folder("broken");
        Metadata(broken, "libraryfolders.vdf", "\"libraryfolders\" { \"0\" { \"path\" \"unclosed");
        string valid = Folder("valid");
        string game = Game(valid);
        Assert.Equal(game, SteamInstallation.FindGameDirectory(null, [broken, valid]));
    }

    [Fact]
    public void ManifestCannotSelectAnotherAppOrEscapeTheCommonDirectory()
    {
        string client = Folder("client");
        string game = Game(client);
        Metadata(client, "appmanifest_872410.acf", "\"AppState\" { \"appid\" \"999\" \"installdir\" \"Romance_of_the_Three_Kingdoms_14\" }");
        Assert.Null(SteamInstallation.FindGameDirectory(null, [client]));
        Metadata(client, "appmanifest_872410.acf", "\"AppState\" { \"appid\" \"872410\" \"installdir\" \"..\\\\..\\\\..\" }");
        Assert.Null(SteamInstallation.FindGameDirectory(null, [client]));
        Assert.True(File.Exists(Path.Combine(game, "0002", "ScreenLayout.rdb")));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

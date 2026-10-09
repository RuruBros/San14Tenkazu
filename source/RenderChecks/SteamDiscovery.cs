using System.IO;
using System.Text.Json;
using San14Tenkazu;

internal static partial class RenderChecks
{
    private static void CheckSteamDiscovery(string output)
    {
        string? executable = GameMemory.RunningGamePath();
        string? runningDirectory = FaceLibrary.FindGameDirectory(executable);
        string? offlineDirectory = SteamInstallation.FindGameDirectory(null);
        var result = new
        {
            gameAccess = false,
            filesWritten = new[] { Path.GetFullPath(output) },
            executable,
            runningDirectory,
            offlineDirectory,
            steamRoots = SteamInstallation.FindRoots(runningDirectory),
            registeredFiles = RegisteredOfficerFile.FindFiles(offlineDirectory)
        };
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        if (offlineDirectory is null) throw new InvalidOperationException("Installed game metadata was not resolved; see discovery log");
        Console.WriteLine("Steam registry, library metadata and installed game directory resolved; no game memory or userdata writes");
    }
}

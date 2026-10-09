using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using San14Tenkazu;
using San14Tenkazu.Seasons;

internal static partial class RenderChecks
{
    private static void CheckPortablePackage(string output)
    {
        string root = Path.GetFullPath(AppContext.BaseDirectory);
        var paths = new Dictionary<string, string>
        {
            ["runtime"] = RuntimeEnvironment.GetRuntimeDirectory(),
            ["coreLibrary"] = typeof(object).Assembly.Location,
            ["wpf"] = typeof(System.Windows.Window).Assembly.Location,
            ["drawing"] = typeof(System.Drawing.Bitmap).Assembly.Location,
            ["direct3d"] = typeof(Vortice.Direct3D11.ID3D11Device).Assembly.Location,
            ["editor"] = typeof(EditorWindow).Assembly.Location,
            ["core"] = typeof(GameMemory).Assembly.Location
        };
        if (!Environment.Is64BitProcess || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException("Portable process must be Windows x64");
        foreach (var path in paths)
            if (!Path.GetFullPath(path.Value).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Dependency was loaded outside the extracted package: {path.Key} = {path.Value}");
        if (string.IsNullOrWhiteSpace(L.Window.Needs64Bit))
            throw new InvalidDataException("Embedded Korean strings are missing");
        using (var icon = typeof(EditorWindow).Assembly.GetManifestResourceStream("app.ico"))
            if (icon is null || icon.Length == 0) throw new InvalidDataException("Embedded app icon is missing");

        // Authenticate and extract the embedded Native bundle into this check's scratch folder.
        string bundle = SeasonNativeBundle.Extract(Path.Combine(Path.GetDirectoryName(output)!, "smoke-native-bundle"));
        SeasonNativeBundle.VerifyDirectory(bundle);
        CheckGpuLifecycle();
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            verified = true, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtimeVersion = Environment.Version.ToString(), packageRoot = root, dependencies = paths,
            embeddedStrings = true, embeddedIcon = true, nativeBundle = true, gpuLifecycle = true,
            gameAccess = false, userProfileWritten = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Extracted portable runtime, WPF, Direct3D, embedded assets and Native bundle verified without game access");
    }
}

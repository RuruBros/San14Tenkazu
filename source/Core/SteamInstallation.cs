using System.Security;
using System.Text;
using Microsoft.Win32;

namespace San14Tenkazu;

// Steam's client holds userdata; its libraries can live on separate volumes.
internal static class SteamInstallation
{
    internal const string AppId = "872410";

    internal static string[] FindRoots(string? gameDirectory = null, IEnumerable<string>? configuredRoots = null)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in configuredRoots ?? ConfiguredRoots()) Add(root);
        if (gameDirectory is not null)
            for (DirectoryInfo? current = new(gameDirectory); current is not null; current = current.Parent)
                if (Directory.Exists(Path.Combine(current.FullName, "steamapps")) || Directory.Exists(Path.Combine(current.FullName, "userdata")))
                    Add(current.FullName);
        return roots.ToArray();

        void Add(string path)
        {
            try
            {
                if (Path.IsPathFullyQualified(path) && Directory.Exists(path)) roots.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
    }

    private static IEnumerable<string> ConfiguredRoots()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            Read(RegistryHive.CurrentUser, RegistryView.Default, "SteamPath");
            Read(RegistryHive.LocalMachine, RegistryView.Registry32, "InstallPath");
            Read(RegistryHive.LocalMachine, RegistryView.Registry64, "InstallPath");
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return roots;

        void Read(RegistryHive hive, RegistryView view, string name)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                using RegistryKey registry = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? key = registry.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue(name) is string path) roots.Add(path);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
        }
    }

    internal static string? FindGameDirectory(string? executablePath, IEnumerable<string>? configuredRoots = null)
    {
        string? directory = executablePath is null ? null : Path.GetDirectoryName(executablePath);
        if (HasResources(directory)) return directory;
        foreach (string library in FindLibraries(FindRoots(directory, configuredRoots)))
        {
            Node? app = ReadMetadata(Path.Combine(library, "steamapps", $"appmanifest_{AppId}.acf"))?.Child("AppState");
            if (app?.Child("appid")?.Value != AppId || app.Child("installdir")?.Value is not { } install) continue;
            try
            {
                string common = Path.GetFullPath(Path.Combine(library, "steamapps", "common")) + Path.DirectorySeparatorChar;
                string candidate = Path.GetFullPath(Path.Combine(common, install));
                if (candidate.StartsWith(common, StringComparison.OrdinalIgnoreCase) && HasResources(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return null;
    }

    private static bool HasResources(string? directory) => directory is not null && File.Exists(Path.Combine(directory, "0002", "ScreenLayout.rdb"));

    private static IEnumerable<string> FindLibraries(string[] roots)
    {
        var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            Node? folders = ReadMetadata(Path.Combine(root, "steamapps", "libraryfolders.vdf"))?.Child("libraryfolders");
            foreach (Node folder in folders?.Children ?? [])
            {
                if (!int.TryParse(folder.Key, out _) || (folder.Value ?? folder.Child("path")?.Value) is not { } path) continue;
                try
                {
                    if (Path.IsPathFullyQualified(path) && Directory.Exists(path)) libraries.Add(Path.GetFullPath(path));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
            }
        }
        return libraries;
    }

    private sealed record Node(string Key, string? Value, List<Node> Children)
    {
        internal Node? Child(string key) => Children.FirstOrDefault(n => n.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    // Only text KeyValues metadata is needed: pairs, nested blocks, quoted escapes and line comments.
    private static Node? ReadMetadata(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            int position = 0;
            List<Node> children = Block(false, 0);
            return new Node("", null, children);

            List<Node> Block(bool nested, int depth)
            {
                if (depth > 32) throw new InvalidDataException("Steam metadata nesting is too deep");
                var nodes = new List<Node>();
                while (Token() is { } key)
                {
                    if (!key.Literal && key.Text == "}") { if (nested) return nodes; throw new InvalidDataException("Unexpected closing block"); }
                    if (!key.Literal) throw new InvalidDataException("Missing metadata key");
                    var value = Token() ?? throw new InvalidDataException("Missing metadata value");
                    if (!value.Literal && value.Text == "}") throw new InvalidDataException("Missing metadata value");
                    nodes.Add(!value.Literal ? new Node(key.Text, null, Block(true, depth + 1)) : new Node(key.Text, value.Text, []));
                }
                if (nested) throw new InvalidDataException("Unclosed metadata block");
                return nodes;
            }

            (string Text, bool Literal)? Token()
            {
                while (position < text.Length)
                {
                    if (char.IsWhiteSpace(text[position])) { position++; continue; }
                    if (text[position] == '/' && position + 1 < text.Length && text[position + 1] == '/')
                    { while (position < text.Length && text[position] != '\n') position++; continue; }
                    break;
                }
                if (position == text.Length) return null;
                char first = text[position++];
                if (first is '{' or '}') return (first.ToString(), false);
                if (first != '"')
                {
                    int start = position - 1;
                    while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}')) position++;
                    return (text[start..position], true);
                }
                var value = new StringBuilder();
                while (position < text.Length)
                {
                    char c = text[position++];
                    if (c == '"') return (value.ToString(), true);
                    if (c == '\\' && position < text.Length)
                    {
                        char escaped = text[position];
                        if (escaped is '\\' or '"' or 'n' or 'r' or 't')
                        { position++; c = escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => escaped }; }
                    }
                    value.Append(c);
                }
                throw new InvalidDataException("Unclosed metadata string");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException) { return null; }
    }
}

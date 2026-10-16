namespace San14Tenkazu;

// The log panel only lives as long as the window. Keep the same lines on disk so a failed
// automatic connection or rule application can still be diagnosed after a restart.
internal sealed class EditorLogFile(string directory)
{
    internal string PathName => Path.Combine(directory, $"editor-{DateTime.Now:yyyyMMdd}.log");
    internal void Append(string line)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(PathName, line);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // never block the editor on its own log
    }
}

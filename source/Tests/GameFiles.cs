namespace San14Tenkazu.Tests;

// The installed game, for the tests that read its files (SAN14_EXE names another exe).
internal static class GameFiles
{
    internal static readonly string Exe = Environment.GetEnvironmentVariable("SAN14_EXE")
        ?? @"C:\Program Files (x86)\Steam\steamapps\common\Romance_of_the_Three_Kingdoms_14\SAN14CE_KO.exe";
}

// A test that reads the game's exe: skipped where the game is not installed.
public sealed class GameExeFactAttribute : FactAttribute
{
    public GameExeFactAttribute()
    {
        if (!File.Exists(GameFiles.Exe)) Skip = $"게임 실행 파일이 없습니다: {GameFiles.Exe}";
    }
}

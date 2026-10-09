using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace San14Tenkazu.Tests;

// The Korean set (Core/Strings/ko.txt) and the code that reads it (L): every text has its member and every member its
// text, every place its parameter, and no text the player reads is left in the code.
public class StringsTests
{
    private static readonly Regex Place = new(@"\{([a-z][A-Za-z0-9]*)(?::[^}]*)?\}");

    [Fact]
    public void EveryTextHasItsMemberAndEveryMemberItsText()
    {
        var texts = L.Texts.Keys.Where(key => !L.Tables.Contains(key.Split('.')[0]) && !L.NamedTables.Contains(key.Split('.')[0])).ToHashSet();
        var members = L.Members().Select(m => m.Key).ToHashSet();

        Assert.Empty(members.Except(texts));
        Assert.Empty(texts.Except(members));
    }

    [Fact]
    public void EveryPlaceIsAParameterAndEveryParameterAPlace()
    {
        foreach ((string key, MemberInfo member) in L.Members())
        {
            var places = Place.Matches(L.Texts[key]).Select(m => m.Groups[1].Value).ToHashSet();
            var parameters = member is MethodInfo method ? method.GetParameters().Select(p => p.Name!).ToHashSet() : [];
            Assert.True(places.SetEquals(parameters), $"{key}: {{{string.Join(", ", places)}}} vs ({string.Join(", ", parameters)})");
        }
    }

    [Fact]
    public void ATableIsTheGamesListByNumber()
    {
        foreach (string table in L.Tables)
        {
            string[] keys = L.Texts.Keys.Where(key => key.StartsWith(table + ".", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(keys);
            Assert.All(keys, key => Assert.True(int.TryParse(key[(table.Length + 1)..], out _), key));
        }
        Assert.Equal("어린", L.Table("Formations", 1));
        Assert.Equal("", L.Table("Formations", 99));
    }

    // A named table is keyed by the game's own identifiers (a class name, then a number after a dot).
    [Fact]
    public void ANamedTableIsKeyedByTheGamesNames()
    {
        foreach (string table in L.NamedTables)
        {
            string[] keys = L.Texts.Keys.Where(key => key.StartsWith(table + ".", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(keys);
            Assert.All(keys, key => Assert.Matches(@"^[A-Za-z_][A-Za-z0-9_]*(\.\d+)?$", key[(table.Length + 1)..]));
        }
        Assert.Equal("", L.Named(L.NamedTables[0], "NoSuchClass"));
    }

    [Fact]
    public void PlacesAreFilledWithTheirValuesFormatsAndParticles()
    {
        Assert.Equal("손권과의 친선: 0부터 1,000 사이", L.Format("{force:과}의 친선: 0부터 {max:N0} 사이", ("force", "손권"), ("max", 1000)));
        Assert.Equal("유비와 조조는", L.Format("{a:와} {b:은}", ("a", "유비"), ("b", "조조")));
        Assert.Equal("{missing} 1", L.Format("{missing} {n}", ("n", 1)));
        Assert.Equal("규모별 상한 50,000/100,000", CityFields.Gold.Hint[..21]);
    }

    [Fact]
    public void TheSetIsReadAsWritten()
    {
        Dictionary<string, string> set = L.Parse("# comment\n[A]\nOne = 첫 줄\\n둘째 줄\n Two =  \\\\ 그리고 \"따옴표\" \n[B]\nOne = 다른 구역\n");

        Assert.Equal("첫 줄\n둘째 줄", set["A.One"]);
        Assert.Equal("\\ 그리고 \"따옴표\"", set["A.Two"]);
        Assert.Equal("다른 구역", set["B.One"]);
        Assert.Throws<FormatException>(() => L.Parse("[A]\nOne = 1\nOne = 2\n"));
        Assert.Throws<FormatException>(() => L.Parse("[A]\nno equals sign\n"));
    }

    // Korean in a string literal of the code is a text the player reads, and belongs in the set; but for the particles
    // (Korean, L) and these, which match the game's own data (names the game gives an unused record, its "no office")
    // or copy the game's own tables (CityNames, the facility types).
    private static readonly HashSet<string> NotTexts = ["CityNames.cs", "Korean.cs", "L.cs"];
    private static readonly HashSet<(string File, string Text)> GameMatches =
    [
        ("GameMemory.cs", "아이템"), ("GameMemory.cs", "국호"), ("GameMemory.cs", "정책"), ("GameMemory.cs", @"^(개성|전법|전)\d+$"),
        ("EditorWindow.Editors.cs", "없음"),
        ("OfficeRules.cs", "무효"), // Native unused-record marker, verified by OfficeRuleTests and the live audit.
        ("MapArt.cs", "진"), ("MapArt.cs", "요새"), ("MapArt.cs", "망루"), ("MapArt.cs", "투석대"), ("MapArt.cs", "군악대"), ("MapArt.cs", "석병팔진"),
        ("MapArt.cs", "토성"), ("MapArt.cs", "석벽"), ("MapArt.cs", "불함정"), ("MapArt.cs", "맹화함정"), ("MapArt.cs", "화선"), ("MapArt.cs", "구덩이"),
        ("MapArt.cs", "망대"), ("MapArt.cs", "철쇄"), ("MapArt.cs", "낙석"),
        .. PersonalityEdits.ReachPatterns.Select(pattern => ("PersonalityEdits.cs", pattern)),
    ];

    [Fact]
    public void NoTextThePlayerReadsIsLeftInTheCode()
    {
        string source = SourceFolder();
        var left = new List<string>();
        foreach (string path in SourceFiles(source))
        {
            string relative = Path.GetRelativePath(source, path);
            string name = Path.GetFileName(path);
            // The SDK excludes hidden directories from Compile, including reverse-engineering caches of old source.
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.'))) continue;
            // These are independent test projects or build output, excluded from the application's Compile items.
            if (relative.Split(Path.DirectorySeparatorChar) is [var first, ..] && first is "Tests" or "RenderChecks" or "bin" or "obj") continue;
            if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || NotTexts.Contains(name)) continue;
            foreach (string literal in Literals(File.ReadAllText(path)))
                if (literal.Any(c => c is >= '가' and <= '힣') && !GameMatches.Contains((name, literal))) left.Add($"{relative}: \"{literal}\"");
        }
        Assert.Empty(left);
    }

    // Exclude build/test/cache directories before descending: enumerating all descendants first can enter
    // an inaccessible reverse-engineering cache even though none of its files are application source.
    private static IEnumerable<string> SourceFiles(string source)
    {
        foreach (string path in Directory.EnumerateFiles(source, "*.cs")) yield return path;
        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            string name = Path.GetFileName(directory);
            if (name.StartsWith('.') || name is "Tests" or "RenderChecks" or "bin" or "obj") continue;
            foreach (string path in SourceFiles(directory)) yield return path;
        }
    }

    // The source folder (the one with San14Tenkazu.csproj), above the tests' output.
    private static string SourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "San14Tenkazu.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException("San14Tenkazu.csproj");
    }

    // The text of each string literal of C# code (an interpolated one without its places, whose literals come too),
    // comments and characters skipped.
    private static IEnumerable<string> Literals(string code)
    {
        var found = new List<string>();
        int i = 0;
        Scan(code, ref i, found, end: '\0');
        return found;
    }

    private static void Scan(string code, ref int i, List<string> found, char end)
    {
        int depth = 0;
        while (i < code.Length)
        {
            char c = code[i];
            if (end != '\0')
            {
                if (c is '{' or '(' or '[') depth++;
                else if (c is ')' or ']') depth--;
                else if (c == '}' && depth-- == 0) return;
            }
            if (c == '/' && i + 1 < code.Length && code[i + 1] == '/') { i = code.IndexOf('\n', i) is var n and >= 0 ? n : code.Length; continue; }
            if (c == '/' && i + 1 < code.Length && code[i + 1] == '*') { i = code.IndexOf("*/", i + 2, StringComparison.Ordinal) + 2; continue; }
            if (c == '\'')
            {
                i += code[i + 1] == '\\' ? 4 : 3;
                while (i < code.Length && code[i - 1] != '\'') i++;
                continue;
            }
            if (string.CompareOrdinal(code, i, "\"\"\"", 0, 3) == 0)
            {
                // A raw literal (the map's shaders): its text as it stands.
                int close = code.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                found.Add(code[(i + 3)..close]);
                i = close + 3;
                continue;
            }
            int start = i;
            bool interpolated = false, verbatim = false;
            while (i < code.Length && code[i] is '$' or '@')
            {
                interpolated |= code[i] == '$';
                verbatim |= code[i] == '@';
                i++;
            }
            if (i < code.Length && code[i] == '"' && (i == start || start == 0 || !char.IsLetterOrDigit(code[start - 1])))
            {
                found.Add(ReadLiteral(code, ref i, interpolated, verbatim, found));
                continue;
            }
            i = start + 1;
        }
    }

    private static string ReadLiteral(string code, ref int i, bool interpolated, bool verbatim, List<string> found)
    {
        var text = new StringBuilder();
        i++;
        while (i < code.Length)
        {
            char c = code[i];
            if (interpolated && c == '{')
            {
                if (code[i + 1] == '{') { text.Append('{'); i += 2; continue; }
                i++;
                Scan(code, ref i, found, end: '}');
                i++;
                continue;
            }
            if (interpolated && c == '}' && code[i + 1] == '}') { text.Append('}'); i += 2; continue; }
            if (verbatim && c == '"' && i + 1 < code.Length && code[i + 1] == '"') { text.Append('"'); i += 2; continue; }
            if (!verbatim && c == '\\') { text.Append(code, i, 2); i += 2; continue; }
            if (c == '"') { i++; break; }
            text.Append(c);
            i++;
        }
        return text.ToString();
    }
}

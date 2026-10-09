using System.Globalization;
using System.Reflection;
using System.Text;

namespace San14Tenkazu;

// The editor's words: every text the player reads is in Strings/ko.txt, the Korean set, under a [section] as
// "key = text", and the code asks for it by name through the classes of this one (L.Officer.SpouseTip is SpouseTip in
// [Officer]), so a mistyped name does not build. A text with values in it has a method whose parameters are named as
// its places: {name} for a value, {name:N0} for a number with separators, {name:은} for a name with the particle that
// fits its last sound (은·이·을·과 pick 은/는, 이/가, 을/를, 과/와; Korean). Another language is another set with the
// same sections and keys; StringsTests checks that each key has its class member and each place its parameter.
internal static partial class L
{
    internal const string SetName = "Strings.ko.txt";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> texts = new(() => Parse(ReadSet(SetName)));

    internal static IReadOnlyDictionary<string, string> Texts => texts.Value;

    // The text of "Section.Key"; the key itself when the set has no such text (StringsTests keeps that from happening).
    internal static string Get(string key) => Texts.TryGetValue(key, out string? text) ? text : key;

    // Sections that are the game's lists by number ("1 = 어린" in [Formations]), read by number rather than by a member.
    internal static readonly string[] Tables = ["Formations", "TacticKinds", PersonalityEdits.ConditionTable, PersonalityEdits.FilterTable, AncientOfficers.ScenarioTable, AncientOfficers.ChronicleTable,
        AiLabels.PredicateTable];

    internal static string Table(string section, int number) => Texts.TryGetValue($"{section}.{number}", out string? text) ? text : "";

    // Sections keyed by the game's own names rather than by number (the AI's node classes: "CDepartureNode = …" in
    // [AiNodes]), read by name; "" when the set has none.
    internal static readonly string[] NamedTables = [AiLabels.NodeTable, AiLabels.ParamTable, AiLabels.InfoTable];

    internal static string Named(string section, string key) => Texts.TryGetValue($"{section}.{key}", out string? text) ? text : "";

    // A text with its places filled.
    internal static string Format(string template, params (string Name, object? Value)[] values)
    {
        var result = new StringBuilder(template.Length + 16);
        int i = 0;
        while (i < template.Length)
        {
            int open = template.IndexOf('{', i);
            if (open < 0) break;
            int close = template.IndexOf('}', open);
            if (close < 0) break;
            result.Append(template, i, open - i);
            string place = template[(open + 1)..close];
            int colon = place.IndexOf(':');
            string name = colon < 0 ? place : place[..colon], spec = colon < 0 ? "" : place[(colon + 1)..];
            int index = Array.FindIndex(values, v => v.Name == name);
            result.Append(index < 0 ? "{" + place + "}" : Fill(values[index].Value, spec));
            i = close + 1;
        }
        return result.Append(template, i, template.Length - i).ToString();
    }

    private static string Fill(object? value, string spec)
    {
        if (spec.Length == 0) return Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
        string word = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
        return spec switch
        {
            "은" or "는" => Korean.Topic(word),
            "이" or "가" => Korean.Subject(word),
            "을" or "를" => Korean.Object(word),
            "과" or "와" => Korean.And(word),
            _ => value is IFormattable formattable ? formattable.ToString(spec, CultureInfo.CurrentCulture) : word
        };
    }

    // "[Section]" lines start a section, "key = text" lines add "Section.key"; "#" starts a comment line. In a text,
    // \n is a line break and \\ a backslash; spaces around it are not part of it.
    internal static Dictionary<string, string> Parse(string set)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        string section = "";
        foreach (string raw in set.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            int equals = line.IndexOf(" = ", StringComparison.Ordinal);
            if (equals < 0) throw new FormatException($"{SetName}: {line}");
            string key = section + "." + line[..equals].Trim();
            if (!parsed.TryAdd(key, Unescape(line[(equals + 3)..].Trim()))) throw new FormatException($"{SetName}: {key} twice");
        }
        return parsed;
    }

    private static string Unescape(string text) => !text.Contains('\\') ? text
        : System.Text.RegularExpressions.Regex.Replace(text, @"\\(.)", m => m.Groups[1].Value == "n" ? "\n" : m.Groups[1].Value);

    private static string ReadSet(string name)
    {
        using Stream stream = typeof(L).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not in {typeof(L).Assembly.GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // The members that read texts, as "Section.Key", for StringsTests: each section class's string properties and
    // methods.
    internal static IEnumerable<(string Key, MemberInfo Member)> Members() =>
        typeof(L).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public).Where(section => !section.Name.StartsWith('<'))
            .SelectMany(section => section.GetMembers(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m is PropertyInfo { PropertyType: var type } && type == typeof(string)
                    || m is MethodInfo { ReturnType: var returns, IsSpecialName: false } method && returns == typeof(string) && method.Name != "T")
                .Select(m => (section.Name + "." + m.Name, m)));
}

namespace San14Tenkazu;

// The particles that follow the sound of the word before them (은/는, 이/가, 을/를, 과/와), for a name or a number put
// into a sentence: 조조는, 하후연은, 3은 (삼), 1,000은 (천), 2는 (이).
internal static class Korean
{
    internal static string Topic(string word) => word + (EndsWithConsonant(word) ? "은" : "는");

    internal static string Subject(string word) => word + (EndsWithConsonant(word) ? "이" : "가");

    internal static string Object(string word) => word + (EndsWithConsonant(word) ? "을" : "를");

    internal static string And(string word) => word + (EndsWithConsonant(word) ? "과" : "와");

    // Whether the word, read aloud, ends with a final consonant. A number is read as Korean reads it: its last digit
    // (0 영, 1 일, 3 삼, 6 육, 7 칠, 8 팔 have one), or for round numbers its last unit (십, 백, 천, 만, 억 have one, 조
    // does not). Anything else (a Latin letter, a bracket) counts as ending in a vowel.
    internal static bool EndsWithConsonant(string word)
    {
        string trimmed = word.TrimEnd();
        if (trimmed.Length == 0) return false;
        char last = trimmed[^1];
        if (last is >= '가' and <= '힣') return (last - '가') % 28 != 0;
        if (!char.IsAsciiDigit(last)) return false;
        string digits = new(trimmed.Reverse().TakeWhile(c => char.IsAsciiDigit(c) || c == ',').Where(char.IsAsciiDigit).Reverse().ToArray());
        int zeros = digits.Length - digits.TrimEnd('0').Length;
        if (zeros == digits.Length) return true;   // 0, 영
        if (zeros == 0) return digits[^1] is '0' or '1' or '3' or '6' or '7' or '8';
        return zeros % 4 != 0 || zeros / 4 != 3;    // 십 백 천, 만 억 (조 has none)
    }
}

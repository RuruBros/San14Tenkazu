using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class RngTests
{
    // Every case of rng.json.gz: [state before, n, the game's result, the game's state after].
    [Fact]
    public void MatchesTheGamesCode()
    {
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("rng.json.gz").GetProperty("cases").EnumerateArray())
        {
            var rng = new Rng(c[0].GetInt32());
            int got = rng.Next(c[1].GetInt32());
            n++;
            if ((got != c[2].GetInt32() || rng.State != c[3].GetInt32()) && wrong.Count < 20)
                wrong.Add($"state {c[0]} n {c[1]}: {got} / {rng.State}, game {c[2]} / {c[3]}");
        }
        Assert.True(n >= 20_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

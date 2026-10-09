using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class CaptureTests
{
    // Every case of capture.json.gz: [a, b, the game's answer (0/1), pokes], officer indexes.
    [Fact]
    public void MatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        Rec[] officers = w.Table(Tables.Officers);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("capture.json.gz").GetProperty("cases").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[3]);
            bool got = Capture.Chance(w, officers[c[0].GetInt32()], officers[c[1].GetInt32()]);
            undo();
            n++;
            if (got != (c[2].GetInt32() != 0) && wrong.Count < 20) wrong.Add($"officers {c[0]} by {c[1]}: {got}, game {c[2]}; pokes {c[3]}");
        }
        Assert.True(n > 3_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class LinkTests
{
    // Every case of link.json.gz: [officer a, officer b, personality term, the game's value, its grade, pokes].
    [Fact]
    public void MatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        JsonElement cases = Vectors.Read("link.json.gz").GetProperty("cases");
        Rec[] officers = w.Table(Tables.Officers);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in cases.EnumerateArray())
        {
            int a = c[0].GetInt32(), b = c[1].GetInt32(), value = c[3].GetInt32(), grade = c[4].GetInt32();
            bool pers = c[2].GetInt32() != 0;
            Action undo = Vectors.Poke(w, c[5]);
            int v = Link.Value(w, officers[a], officers[b], pers);
            int g = Link.Grade(w, officers[a], officers[b], pers);
            undo();
            n++;
            if ((v != value || g != grade) && wrong.Count < 20)
                wrong.Add($"officers {a},{b} pers {pers}: {v}/{g}, game {value}/{grade}; pokes {c[5]}");
        }
        Assert.True(n > 10_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

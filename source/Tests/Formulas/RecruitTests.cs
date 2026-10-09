using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class RecruitTests
{
    // Every case of recruit.json.gz: [target, recruiter, roll in (-1 for the game's), accepts (0/1), roll out, pokes].
    [Fact]
    public void MatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        Rec[] officers = w.Table(Tables.Officers);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("recruit.json.gz").GetProperty("cases").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[5]);
            Recruit.Result got = Recruit.Chance(w, officers[c[0].GetInt32()], officers[c[1].GetInt32()], c[2].GetInt32());
            undo();
            n++;
            if ((got.Accepts != (c[3].GetInt32() != 0) || got.Roll != c[4].GetInt32()) && wrong.Count < 20)
                wrong.Add($"target {c[0]} by {c[1]} roll {c[2]}: {got.Accepts}/{got.Roll}, game {c[3]}/{c[4]}; pokes {c[5]}");
        }
        Assert.True(n > 2_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ExperienceTests
{
    // experience.json.gz: merit [officer, amount, merit after, pokes], exp [officer, ability, amount, exp after,
    // level after, pokes], gain [officer, kind, amount, merit after, exp0 after, level0 after, pokes].
    [Fact]
    public void MeritMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        Rec[] officers = w.Table(Tables.Officers);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("experience.json.gz").GetProperty("merit").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[3]);
            int got = Experience.Merit(w, officers[c[0].GetInt32()], c[1].GetInt32());
            undo();
            n++;
            if (got != c[2].GetInt32() && wrong.Count < 20) wrong.Add($"officer {c[0]} amount {c[1]}: {got}, game {c[2]}; pokes {c[3]}");
        }
        Assert.True(n > 2_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void AbilityExpMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        Rec[] officers = w.Table(Tables.Officers);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("experience.json.gz").GetProperty("exp").EnumerateArray())
        {
            int k = c[1].GetInt32();
            Action undo = Vectors.Poke(w, c[5]);
            // for an ability above 4 the game changes nothing; the vector then holds ability 4's untouched fields
            var got = k > 4 ? (Exp: officers[c[0].GetInt32()].I32(0x114), Level: (int)officers[c[0].GetInt32()].U8(0xF0))
                            : Experience.AbilityExp(w, officers[c[0].GetInt32()], k, c[2].GetInt32());
            undo();
            n++;
            if ((got.Exp != c[3].GetInt32() || got.Level != c[4].GetInt32()) && wrong.Count < 20)
                wrong.Add($"officer {c[0]} ability {c[1]} amount {c[2]}: {got.Exp}/{got.Level}, game {c[3]}/{c[4]}; pokes {c[5]}");
        }
        Assert.True(n > 2_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void GainMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        Rec[] officers = w.Table(Tables.Officers);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("experience.json.gz").GetProperty("gain").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[6]);
            var got = Experience.Gain(w, officers[c[0].GetInt32()], c[1].GetInt32(), c[2].GetInt32());
            undo();
            n++;
            if ((got.Merit != c[3].GetInt32() || got.Exp != c[4].GetInt32() || got.Level != c[5].GetInt32()) && wrong.Count < 20)
                wrong.Add($"officer {c[0]} kind {c[1]} amount {c[2]}: {got.Merit}/{got.Exp}/{got.Level}, game {c[3]}/{c[4]}/{c[5]}; pokes {c[6]}");
        }
        Assert.True(n > 500, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

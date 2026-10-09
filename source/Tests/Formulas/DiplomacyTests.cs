using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class DiplomacyTests
{
    [Fact]
    public void GeographicAndMilitaryPrerequisitesMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("diplomacy.json.gz").GetProperty("prerequisites").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[4]);
            try
            {
                Rec a = w.Table(Tables.Forces)[c[0].GetInt32()], b = w.Table(Tables.Forces)[c[1].GetInt32()];
                bool adjacent = Diplomacy.Adjacent(w, a, b);
                int troops = Diplomacy.Troops(w, a);
                if ((adjacent != (c[2].GetInt32() != 0) || troops != c[3].GetInt32()) && wrong.Count < 20)
                    wrong.Add($"case {n}, forces {c[0]}/{c[1]}: adjacent {adjacent}, troops {troops}, game {c[2]}/{c[3]}; pokes {c[4]}");
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 800, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void Kind7MatchesTheGamesCompleteVerdict()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        var outcomes = new HashSet<bool>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("diplomacy.json.gz").GetProperty("cases").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[5]);
            try
            {
                int kind = c[3].GetInt32();
                bool got = Diplomacy.Judge(w, w.Table(Tables.Forces)[c[0].GetInt32()], w.Table(Tables.Officers)[c[1].GetInt32()], w.Table(Tables.Forces)[c[2].GetInt32()], kind);
                bool expected = c[4].GetInt32() != 0;
                if (kind == 7) outcomes.Add(expected);
                if (got != expected && wrong.Count < 20) wrong.Add($"case {n}, forces {c[0]}/{c[2]}, envoy {c[1]}: {got}, game {expected}; pokes {c[5]}");
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 4_200, $"only {n} cases");
        Assert.Equal(2, outcomes.Count);
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }
}

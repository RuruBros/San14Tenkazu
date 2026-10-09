using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class BombardTests
{
    [Fact]
    public void ChallengeCityRestrictionsAndProtectedDurabilityMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0, protectedCases = 0;
        foreach (JsonElement c in Vectors.Read("bombard_challenge.json.gz").GetProperty("cases").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[5]);
            try
            {
                Rec building = w.Table(Tables.Buildings)[c[0].GetInt32()];
                Rec? unit = c[2].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[c[2].GetInt32()];
                var got = Bombard.Apply(w, building, c[1].GetInt32(), unit);
                if ((got.Durability != c[3].GetInt32() || got.Fell != (c[4].GetInt32() != 0)) && wrong.Count < 20)
                    wrong.Add($"case {n}: {got}, native {c}");
                protectedCases += building.Index == 5 && w.Flag("challenge_active") && w.Value("challenge_kind") == 3
                    && (w.Value("challenge_rules") & 64) != 0 && c[3].GetInt32() == 1 ? 1 : 0;
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 2_400 && protectedCases > 0, $"cases {n}, protected {protectedCases}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    // Every case of bombard.json.gz: [building, damage, unit or null, durability after, fell (0/1), pokes].
    [Fact]
    public void MatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        Rec[] buildings = w.Table(Tables.Buildings), units = w.Table(Tables.Units);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("bombard.json.gz").GetProperty("cases").EnumerateArray())
        {
            Rec? unit = c[2].ValueKind == JsonValueKind.Null ? null : units[c[2].GetInt32()];
            Action undo = Vectors.Poke(w, c[5]);
            Bombard.Result got = Bombard.Apply(w, buildings[c[0].GetInt32()], c[1].GetInt32(), unit);
            undo();
            n++;
            if ((got.Durability != c[3].GetInt32() || got.Fell != (c[4].GetInt32() != 0)) && wrong.Count < 20)
                wrong.Add($"building {c[0]} damage {c[1]} unit {c[2]}: {got.Durability}/{got.Fell}, game {c[3]}/{c[4]}; pokes {c[5]}");
        }
        Assert.True(n > 2_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

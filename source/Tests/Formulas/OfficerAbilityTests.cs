using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class OfficerAbilityTests
{
    // Every case of abilities.json.gz: [officer index, ability, flags, the game's answer, pokes].
    [Fact]
    public void MatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        JsonElement cases = Vectors.Read("abilities.json.gz").GetProperty("cases");
        Rec[] officers = w.Table(Tables.Officers);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in cases.EnumerateArray())
        {
            int i = c[0].GetInt32(), a = c[1].GetInt32(), flags = c[2].GetInt32(), expected = c[3].GetInt32();
            Action undo = Vectors.Poke(w, c[4]);
            int got = OfficerAbility.Get(w, officers[i], a, flags);
            undo();
            n++;
            if (got != expected && wrong.Count < 20) wrong.Add($"officer {i} ability {a} flags {flags:X}: {got}, game {expected}; pokes {c[4]}");
        }
        Assert.True(n > 70_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

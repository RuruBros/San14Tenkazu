using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class InjuryTests
{
    // Every case of injury.json.gz: [person, mode, attacker or null, state after, died (0/1), pokes].
    [Fact]
    public void MatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        Rec[] officers = w.Table(Tables.Officers);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("injury.json.gz").GetProperty("cases").EnumerateArray())
        {
            Rec? attacker = c[2].ValueKind == JsonValueKind.Null ? null : officers[c[2].GetInt32()];
            Action undo = Vectors.Poke(w, c[5]);
            Injury.Result got = Injury.Apply(w, officers[c[0].GetInt32()], c[1].GetInt32(), attacker);
            undo();
            n++;
            if ((got.State != c[3].GetInt32() || got.Died != (c[4].GetInt32() != 0)) && wrong.Count < 20)
                wrong.Add($"officer {c[0]} mode {c[1]} attacker {c[2]}: {got.State}/{got.Died}, game {c[3]}/{c[4]}; pokes {c[5]}");
        }
        Assert.True(n > 2_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

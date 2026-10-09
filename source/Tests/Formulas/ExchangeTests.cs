using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ExchangeTests
{
    // Every case of exchange.json.gz: [A, envoy, B, [gold, food, item] given, [gold, food, item] asked, verdict, pokes].
    [Fact]
    public void MatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        Rec[] officers = w.Table(Tables.Officers), forces = w.Table(Tables.Forces);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("exchange.json.gz").GetProperty("cases").EnumerateArray())
        {
            var give = new Exchange.Offer(c[3][0].GetInt32(), c[3][1].GetInt32(), c[3][2].GetInt32());
            var ask = new Exchange.Offer(c[4][0].GetInt32(), c[4][1].GetInt32(), c[4][2].GetInt32());
            Action undo = Vectors.Poke(w, c[6]);
            bool got = Exchange.Judge(w, forces[c[0].GetInt32()], officers[c[1].GetInt32()], forces[c[2].GetInt32()], give, ask);
            undo();
            n++;
            if (got != (c[5].GetInt32() != 0) && wrong.Count < 20)
                wrong.Add($"{c[0]} ({c[1]}) -> {c[2]} give {c[3]} ask {c[4]}: {got}, game {c[5]}; pokes {c[6]}");
        }
        Assert.True(n > 2_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

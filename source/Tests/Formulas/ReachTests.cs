using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ReachTests
{
    // reach.json.gz: hostile [a, b, result, pokes] (force numbers), objectat [hex, kind, number, pokes] and reach
    // [from, to, result, pokes] (hexes).
    [Fact]
    public void HostileMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("reach.json.gz").GetProperty("hostile").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[3]);
            bool got = Reach.Hostile(w, c[0].GetInt32(), c[1].GetInt32());
            undo();
            n++;
            if (got != (c[2].GetInt32() != 0) && wrong.Count < 20) wrong.Add($"forces {c[0]} vs {c[1]}: {got}, game {c[2]}; pokes {c[3]}");
        }
        Assert.True(n > 2_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void ObjectAtMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("reach.json.gz").GetProperty("objectat").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[3]);
            var got = Reach.ObjectAt(w, w.Table(Tables.Tiles)[c[0].GetInt32()]);
            undo();
            n++;
            if ((got.Kind != c[1].GetInt32() || got.Number != c[2].GetInt32()) && wrong.Count < 20)
                wrong.Add($"hex {c[0]}: {got.Kind:X}/{got.Number}, game {c[1].GetInt32():X}/{c[2]}; pokes {c[3]}");
        }
        Assert.True(n > 1_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void CanAttackMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("reach.json.gz").GetProperty("reach").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[3]);
            bool got = Reach.CanAttack(w, c[0].GetInt32(), c[1].GetInt32());
            undo();
            n++;
            if (got != (c[2].GetInt32() != 0) && wrong.Count < 20) wrong.Add($"hex {c[0]} -> {c[1]}: {got}, game {c[2]}; pokes {c[3]}");
        }
        Assert.True(n > 3_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

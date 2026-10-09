using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class StepEndTests
{
    // Every case of stepend.json.gz: [[index, kind, hex, force] a, [..] b, [agri, comm, barr, pop, order, relAB,
    // relBA, ownerPanel] after, pokes].
    [Fact]
    public void MatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        StepEnd.Side Side(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32());
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("stepend.json.gz").GetProperty("cases").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[3]);
            StepEnd.Result got = StepEnd.Apply(w, Side(c[0]), Side(c[1]));
            undo();
            n++;
            int[] expected = c[2].EnumerateArray().Select(x => x.GetInt32()).ToArray();
            int[] actual = [got.Agriculture, got.Commerce, got.Barracks, got.Population, got.Order, got.RelationAB, got.RelationBA, got.OwnerPanel];
            if (!actual.SequenceEqual(expected) && wrong.Count < 20)
                wrong.Add($"{c[0]} vs {c[1]}: [{string.Join(",", actual)}], game [{string.Join(",", expected)}]; pokes {c[3]}");
        }
        Assert.True(n > 1_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

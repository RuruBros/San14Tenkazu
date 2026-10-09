using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class WoundedTests
{
    // wounded.json.gz: the wounded share [person, morale, flag, terrain, attacker, hasAttacker, float bits, pokes],
    // the morale loss [wounded, troops, lead, damage, result, pokes] and the seeded random [n, div, a, b, c, d, result,
    // pokes].
    [Fact]
    public void RatioMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        Rec[] officers = w.Table(Tables.Officers), terrains = w.Table(Tables.Terrains);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("wounded.json.gz").GetProperty("ratio").EnumerateArray())
        {
            Rec? person = c[0].ValueKind == JsonValueKind.Null ? null : officers[c[0].GetInt32()];
            Rec? terrain = c[3].ValueKind == JsonValueKind.Null ? null : terrains[c[3].GetInt32()];
            Rec? attacker = c[4].ValueKind == JsonValueKind.Null ? null : officers[c[4].GetInt32()];
            Action undo = Vectors.Poke(w, c[7]);
            float got = Wounded.Ratio(w, person, c[1].GetInt32(), c[2].GetInt32() != 0, terrain, attacker, c[5].GetInt32() != 0);
            undo();
            n++;
            if (BitConverter.SingleToUInt32Bits(got) != c[6].GetUInt32() && wrong.Count < 20)
                wrong.Add($"{c[0]} morale {c[1]} flag {c[2]} force {c[3]} attacker {c[4]}/{c[5]}: {got:R}, game {BitConverter.UInt32BitsToSingle(c[6].GetUInt32()):R}; pokes {c[7]}");
        }
        Assert.True(n > 3_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void MoraleLossMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("wounded.json.gz").GetProperty("morale").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[5]);
            int got = Wounded.MoraleLoss(w, c[0].GetInt32(), c[1].GetInt32(), c[2].GetInt32(), c[3].GetInt32());
            undo();
            n++;
            if (got != c[4].GetInt32() && wrong.Count < 20) wrong.Add($"{c[0]} {c[1]} {c[2]} {c[3]}: {got}, game {c[4]}; pokes {c[5]}");
        }
        Assert.True(n > 3_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void SeededRandomMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("wounded.json.gz").GetProperty("seeded").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[7]);
            int got = Wounded.Seeded(w, c[0].GetInt32(), c[1].GetInt32(), c[2].GetInt32(), c[3].GetInt32(), c[4].GetInt32(), c[5].GetInt32());
            undo();
            n++;
            if (got != c[6].GetInt32() && wrong.Count < 20) wrong.Add($"{c[0]} {c[1]} {c[2]} {c[3]} {c[4]} {c[5]}: {got}, game {c[6]}; pokes {c[7]}");
        }
        Assert.True(n > 3_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

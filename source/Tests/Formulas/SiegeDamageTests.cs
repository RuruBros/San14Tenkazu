using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class SiegeDamageTests
{
    // Every case of siegedmg.json.gz: [unit, formation or null, effects (0/1), float bits, pokes].
    [Fact]
    public void MatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        Rec[] units = w.Table(Tables.Units), formations = w.Table(Tables.Formations);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("siegedmg.json.gz").GetProperty("cases").EnumerateArray())
        {
            Rec? formation = c[1].ValueKind == JsonValueKind.Null ? null : formations[c[1].GetInt32()];
            Action undo = Vectors.Poke(w, c[4]);
            float got = Attack.SiegeDamage(w, units[c[0].GetInt32()], formation, c[2].GetInt32() != 0);
            undo();
            n++;
            if (BitConverter.SingleToUInt32Bits(got) != c[3].GetUInt32() && wrong.Count < 20)
                wrong.Add($"unit {c[0]} formation {c[1]} effects {c[2]}: {got:R}, game {BitConverter.UInt32BitsToSingle(c[3].GetUInt32()):R}; pokes {c[4]}");
        }
        Assert.True(n > 3_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

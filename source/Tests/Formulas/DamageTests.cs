using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class DamageTests
{
    // Every case of damage.json.gz: [[table, index] attacker, atkA bits, defA bits, [table, index] defender, atkB
    // bits, defB bits, the game's damage, pokes].
    [Fact]
    public void MatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        JsonElement cases = Vectors.Read("damage.json.gz").GetProperty("cases");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in cases.EnumerateArray())
        {
            Rec a = w.Table(Convert.ToInt32(c[0][0].GetString()!, 16))[c[0][1].GetInt32()];
            Rec b = w.Table(Convert.ToInt32(c[3][0].GetString()!, 16))[c[3][1].GetInt32()];
            float atkA = BitConverter.UInt32BitsToSingle(c[1].GetUInt32()), defA = BitConverter.UInt32BitsToSingle(c[2].GetUInt32());
            float atkB = BitConverter.UInt32BitsToSingle(c[4].GetUInt32()), defB = BitConverter.UInt32BitsToSingle(c[5].GetUInt32());
            int expected = c[6].GetInt32();
            Action undo = Vectors.Poke(w, c[7]);
            int got = Damage.Get(w, a, atkA, defA, b, atkB, defB);
            undo();
            n++;
            if (got != expected && wrong.Count < 20)
                wrong.Add($"{c[0]} ({atkA}/{defA}) vs {c[3]} ({atkB}/{defB}): {got}, game {expected}; pokes {c[7]}");
        }
        Assert.True(n > 3_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

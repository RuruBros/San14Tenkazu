using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class BattlePowerTests
{
    // Every case of battlepower.json.gz (no effects) and battlepower_fx.json.gz (facilities and allies):
    // [unit slot, split, effects, the game's float bits, pokes].
    [Theory]
    [InlineData("battlepower.json.gz")]
    [InlineData("battlepower_fx.json.gz")]
    public void MatchesTheGamesCode(string file)
    {
        World w = Vectors.LoadWorld("world.json.gz");
        JsonElement cases = Vectors.Read(file).GetProperty("cases");
        Rec[] units = w.Table(Tables.Units);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in cases.EnumerateArray())
        {
            int i = c[0].GetInt32(), split = c[1].GetInt32();
            bool effects = c[2].GetInt32() != 0;
            uint expected = c[3].GetUInt32();
            Action undo = Vectors.Poke(w, c[4]);
            float got = BattlePower.Get(w, units[i], null, split, effects);
            undo();
            n++;
            uint bits = BitConverter.SingleToUInt32Bits(got);
            if (bits != expected && wrong.Count < 20)
                wrong.Add($"unit {i} split {split}: {got:R}, game {BitConverter.UInt32BitsToSingle(expected):R}; pokes {c[4]}");
        }
        Assert.True(n > 5_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

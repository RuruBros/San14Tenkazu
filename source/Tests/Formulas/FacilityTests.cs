using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class FacilityTests
{
    // Every case of facility.json.gz: [tile, force, the game's float bits, its sum (0x5A5A5A5A: not written), pokes].
    [Fact]
    public void MatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        JsonElement cases = Vectors.Read("facility.json.gz").GetProperty("cases");
        Rec[] tiles = w.Table(Tables.Tiles);
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in cases.EnumerateArray())
        {
            int t = c[0].GetInt32(), fi = c[1].GetInt32(), expectedSum = c[3].GetInt32();
            uint expected = c[2].GetUInt32();
            Action undo = Vectors.Poke(w, c[4]);
            float got = BattlePower.Facility(w, tiles[t], fi, out int sum);
            undo();
            n++;
            bool sumOk = expectedSum == 0x5A5A5A5A ? sum == 0 : sum == expectedSum;
            if ((BitConverter.SingleToUInt32Bits(got) != expected || !sumOk) && wrong.Count < 20)
                wrong.Add($"tile {t} force {fi}: {got:R} sum {sum}, game {BitConverter.UInt32BitsToSingle(expected):R} sum {expectedSum}; pokes {c[4]}");
        }
        Assert.True(n > 5_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

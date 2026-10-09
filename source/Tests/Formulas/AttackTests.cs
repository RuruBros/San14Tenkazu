using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class AttackTests
{
    // Every case of attack.json.gz (0x2B21A0), siege.json.gz (0x2B2440) and defense.json.gz (0x2A9E50): [unit slot
    // or null, base table or null, base index, power bits, a5, the game's float bits, pokes]. The formation is the
    // unit's as it stands after the pokes, as the game passes it.
    [Theory]
    [InlineData("attack.json.gz")]
    [InlineData("siege.json.gz")]
    [InlineData("defense.json.gz")]
    public void MatchesTheGamesCode(string file)
    {
        World w = Vectors.LoadWorld("world.json.gz");
        JsonElement cases = Vectors.Read(file).GetProperty("cases");
        Func<World, float, Rec?, Rec?, Rec?, bool, float> formula = file switch
        {
            "attack.json.gz" => Attack.VsUnit, "siege.json.gz" => Attack.VsBase, _ => Attack.Defense,
        };
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in cases.EnumerateArray())
        {
            Rec? unit = c[0].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[c[0].GetInt32()];
            Rec? b = c[1].ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(c[1].GetString()!, 16))[c[2].GetInt32()];
            float power = BitConverter.UInt32BitsToSingle(c[3].GetUInt32());
            bool a5 = c[4].GetInt32() != 0;
            uint expected = c[5].GetUInt32();
            Action undo = Vectors.Poke(w, c[6]);
            Rec? formation = unit is null ? null : Objects.FormationOf(w, unit);
            float got = formula(w, power, unit, b, formation, a5);
            undo();
            n++;
            if (BitConverter.SingleToUInt32Bits(got) != expected && wrong.Count < 20)
                wrong.Add($"unit {c[0]} base {c[1]}/{c[2]} power {power} a5 {a5}: {got:R}, game {BitConverter.UInt32BitsToSingle(expected):R}; pokes {c[6]}");
        }
        Assert.True(n > 3_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

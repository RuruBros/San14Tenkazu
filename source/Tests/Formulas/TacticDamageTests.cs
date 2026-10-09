using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TacticDamageTests
{
    private static Tactic.Side Side(World w, JsonElement source) => new(
        source[0].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[source[0].GetInt32()],
        source[1].ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(source[1][0].GetString()!, 16))[source[1][1].GetInt32()]);

    [Fact]
    public void DeferredDamageAndMapsMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        var kinds = new HashSet<int>();
        int n = 0, positive = 0;
        foreach (JsonElement c in Vectors.Read("tactic_damage.json.gz").GetProperty("compute").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[8]);
            try
            {
                Rec tile = w.Table(Tables.Tiles)[c[4].GetInt32()];
                var at = Reach.ObjectAt(w, tile);
                int? damage = TacticEffects.DamageFor(w, w.Table(Tables.Tactics)[c[0].GetInt32()], Side(w, c[1]), c[2].GetInt32(), c[3].GetInt32(), tile);
                TacticEffects.Applied[] got = damage is { } amount ? [new(at.Kind, at.Number, tile.Index, amount & 0xFFFF)] : [];
                var expected = c[6].EnumerateArray().Select(e => new TacticEffects.Applied(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32())).ToArray();
                if (!got.SequenceEqual(expected) && wrong.Count < 20)
                    wrong.Add($"case {n}, tactic {c[0]}, source {c[1]}, hex {c[4]}: damage {damage}, game {c[6]}; pokes {c[8]}");
                if (expected.Length != 0) kinds.Add(expected[0].Kind);
                if (damage > 0) positive++;
                for (int k = 0; k < 2; k++)
                {
                    var map = c[7][k].EnumerateArray().Select(e => (Table: Convert.ToInt32(e[0].GetString()!, 16), Index: e[1].GetInt32(), Damage: e[2].GetInt32())).ToArray();
                    bool mapped = damage is not null && (k == 0 ? at.Kind == 27 : at.Kind is 5 or 6);
                    int table = at.Kind == 27 ? Tables.Units : at.Kind == 5 ? Tables.Cities : Tables.Gates;
                    if ((map.Length != (mapped ? 1 : 0) || mapped && map[0] != (table, at.Number, damage!.Value)) && wrong.Count < 20)
                        wrong.Add($"case {n}, map {k}: damage {damage}, object {at}, game {c[7][k]}; pokes {c[8]}");
                }
                foreach (JsonElement record in c[5].EnumerateArray())
                {
                    Rec r = w.Table(Convert.ToInt32(record[0].GetString()!, 16))[record[1].GetInt32()];
                    if (!r.Bytes.SequenceEqual(Convert.FromHexString(record[2].GetString()!)) && wrong.Count < 20)
                        wrong.Add($"case {n}: deferred target {r.Table:X}/{r.Index} changed in native code");
                }
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 5_000, $"only {n} cases");
        Assert.True(positive > 1_000, $"only {positive} positive damage cases");
        Assert.True(new[] {5, 6, 27}.All(kinds.Contains), $"target kinds: {string.Join(',', kinds)}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }
}

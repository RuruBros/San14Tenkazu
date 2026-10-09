using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TacticTests
{
    // Every case of tactic.json.gz: [attacker [unit, base] (indexes or null), defender [unit, base], tactic indexes,
    // the game's answer (the index of the tactic that fired, or -1), pokes].
    [Fact]
    public void MatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        Rec[] units = w.Table(Tables.Units), cities = w.Table(Tables.Cities), gates = w.Table(Tables.Gates), tactics = w.Table(Tables.Tactics);
        Rec? Base(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e[0].GetString() == "city" ? cities[e[1].GetInt32()] : gates[e[1].GetInt32()];
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("tactic.json.gz").GetProperty("cases").EnumerateArray())
        {
            var a = new Tactic.Side(c[0][0].ValueKind == JsonValueKind.Null ? null : units[c[0][0].GetInt32()], Base(c[0][1]));
            var d = new Tactic.Side(c[1][0].ValueKind == JsonValueKind.Null ? null : units[c[1][0].GetInt32()], Base(c[1][1]));
            List<Rec> list = c[2].EnumerateArray().Select(t => tactics[t.GetInt32()]).ToList();
            int expected = c[3].GetInt32();
            Action undo = Vectors.Poke(w, c[4]);
            Rec? fired = Tactic.Fire(w, a, d, list);
            undo();
            n++;
            int got = fired is null ? -1 : fired.Index;
            if (got != expected && wrong.Count < 20) wrong.Add($"{c[0]} vs {c[1]} tactics {c[2]}: {got}, game {expected}; pokes {c[4]}");
        }
        Assert.True(n > 3_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

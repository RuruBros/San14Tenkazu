using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TacticLinkTests
{
    [Fact]
    public void OrderedUnitsSelectedTacticsAndBothTargetListsMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0, selected = 0, capped = 0;
        foreach (JsonElement c in Vectors.Read("tactic_link.json.gz").GetProperty("cases").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[5]);
            try
            {
                Rec person = w.Table(Tables.Officers)[c[0].GetInt32()];
                Rec? target = c[1].ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Tiles)[c[1].GetInt32()];
                var units = c[3].EnumerateArray().Select(e => w.Table(Tables.Units)[e.GetInt32()]).ToArray();
                var got = TacticLink.Select(w, person, target, c[2].GetInt32(), units);
                var expected = c[4].EnumerateArray().ToArray();
                bool match = got.Count == expected.Length;
                if (match)
                    for (int i = 0; i < got.Count; i++)
                    {
                        match &= expected[i][0].GetInt32() == 27 && got[i].Unit.Index == expected[i][1].GetInt32()
                            && got[i].Tactic.Index == expected[i][2].GetInt32();
                        for (int slot = 0; slot < 2; slot++)
                            match &= got[i].Targets[slot].Select(r => r.Index).SequenceEqual(expected[i][3][slot].EnumerateArray().Select(e => e.GetInt32()));
                    }
                if (!match && wrong.Count < 20)
                    wrong.Add($"case {n}: selected [{string.Join(',', got.Select(e => $"{e.Unit.Index}/{e.Tactic.Index}: [{string.Join(',', e.Targets[0].Select(t => t.Index))}]/[{string.Join(',', e.Targets[1].Select(t => t.Index))}]"))}], game {c[4]}; pokes {c[5]}");
                selected += expected.Length;
                capped += expected.Length == 10 ? 1 : 0;
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 1_200 && selected > 0 && capped > 0, $"cases {n}, selected {selected}, capped {capped}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }
}

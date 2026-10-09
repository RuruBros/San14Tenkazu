using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TacticLinkGraphTests
{
    [Fact]
    public void ExpandedOrderedGraphsAndSelectedQueuesMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0, nonempty = 0, expanded = 0, selected = 0;
        foreach (JsonElement c in Vectors.Read("tactic_link_graph.json.gz").GetProperty("cases").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[4]);
            try
            {
                Rec source = w.Table(Tables.Units)[c[0].GetInt32()];
                Rec target = w.Table(Tables.Tiles)[c[1].GetInt32()];
                var graph = TacticLink.CandidateUnits(w, source);
                var expectedGraph = c[3][0].EnumerateArray().Select(e => e.GetInt32()).ToArray();
                bool match = graph.Select(u => u.Index).SequenceEqual(expectedGraph);
                Rec? person = w.OfficerById(source.U16(0x12));
                var got = TacticLink.Select(w, person, target, c[2].GetInt32(), graph);
                var expected = c[3][1].EnumerateArray().ToArray();
                match &= got.Count == expected.Length;
                if (got.Count == expected.Length)
                    for (int i = 0; i < got.Count; i++)
                    {
                        match &= expected[i][0].GetInt32() == 27 && got[i].Unit.Index == expected[i][1].GetInt32()
                            && got[i].Tactic.Index == expected[i][2].GetInt32();
                        for (int slot = 0; slot < 2; slot++)
                            match &= got[i].Targets[slot].Select(r => r.Index).SequenceEqual(expected[i][3][slot].EnumerateArray().Select(e => e.GetInt32()));
                    }
                if (!match && wrong.Count < 20)
                    wrong.Add($"case {n}: graph [{string.Join(',', graph.Select(u => u.Index))}], native {c[3]}; pokes {c[4]}");
                nonempty += expectedGraph.Length > 0 ? 1 : 0;
                expanded += expectedGraph.Length > BattlePower.AlliedUnits(w, source).Count ? 1 : 0;
                selected += expected.Length;
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 1_200 && nonempty > 0 && expanded > 0 && selected > 0,
            $"cases {n}, nonempty {nonempty}, expanded {expanded}, selected {selected}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }
}

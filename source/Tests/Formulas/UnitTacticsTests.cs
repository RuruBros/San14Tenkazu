using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class UnitTacticsTests
{
    [Fact]
    public void OfficerSlotsMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("unit_tactics.json.gz").GetProperty("slots").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[3]);
            try
            {
                int got = UnitTactics.OfficerSlot(w, w.Table(Tables.Officers)[c[0].GetInt32()], c[1].GetInt32());
                if (got != c[2].GetInt32() && wrong.Count < 20)
                    wrong.Add($"case {n}: slot {got}, game {c[2]}; pokes {c[3]}");
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 2_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void UnitCandidateListsMatchTheGamesCodeInOrder()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0, nonempty = 0;
        foreach (JsonElement c in Vectors.Read("unit_tactics.json.gz").GetProperty("candidates").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[2]);
            try
            {
                int[] got = UnitTactics.Candidates(w, w.Table(Tables.Units)[c[0].GetInt32()]).Select(r => r.Index).ToArray();
                int[] expected = c[1].EnumerateArray().Select(e => e.GetInt32()).ToArray();
                if (expected.Length != 0) nonempty++;
                if (!got.SequenceEqual(expected) && wrong.Count < 20)
                    wrong.Add($"case {n}, unit {c[0]}: candidates [{string.Join(',', got)}], game {c[1]}; pokes {c[2]}");
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 3_000 && nonempty > 0, $"only {n} cases, {nonempty} nonempty");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }
}

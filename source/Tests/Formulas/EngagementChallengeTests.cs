using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class EngagementChallengeTests
{
    [Fact]
    public void ChallengeTroopApplicationMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        JsonElement data = Vectors.Read("engagement_challenge.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in data.GetProperty("cases").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[6]);
            try
            {
                var a = new Engagement.Side(c[0][0].GetInt32(), c[0][1].GetInt32(), c[0][2].GetInt32(), c[0][3].GetInt32(), c[0][4].GetInt32());
                Rec b = w.Table(Convert.ToInt32(c[1][0].GetString()!, 16))[c[1][1].GetInt32()];
                var input = new EngagementTactic.Frame(c[2][0].GetUInt16(), c[2][1].GetUInt16(), c[2][2].GetInt32());
                var combined = EngagementTactic.Combine(input);
                var result = Engagement.ApplyBase(w, b, combined.TotalDamage, a);
                int[] got = [result.Troops, result.Wounded, result.Morale];
                int[] expected = c[3].EnumerateArray().Select(x => x.GetInt32()).ToArray();
                int[] frame = [combined.Frame.BasicDamage, combined.Frame.ExtraDamage, combined.Frame.TacticFired];
                if ((!got.SequenceEqual(expected) || !frame.SequenceEqual(c[4].EnumerateArray().Select(x => x.GetInt32())) || c[5].GetInt32() != 8) && wrong.Count < 20)
                    wrong.Add($"case {n}: [{string.Join(',', got)}], native {c[3]}, frame {string.Join(',', frame)}; pokes {c[6]}");
            }
            finally { undo(); }
            n++;
        }
        Assert.Equal(1200, n);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.True(data.GetProperty("protected").GetInt32() > 20);
    }
}

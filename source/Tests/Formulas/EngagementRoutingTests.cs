using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class EngagementRoutingTests
{
    [Fact]
    public void RecordAdmissionMatchesTheRealCallerBeforeStepOne()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0, admitted = 0;
        foreach (JsonElement c in Vectors.Read("engagement_routing.json.gz").GetProperty("admission").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[4]);
            try
            {
                bool got = Engagement.CanResolve(w, Side(c[0]), Side(c[1]));
                bool expected = c[2].GetInt32() != 0;
                if (got != expected && wrong.Count < 20) wrong.Add($"case {n}: admitted {got}, native {expected}; {c}");
                Assert.Equal(1, c[3].GetInt32());
                admitted += expected ? 1 : 0;
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 2400 && admitted > 50 && admitted < n - 50, $"cases {n}, admitted {admitted}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void FailedSiegeReachStillRoutesUnitAndBaseRecordsToBasicDamage()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0, noReachBasic = 0, missing = 0, siege = 0;
        foreach (JsonElement c in Vectors.Read("engagement_routing.json.gz").GetProperty("start").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[6]);
            try
            {
                Engagement.Side a = Side(c[0]), b = Side(c[1]);
                bool absent = c[2].GetBoolean();
                Engagement.StartResult got = Engagement.Start(w, absent ? null : a, b);
                int id = b.Kind == 11 ? b.Index : b.Kind is 5 or 6 ? w.Table(b.Kind == 5 ? Tables.Cities : Tables.Gates)[b.Index].U16(0x4E) : 0;
                int durability = got.Siege?.Durability ?? w.Table(Tables.Buildings)[id].U16(0x14);
                if ((got.NextStep != c[4].GetInt32() || durability != c[5].GetInt32()) && wrong.Count < 20)
                    wrong.Add($"case {n}: step {got.NextStep}, durability {durability}; {c}");
                noReachBasic += !absent && c[3].GetInt32() == 0 && b.Kind is 5 or 6 or 27 && c[4].GetInt32() == 2 ? 1 : 0;
                missing += absent ? 1 : 0;
                siege += got.Siege is not null ? 1 : 0;
                n++;
            }
            finally { undo(); }
        }
        Assert.True(n >= 1200 && noReachBasic > 100 && missing > 0 && siege > 50,
            $"cases {n}, no-reach basic routes {noReachBasic}, missing {missing}, siege {siege}");
        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    private static Engagement.Side Side(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32(), e[4].GetInt32());
}

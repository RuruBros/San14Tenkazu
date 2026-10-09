using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class PrisonerMembersTests
{
    [Fact]
    public void RegisteredPrisonersMatchNativeListOrder()
    {
        var root = Vectors.Read("prisoner_members.json.gz");
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            var fixture = c.GetProperty("fixture");
            Vectors.Poke(w, fixture.GetProperty("pokes"));
            Rec unit = w.Table(Tables.Units)[fixture.GetProperty("unit")[1].GetInt32()];
            Assert.Equal(c.GetProperty("members").EnumerateArray().Select(r => w.Table(Tables.Officers)[r[1].GetInt32()]),
                PrisonerRelease.HeldBy(w, unit));
            Assert.Equal(c.GetProperty("result").GetInt32(), PrisonerRelease.HeldBy(w, unit).Count());
        }
    }
}

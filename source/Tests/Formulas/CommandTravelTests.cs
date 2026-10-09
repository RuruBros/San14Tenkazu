using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class CommandTravelTests
{
    [Fact]
    public void CalendarTravelAndLocationBranchesMatchNativeHelpers()
    {
        var root = Vectors.Read("command_travel.json.gz");
        var snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        foreach (var c in root.GetProperty("dates").EnumerateArray())
        {
            Rec date = new(0, Convert.FromHexString(c.GetProperty("before").GetString()!));
            CommandTravel.SetDateDays(date, 0, c.GetProperty("days").GetInt32());
            Assert.Equal(Convert.FromHexString(c.GetProperty("after").GetString()!), date.Bytes);
            Assert.Equal(c.GetProperty("encoded").GetInt32(), CommandTravel.DateDays(date, 0));
            Assert.Equal(c.GetProperty("valid").GetBoolean(), CommandTravel.DateValid(date, 0));
        }
        foreach (var c in root.GetProperty("travel").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            Vectors.Poke(w, c.GetProperty("pokes"));
            Rec? force = c.GetProperty("force").ValueKind == System.Text.Json.JsonValueKind.Null ? null : w.Table(Tables.Forces)[c.GetProperty("force").GetInt32()];
            Assert.Equal(c.GetProperty("days").GetInt32(), CommandTravel.Days(w, c.GetProperty("steps").GetInt32(), force));
        }
        foreach (var c in root.GetProperty("locations").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            Vectors.Poke(w, c.GetProperty("pokes"));
            var command = ReturnCommandTests.Commands(w, snapshot)[c.GetProperty("command").GetInt32()];
            foreach (var p in c.GetProperty("command_pokes").EnumerateArray()) Convert.FromHexString(p[1].GetString()!).CopyTo(command.Data.Bytes, p[0].GetInt32());
            Assert.Equal(c.GetProperty("tile").GetInt32(), CommandTravel.Location(w, command).Index);
        }
        foreach (var c in root.GetProperty("officer_locations").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            Vectors.Poke(w, c.GetProperty("pokes"));
            var commands = ReturnCommandTests.Commands(w, snapshot);
            var actual = CommandTravel.OfficerLocation(w, ReturnCommandTests.Storage(w, snapshot, commands), w.Table(Tables.Officers)[c.GetProperty("person").GetInt32()]);
            if (c.GetProperty("tile").ValueKind == System.Text.Json.JsonValueKind.Null) Assert.Null(actual);
            else Assert.Equal(c.GetProperty("tile").GetInt32(), actual!.Index);
        }
    }
}

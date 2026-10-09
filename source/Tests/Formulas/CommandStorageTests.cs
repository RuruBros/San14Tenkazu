using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class CommandStorageTests
{
    private static ulong Address(string text) => Convert.ToUInt64(text[2..], 16);

    private static void Compare(CommandStorage storage, JsonElement expected)
    {
        Assert.Equal(expected.GetProperty("free_head").GetInt32(), storage.FreeHead);
        Assert.Equal(expected.GetProperty("free_ids").EnumerateArray().Select(p => p.GetUInt16()), storage.FreeIds);
        Assert.Equal(expected.GetProperty("by_id").EnumerateObject().Select(p => (int.Parse(p.Name), Address(p.Value.GetString()!))).Order(),
            storage.ById.Select(p => (p.Key, p.Value.Data.NativeAddress)).Order());
        Assert.Equal(expected.GetProperty("active").EnumerateArray().Select(p => Address(p.GetString()!)), storage.Active.Select(p => p.Data.NativeAddress));
        Assert.Equal(expected.GetProperty("pending").EnumerateArray().Select(p => Address(p.GetString()!)), storage.Pending.Select(p => p.Data.NativeAddress));
    }

    [Fact]
    public void RegistrationRemovalAndCallbackOrderMatchNativeManager()
    {
        var root = Vectors.Read("command_storage.json.gz");
        var snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            var fixture = c.GetProperty("fixture");
            var commands = snapshot.GetProperty("commands").EnumerateArray().Select((command, i) =>
                new CommandCancellation.Command(command.GetProperty("kind").GetInt32(),
                    new Rec(i, Convert.FromHexString(command.GetProperty("data").GetString()!), nativeAddress: Address(command.GetProperty("address").GetString()!)),
                    command.GetProperty("officers").EnumerateArray().Select(p => p.GetInt32()).ToArray(), 0, 0, 0, 0)).ToArray();
            var pointers = commands.ToDictionary(p => p.Data.NativeAddress);
            var initial = snapshot.GetProperty("command_storage");
            var byId = initial.GetProperty("by_id").EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => pointers[Address(p.Value.GetString()!)]);
            var active = initial.GetProperty("active").EnumerateArray().Select(p => pointers[Address(p.GetString()!)]).ToList();
            var pending = initial.GetProperty("pending").EnumerateArray().Select(p => pointers[Address(p.GetString()!)]).ToList();
            if (fixture.TryGetProperty("alias", out _)) active[1] = active[0];
            ushort head = fixture.TryGetProperty("free_head", out var headOverride) ? headOverride.GetUInt16() : initial.GetProperty("free_head").GetUInt16();
            int eventIndex = 0;
            var events = c.GetProperty("events").EnumerateArray().ToArray();
            CommandStorage? storage = null;
            void Callback(string kind, CommandCancellation.Command command)
            {
                var expected = events[eventIndex++];
                Assert.Equal(kind, expected.GetProperty("kind").GetString());
                Assert.Equal(Address(expected.GetProperty("command").GetString()!), command.Data.NativeAddress);
                Compare(storage!, expected.GetProperty("store"));
            }
            storage = new(w, byId, active, pending, initial.GetProperty("free_ids").EnumerateArray().Select(p => p.GetUInt16()).ToArray(),
                head, command => Callback("detach", command), command => Callback("dispose", command));
            var commandIndex = fixture.GetProperty("command");
            var selected = commandIndex.ValueKind == JsonValueKind.Null ? null : commands[commandIndex.GetInt32()];
            if (fixture.GetProperty("operation").GetString() == "register")
            {
                Assert.Equal(c.GetProperty("success").GetBoolean(), storage.Register(selected!));
                Assert.Equal(Convert.FromHexString(c.GetProperty("data").GetString()!), selected!.Data.Bytes);
            }
            else storage.Remove(selected, fixture.GetProperty("mode").GetInt32());
            Compare(storage, c.GetProperty("store"));
            Assert.Equal(events.Length, eventIndex);
            Assert.Equal(active.Select(p => (p.Kind, string.Join(",", p.Officers))),
                w.CmdActions.Select(p => (p.Kind, string.Join(",", p.Officers))));
            var delta = c.GetProperty("delta");
            Assert.Empty(delta.GetProperty("records").EnumerateArray());
            foreach (var section in delta.EnumerateObject()) Assert.Contains(section.Name, new[] { "records", "cmdactions" });
            foreach (var table in snapshot.GetProperty("tables").EnumerateObject())
                Assert.Equal(table.Value.GetProperty("records").EnumerateArray().Select(r => r.GetString()),
                    w.Table(Convert.ToInt32(table.Name[2..], 16)).Select(r => Convert.ToHexString(r.Bytes).ToLowerInvariant()));
        }
    }
}

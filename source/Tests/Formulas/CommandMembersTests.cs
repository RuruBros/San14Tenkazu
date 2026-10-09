using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class CommandMembersTests
{
    [Fact]
    public void MemberRemovalMatchesNativeBoundsDuplicatesAndUnchangedStorage()
    {
        var root = Vectors.Read("command_members.json.gz");
        var snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            var commands = ReturnCommandTests.Commands(w, snapshot);
            var f = c.GetProperty("fixture");
            var selected = commands[f.GetProperty("command").GetInt32()];
            selected.Officers = f.GetProperty("officer_ids").EnumerateArray().Select(p => p.GetInt32()).ToArray();
            var storage = ReturnCommandTests.Storage(w, snapshot, commands, _ => Assert.Fail("Unexpected UI detach"),
                _ => Assert.Fail("Unexpected command destruction"));
            storage.Refresh();
            var active = storage.Active.Select(p => p.Data.NativeAddress).ToArray();
            var pending = storage.Pending.Select(p => p.Data.NativeAddress).ToArray();
            var mapping = storage.ById.Select(p => (p.Key, p.Value.Data.NativeAddress)).Order().ToArray();
            var before = w.CmdActions.Select(p => (p.Kind, string.Join(",", p.Officers))).ToArray();
            var records = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(t => t.Name,
                t => w.Table(Convert.ToInt32(t.Name[2..], 16)).Select(r => r.Bytes.ToArray()).ToArray());
            byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
            storage.RemoveOfficer(selected, f.GetProperty("id").GetInt32());
            var delta = c.GetProperty("delta");
            var expected = delta.TryGetProperty("cmdactions", out var actions)
                ? actions.EnumerateArray().Select(p => (p.GetProperty("kind").GetInt32(), string.Join(",", p.GetProperty("officers").EnumerateArray().Select(i => i.GetInt32())))).ToArray()
                : before;
            Assert.Equal(expected, w.CmdActions.Select(p => (p.Kind, string.Join(",", p.Officers))));
            Assert.Equal(Convert.FromHexString(c.GetProperty("data").GetString()!), selected.Data.Bytes);
            Assert.Equal(snapshot.GetProperty("command_storage").GetRawText(), c.GetProperty("store").GetRawText());
            Assert.Equal(snapshot.GetProperty("command_storage").GetProperty("free_head").GetUInt16(), storage.FreeHead);
            Assert.Equal(snapshot.GetProperty("command_storage").GetProperty("free_ids").EnumerateArray().Select(p => p.GetUInt16()), storage.FreeIds);
            Assert.Equal(active, storage.Active.Select(p => p.Data.NativeAddress));
            Assert.Equal(pending, storage.Pending.Select(p => p.Data.NativeAddress));
            Assert.Equal(mapping, storage.ById.Select(p => (p.Key, p.Value.Data.NativeAddress)).Order());
            Assert.Equal(state, w.WorldState.Bytes);
            Assert.Equal(settings, w.Settings.Bytes);
            Assert.Empty(delta.GetProperty("records").EnumerateArray());
            foreach (var table in records)
                for (int i = 0; i < table.Value.Length; i++) Assert.Equal(table.Value[i], w.Table(Convert.ToInt32(table.Key[2..], 16))[i].Bytes);
            foreach (var section in delta.EnumerateObject()) Assert.Contains(section.Name, new[] { "records", "cmdactions" });
        }
    }
}

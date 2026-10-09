using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class OrphanItemTests
{
    private static ulong Address(string s) => Convert.ToUInt64(s[2..], 16);

    private static void Store(CommandStorage actual, JsonElement expected)
    {
        Assert.Equal(expected.GetProperty("free_head").GetUInt16(), actual.FreeHead);
        Assert.Equal(expected.GetProperty("free_ids").EnumerateArray().Select(e => e.GetUInt16()), actual.FreeIds);
        Assert.Equal(expected.GetProperty("by_id").EnumerateObject().Select(e => (int.Parse(e.Name), Address(e.Value.GetString()!))).Order(),
            actual.ById.Select(e => (e.Key, e.Value.Data.NativeAddress)).Order());
        Assert.Equal(expected.GetProperty("active").EnumerateArray().Select(e => Address(e.GetString()!)), actual.Active.Select(e => e.Data.NativeAddress));
        Assert.Equal(expected.GetProperty("pending").EnumerateArray().Select(e => Address(e.GetString()!)), actual.Pending.Select(e => e.Data.NativeAddress));
    }

    private static Dictionary<string, byte[][]> Records(World w, JsonElement snapshot) => snapshot.GetProperty("tables")
        .EnumerateObject().ToDictionary(t => t.Name, t => w.Table(Convert.ToInt32(t.Name[2..], 16)).Select(r => r.Bytes.ToArray()).ToArray());

    private static void State(World w, JsonElement snapshot, JsonElement delta, Dictionary<string, byte[][]> records, string name)
    {
        foreach (var r in delta.GetProperty("records").EnumerateArray()) records[r[0].GetString()!][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
        foreach (var table in records)
            for (int i = 0; i < table.Value.Length; i++)
                Assert.True(table.Value[i].SequenceEqual(w.Table(Convert.ToInt32(table.Key[2..], 16))[i].Bytes), $"{name}: {table.Key}[{i}] differs");
        foreach (var l in snapshot.GetProperty("lists").EnumerateObject())
        {
            var expected = delta.TryGetProperty("lists", out var changed) && changed.TryGetProperty(l.Name, out var value) ? value : l.Value.GetProperty("items");
            var actual = w.List(Convert.ToInt32(l.Name[2..], 16));
            Assert.Equal(expected.GetArrayLength(), actual.Count);
            for (int i = 0; i < actual.Count; i++)
                Assert.Equal(expected[i].EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Null ? null :
                    w.Table(Convert.ToInt32(r[0].GetString()![2..], 16))[r[1].GetInt32()]), actual[i]);
        }
        var actions = delta.TryGetProperty("cmdactions", out var newActions) ? newActions : snapshot.GetProperty("cmdactions");
        Assert.Equal(actions.EnumerateArray().Select(p => (p.GetProperty("kind").GetInt32(), string.Join(",", p.GetProperty("officers").EnumerateArray().Select(x => x.GetInt32())))),
            w.CmdActions.Select(p => (p.Kind, string.Join(",", p.Officers))));
        foreach (var section in delta.EnumerateObject()) Assert.Contains(section.Name, new[] { "records", "lists", "cmdactions" });
    }

    private static CommandCancellation.Command Fixture(World w, JsonElement snapshot, JsonElement fixture, out CommandCancellation.Command[] commands)
    {
        commands = ReturnCommandTests.Commands(w, snapshot);
        var command = commands[fixture.GetProperty("command").GetInt32()];
        foreach (var p in fixture.GetProperty("command_pokes").EnumerateArray()) Convert.FromHexString(p[1].GetString()!).CopyTo(command.Data.Bytes, p[0].GetInt32());
        Vectors.Poke(w, fixture.GetProperty("pokes"));
        return command;
    }

    [Fact]
    public void CompleteOrphanRefundAndRemovalMatchNativeCancellations()
    {
        var root = Vectors.Read("orphan_item.json.gz");
        var snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            var f = c.GetProperty("fixture");
            var command = Fixture(w, snapshot, f, out var commands);
            var before = Records(w, snapshot);
            byte[] worldBefore = w.WorldState.Bytes.ToArray(), settingsBefore = w.Settings.Bytes.ToArray();
            int detach = 0, dispose = 0, allocations = 0;
            var storage = ReturnCommandTests.Storage(w, snapshot, commands, source => { Assert.Same(command, source); detach++; },
                source => { Assert.Same(command, source); dispose++; });
            Rec Allocate()
            {
                var input = c.GetProperty("allocations")[allocations++];
                return new(-1, Convert.FromHexString(input.GetProperty("data").GetString()!),
                    nativeAddress: Address(input.GetProperty("address").GetString()!));
            }
            var result = ReturnCommand.Cancel(w, storage, command, Allocate,
                _ => throw new InvalidOperationException("No player refresh is expected."));
            Assert.Equal(c.GetProperty("allocations").GetArrayLength(), allocations);
            if (c.TryGetProperty("new_command", out var native))
            {
                Assert.NotNull(result);
                Assert.Equal(Address(native.GetProperty("address").GetString()!), result.Data.NativeAddress);
                byte[] expected = Convert.FromHexString(native.GetProperty("data").GetString()!);
                var physical = root.GetProperty("physical_fields").EnumerateArray().Select(p => (Start: p[0].GetInt32(), Length: p[1].GetInt32())).ToArray();
                for (int at = 0; at < expected.Length; at++)
                    if (!physical.Any(p => at >= p.Start && at < p.Start + p.Length))
                        Assert.True(expected[at] == result.Data.Bytes[at], $"orphan return +{at:X}: expected {expected[at]:X2}, got {result.Data.Bytes[at]:X2}");
                var descriptors = native.GetProperty("descriptors").EnumerateArray().ToArray();
                Assert.Equal(descriptors.Length, result.Destinations!.Count);
                for (int i = 0; i < descriptors.Length; i++)
                {
                    Rec d = new(0, Convert.FromHexString(descriptors[i].GetString()!));
                    Assert.Equal(BitConverter.ToUInt64(d.Bytes, 0), result.Destinations[i].Primary.NativeAddress);
                    Assert.Equal(d.I32(8), result.Destinations[i].PrimaryKind);
                    Assert.Equal(d.I32(12), result.Destinations[i].SecondaryKind);
                    Assert.Equal(BitConverter.ToUInt64(d.Bytes, 16), result.Destinations[i].Secondary.NativeAddress);
                }
            }
            else Assert.Null(result);
            Assert.Equal(1, detach);
            Assert.Equal(1, dispose);
            Assert.Equal(Convert.FromHexString(c.GetProperty("removal_data").GetString()!), command.Data.Bytes);
            Store(storage, c.GetProperty("store"));
            State(w, snapshot, c.GetProperty("after"), before, f.GetProperty("name").GetString()!);
            Assert.Equal(worldBefore, w.WorldState.Bytes);
            Assert.Equal(settingsBefore, w.Settings.Bytes);
            Assert.DoesNotContain(c.GetProperty("calls").EnumerateArray(), e => e.GetString() == "0x24a9c0");
        }
        Assert.Equal(11, root.GetProperty("cases").GetArrayLength());
    }

    [Fact]
    public void EmptyHomeCountyListPreservesTheVerifiedNativeFailure()
    {
        var root = Vectors.Read("orphan_item.json.gz");
        var snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        var c = Assert.Single(root.GetProperty("bugs").EnumerateArray());
        Assert.Equal("0x0", c.GetProperty("fault_address").GetString());
        Assert.Equal("0x1f1ebe", c.GetProperty("fault_rva").GetString());
        Assert.Contains(c.GetProperty("calls").EnumerateArray(), e => e.GetString() == "0x24a9c0");
        Assert.DoesNotContain(c.GetProperty("calls").EnumerateArray(), e => e.GetString() is "0x323ed0" or "0x224670");
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        var command = Fixture(w, snapshot, c.GetProperty("fixture"), out var commands);
        var before = Records(w, snapshot);
        byte[] commandBefore = command.Data.Bytes.ToArray(), worldBefore = w.WorldState.Bytes.ToArray();
        var storage = ReturnCommandTests.Storage(w, snapshot, commands,
            _ => throw new InvalidOperationException("The native failure precedes removal."),
            _ => throw new InvalidOperationException("The native failure precedes destruction."));
        var error = Assert.Throws<InvalidOperationException>(() => ReturnCommand.Cancel(w, storage, command,
            () => throw new InvalidOperationException("The native failure precedes allocation."), _ => { }));
        Assert.Contains("null county head", error.Message);
        State(w, snapshot, c.GetProperty("before_failure"), before, "empty_home_counties");
        Store(storage, snapshot.GetProperty("command_storage"));
        Assert.Equal(commandBefore, command.Data.Bytes);
        Assert.Equal(worldBefore, w.WorldState.Bytes);
    }
}

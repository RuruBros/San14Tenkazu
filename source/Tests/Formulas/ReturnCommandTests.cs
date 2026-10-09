using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ReturnCommandTests
{
    private static ulong Address(string text) => Convert.ToUInt64(text[2..], 16);
    private static int Handler(JsonElement handlers, string slot) => Convert.ToInt32(handlers.GetProperty(slot).GetString()![2..], 16);

    internal static CommandCancellation.Command[] Commands(World w, JsonElement snapshot) => snapshot.GetProperty("commands").EnumerateArray().Select((c, i) =>
    {
        var h = c.GetProperty("handlers");
        return new CommandCancellation.Command(c.GetProperty("kind").GetInt32(),
            new Rec(i, Convert.FromHexString(c.GetProperty("data").GetString()!), nativeAddress: Address(c.GetProperty("address").GetString()!)),
            c.GetProperty("officers").EnumerateArray().Select(p => p.GetInt32()).ToArray(),
            Handler(h, "0x68"), Handler(h, "0x90"), Handler(h, "0xf0"), Handler(h, "0x110"),
            new(Handler(h, "0xd8"), Handler(h, "0x118"), Handler(h, "0xa8"), Handler(h, "0xb0"), h.TryGetProperty("0xc8", out _) ? Handler(h, "0xc8") : 0),
            c.TryGetProperty("destinations", out var ds) ? ds.EnumerateArray().Select(d => new CommandTravel.Destination(
                w.Table(Convert.ToInt32(d.GetProperty("primary")[0].GetString()![2..], 16))[d.GetProperty("primary")[1].GetInt32()], d.GetProperty("primary_kind").GetInt32(),
                w.Table(Convert.ToInt32(d.GetProperty("secondary")[0].GetString()![2..], 16))[d.GetProperty("secondary")[1].GetInt32()], d.GetProperty("secondary_kind").GetInt32())).ToArray() : null);
    }).ToArray();

    internal static CommandStorage Storage(World w, JsonElement snapshot, CommandCancellation.Command[] commands,
        Action<CommandCancellation.Command>? detach = null, Action<CommandCancellation.Command>? dispose = null)
    {
        var p = commands.ToDictionary(c => c.Data.NativeAddress);
        var s = snapshot.GetProperty("command_storage");
        return new(w, s.GetProperty("by_id").EnumerateObject().ToDictionary(e => int.Parse(e.Name), e => p[Address(e.Value.GetString()!)]),
            s.GetProperty("active").EnumerateArray().Select(e => p[Address(e.GetString()!)]).ToList(),
            s.GetProperty("pending").EnumerateArray().Select(e => p[Address(e.GetString()!)]).ToList(),
            s.GetProperty("free_ids").EnumerateArray().Select(e => e.GetUInt16()).ToArray(), s.GetProperty("free_head").GetUInt16(),
            detach ?? (_ => { }), dispose ?? (_ => { }));
    }

    [Fact]
    public void CompleteCancellationAndNewReturnFieldsMatchNativeBodies()
    {
        var root = Vectors.Read("return_command.json.gz");
        var snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        var h = snapshot.GetProperty("return_handlers");
        Assert.Equal(new CommandTravel.Dispatch(Handler(h, "0xd8"), Handler(h, "0x118"), Handler(h, "0xa8"), Handler(h, "0xb0"), Handler(h, "0xc8")),
            CommandTravel.ReturnDispatch);
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            var f = c.GetProperty("fixture");
            var commands = Commands(w, snapshot);
            int index = f.GetProperty("command").GetInt32();
            if (f.TryGetProperty("officer_ids", out var ids)) commands[index] = commands[index] with { Officers = ids.EnumerateArray().Select(p => p.GetInt32()).ToArray() };
            var source = commands[index];
            foreach (var p in f.GetProperty("command_pokes").EnumerateArray()) Convert.FromHexString(p[1].GetString()!).CopyTo(source.Data.Bytes, p[0].GetInt32());
            Vectors.Poke(w, f.GetProperty("pokes"));
            var records = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(t => t.Name, t =>
                w.Table(Convert.ToInt32(t.Name[2..], 16)).Select(r => r.Bytes.ToArray()).ToArray());
            int detached = 0, disposed = 0, refreshed = 0, allocations = 0;
            var storage = Storage(w, snapshot, commands, command => { Assert.Same(source, command); detached++; },
                command => { Assert.Same(source, command); disposed++; });
            storage.Refresh();
            Rec Allocate()
            {
                var a = c.GetProperty("allocations")[allocations++];
                return new(-1, Convert.FromHexString(a.GetProperty("data").GetString()!), nativeAddress: Address(a.GetProperty("address").GetString()!));
            }
            var result = ReturnCommand.Cancel(w, storage, source, Allocate, _ => refreshed++);
            Assert.Equal(c.GetProperty("allocations").GetArrayLength(), allocations);
            Assert.Equal(1, detached);
            Assert.Equal(1, disposed);
            Assert.Equal(0, refreshed);
            if (c.TryGetProperty("new_command", out var native))
            {
                Assert.NotNull(result);
                Assert.Equal(Address(native.GetProperty("address").GetString()!), result.Data.NativeAddress);
                byte[] expected = Convert.FromHexString(native.GetProperty("data").GetString()!);
                var physical = root.GetProperty("physical_fields").EnumerateArray().Select(p => (Start: p[0].GetInt32(), Length: p[1].GetInt32())).ToArray();
                for (int at = 0; at < expected.Length; at++)
                    if (!physical.Any(p => at >= p.Start && at < p.Start + p.Length))
                        Assert.True(expected[at] == result.Data.Bytes[at], $"{f.GetProperty("name").GetString()}: command +{at:X} expected {expected[at]:X2}, got {result.Data.Bytes[at]:X2}");
                var descriptors = native.GetProperty("descriptors").EnumerateArray().ToArray();
                Assert.Equal(descriptors.Length, result.Destinations!.Count);
                for (int i = 0; i < descriptors.Length; i++)
                {
                    Rec descriptor = new(0, Convert.FromHexString(descriptors[i].GetString()!));
                    Assert.Equal(BitConverter.ToUInt64(descriptor.Bytes, 0), result.Destinations[i].Primary.NativeAddress);
                    Assert.Equal(descriptor.I32(8), result.Destinations[i].PrimaryKind);
                    Assert.Equal(descriptor.I32(12), result.Destinations[i].SecondaryKind);
                    Assert.Equal(BitConverter.ToUInt64(descriptor.Bytes, 16), result.Destinations[i].Secondary.NativeAddress);
                }
            }
            else Assert.Null(result);
            var expectedStore = c.GetProperty("store");
            Assert.Equal(expectedStore.GetProperty("free_head").GetUInt16(), storage.FreeHead);
            Assert.Equal(expectedStore.GetProperty("free_ids").EnumerateArray().Select(p => p.GetUInt16()), storage.FreeIds);
            Assert.Equal(expectedStore.GetProperty("by_id").EnumerateObject().Select(p => (int.Parse(p.Name), Address(p.Value.GetString()!))).Order(), storage.ById.Select(p => (p.Key, p.Value.Data.NativeAddress)).Order());
            Assert.Equal(expectedStore.GetProperty("active").EnumerateArray().Select(p => Address(p.GetString()!)), storage.Active.Select(p => p.Data.NativeAddress));
            Assert.Equal(expectedStore.GetProperty("pending").EnumerateArray().Select(p => Address(p.GetString()!)), storage.Pending.Select(p => p.Data.NativeAddress));
            var delta = c.GetProperty("delta");
            foreach (var r in delta.GetProperty("records").EnumerateArray()) records[r[0].GetString()!][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
            foreach (var table in records)
                for (int i = 0; i < table.Value.Length; i++)
                    Assert.True(table.Value[i].SequenceEqual(w.Table(Convert.ToInt32(table.Key[2..], 16))[i].Bytes), $"{f.GetProperty("name").GetString()}: {table.Key}[{i}]");
            Assert.Equal(delta.GetProperty("cmdactions").EnumerateArray().Select(p => (p.GetProperty("kind").GetInt32(), string.Join(",", p.GetProperty("officers").EnumerateArray().Select(x => x.GetInt32())))),
                w.CmdActions.Select(p => (p.Kind, string.Join(",", p.Officers))));
            foreach (var section in delta.EnumerateObject()) Assert.Contains(section.Name, new[] { "records", "cmdactions" });
        }
    }
}

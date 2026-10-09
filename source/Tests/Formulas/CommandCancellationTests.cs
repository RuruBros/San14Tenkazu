using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class CommandCancellationTests
{
    private static CommandCancellation.Command LoadCommand(JsonElement snapshot, int index)
    {
        JsonElement c = snapshot.GetProperty("commands")[index], handlers = c.GetProperty("handlers");
        int Handler(string slot) => Convert.ToInt32(handlers.GetProperty(slot).GetString()![2..], 16);
        return new(c.GetProperty("kind").GetInt32(), new Rec(index,
            Convert.FromHexString(c.GetProperty("data").GetString()!), nativeAddress: Convert.ToUInt64(c.GetProperty("address").GetString()![2..], 16)),
            c.GetProperty("officers").EnumerateArray().Select(p => p.GetInt32()).ToArray(),
            Handler("0x68"), Handler("0x90"), Handler("0xf0"), Handler("0x110"));
    }

    private static void PokeCommand(CommandCancellation.Command command, JsonElement pokes)
    {
        foreach (JsonElement p in pokes.EnumerateArray())
            Convert.FromHexString(p[1].GetString()!).CopyTo(command.Data.Bytes, p[0].GetInt32());
    }

    private static Dictionary<string, byte[][]> Records(World w, JsonElement snapshot) => snapshot.GetProperty("tables")
        .EnumerateObject().ToDictionary(t => t.Name, t => w.Table(Convert.ToInt32(t.Name[2..], 16)).Select(r => r.Bytes.ToArray()).ToArray());

    private static void CompareRecords(World w, Dictionary<string, byte[][]> before, JsonElement delta, string name)
    {
        var expected = before.ToDictionary(p => p.Key, p => p.Value.ToArray());
        foreach (JsonElement r in delta.GetProperty("records").EnumerateArray())
            expected[r[0].GetString()!][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
        foreach ((string table, byte[][] records) in expected)
            for (int i = 0; i < records.Length; i++)
                Assert.True(records[i].SequenceEqual(w.Table(Convert.ToInt32(table[2..], 16))[i].Bytes), $"{name}: {table}[{i}] differs");
    }

    private static void CompareLists(World w, JsonElement snapshot, JsonElement delta)
    {
        foreach (JsonProperty list in snapshot.GetProperty("lists").EnumerateObject())
        {
            JsonElement items = delta.TryGetProperty("lists", out JsonElement changed) && changed.TryGetProperty(list.Name, out JsonElement data)
                ? data : list.Value.GetProperty("items");
            List<Rec?[]> actual = w.List(Convert.ToInt32(list.Name[2..], 16));
            Assert.Equal(items.GetArrayLength(), actual.Count);
            for (int i = 0; i < actual.Count; i++)
                Assert.Equal(items[i].EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Null ? null :
                    w.Table(Convert.ToInt32(r[0].GetString()![2..], 16))[r[1].GetInt32()]), actual[i]);
        }
    }

    [Fact]
    public void RefundsReturnRequestsAndRemovalMarkingMatchNativeBoundaries()
    {
        JsonElement root = Vectors.Read("command_cancel.json.gz"), snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        var handlers = new HashSet<int>();
        int cases = 0, serving = 0, refunds = 0, skippedRemoval = 0, repeated = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            JsonElement f = c.GetProperty("fixture"), prefix = c.GetProperty("prefix"), after = c.GetProperty("after");
            string name = f.GetProperty("name").GetString()!;
            CommandCancellation.Command command = LoadCommand(snapshot, f.GetProperty("command").GetInt32());
            if (f.TryGetProperty("officer_ids", out JsonElement ids))
                command = command with { Officers = ids.EnumerateArray().Select(p => p.GetInt32()).ToArray() };
            PokeCommand(command, f.GetProperty("command_pokes"));
            Vectors.Poke(w, f.GetProperty("pokes"));
            var before = Records(w, snapshot);
            byte[] worldBefore = w.WorldState.Bytes.ToArray(), settingsBefore = w.Settings.Bytes.ToArray(), commandBefore = command.Data.Bytes.ToArray();
            CommandCancellation.ReturnRequest request = CommandCancellation.RefundAndRequestReturn(w, command);
            CompareRecords(w, before, prefix, name);
            CompareLists(w, snapshot, prefix);
            foreach (JsonProperty section in prefix.EnumerateObject()) Assert.Contains(section.Name, new[] { "records", "lists" });
            Assert.Equal(worldBefore, w.WorldState.Bytes);
            Assert.Equal(settingsBefore, w.Settings.Bytes);
            Assert.Equal(commandBefore, command.Data.Bytes);
            JsonElement expected = Assert.Single(c.GetProperty("return_requests").EnumerateArray());
            Assert.Same(command, request.Source);
            Assert.Equal(command.Data.NativeAddress, Convert.ToUInt64(expected.GetProperty("command").GetString()![2..], 16));
            Assert.Equal(expected.GetProperty("notify").GetBoolean(), request.Notify);
            Assert.Equal(expected.GetProperty("officers").EnumerateArray().Select(r => w.Table(Convert.ToInt32(r[0].GetString()![2..], 16))[r[1].GetInt32()]), request.Officers);
            string[] events = c.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("rva").GetString()!).ToArray();
            Assert.True(Array.IndexOf(events, "0x224670") < Array.IndexOf(events, "0x2289d0"));
            if (name == "ordered_repeated_return")
            {
                Assert.Equal(5, request.Officers.Count);
                Assert.Same(request.Officers[0], request.Officers[^1]);
                Assert.Equal(new byte[] { 1, 2, 3, 4, 1 }, request.Officers.Select(p => p.U8(0xE6)));
                Assert.True(request.Notify);
                repeated++;
            }
            CommandCancellation.RemovalRequest? removal = CommandCancellation.RequestRemoval(command);
            JsonElement[] nativeRemoval = c.GetProperty("removal").EnumerateArray().ToArray();
            if (nativeRemoval.Length == 0)
            {
                Assert.Null(removal);
                Assert.Equal(commandBefore, command.Data.Bytes);
                skippedRemoval++;
            }
            else
            {
                JsonElement native = Assert.Single(nativeRemoval);
                Assert.True(removal.HasValue);
                Assert.Same(command, removal.Value.Source);
                Assert.True(removal.Value.Immediate);
                Assert.Equal(1, native.GetProperty("immediate").GetInt32());
                Assert.Equal(Convert.FromHexString(native.GetProperty("data").GetString()!), command.Data.Bytes);
            }
            if (request.Officers.Count == 0)
            {
                // No return constructor runs here; all native gameplay records and lists must still match.
                CompareRecords(w, before, after, name);
                CompareLists(w, snapshot, after);
                foreach (JsonProperty section in after.EnumerateObject()) Assert.Contains(section.Name, new[] { "records", "lists", "cmdactions" });
                if (nativeRemoval.Length != 0)
                {
                    Assert.Equal(snapshot.GetProperty("commands").EnumerateArray()
                        .Where(p => p.GetProperty("address").GetString() != expected.GetProperty("command").GetString())
                        .Select(p => (p.GetProperty("kind").GetInt32(), string.Join(",", p.GetProperty("officers").EnumerateArray().Select(x => x.GetInt32())))),
                        after.GetProperty("cmdactions").EnumerateArray().Select(p => (p.GetProperty("kind").GetInt32(),
                            string.Join(",", p.GetProperty("officers").EnumerateArray().Select(x => x.GetInt32())))));
                }
            }
            else
            {
                Assert.Contains(c.GetProperty("events").EnumerateArray(), e => e.GetProperty("rva").GetString() == "0x1f6720");
                serving++;
            }
            handlers.Add(command.CancelHandler);
            if (prefix.GetProperty("records").GetArrayLength() != 0) refunds++;
            cases++;
        }
        Assert.Equal(root.GetProperty("cases").GetArrayLength(), cases);
        Assert.Equal(new[] { 0x1F1B30, 0x1F1F70, 0x1F1F80, 0x1F1FC0 }, handlers.Order());
        Assert.True(serving > 0);
        Assert.True(refunds >= 3);
        Assert.Equal(2, skippedRemoval);
        Assert.Equal(1, repeated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnsupportedDispatchAndMissingLocationFailBeforeRefunds(int branch)
    {
        JsonElement root = Vectors.Read("command_cancel.json.gz"), snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        JsonElement fixture = root.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("fixture").GetProperty("name").GetString() == "diplomacy_refund").GetProperty("fixture");
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        CommandCancellation.Command command = LoadCommand(snapshot, fixture.GetProperty("command").GetInt32());
        PokeCommand(command, fixture.GetProperty("command_pokes"));
        Vectors.Poke(w, fixture.GetProperty("pokes"));
        if (branch == 0) command = command with { PhaseGetter = 0 };
        if (branch == 1) command = command with { CancelHandler = 0 };
        if (branch == 2) command.Data.Bytes[0x75] = 0;
        var before = Records(w, snapshot);
        byte[] commandBefore = command.Data.Bytes.ToArray();
        Assert.Throws<NotSupportedException>(() => CommandCancellation.RefundAndRequestReturn(w, command));
        foreach ((string table, byte[][] records) in before)
            for (int i = 0; i < records.Length; i++) Assert.Equal(records[i], w.Table(Convert.ToInt32(table[2..], 16))[i].Bytes);
        Assert.Equal(commandBefore, command.Data.Bytes);
        CompareLists(w, snapshot, JsonDocument.Parse("{}").RootElement);
    }
}

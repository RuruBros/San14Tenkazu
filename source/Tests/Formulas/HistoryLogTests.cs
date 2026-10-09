using System.Buffers.Binary;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class HistoryLogTests
{
    private static string Utf16(string hex)
    {
        byte[] data = Convert.FromHexString(hex);
        return new string(Enumerable.Range(0, data.Length / 2)
            .Select(i => (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2))).ToArray());
    }

    [Fact]
    public void WholeNativeHistoryMatchesClampsFixedFieldTailsCounterAndRefresh()
    {
        JsonElement root = Vectors.Read("history_log.json.gz");
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.False(root.GetProperty("allocator_stubs").GetBoolean());
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        int cases = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture"), delta = c.GetProperty("after");
            World w = Vectors.LoadWorld(world);
            Vectors.Poke(w, f.GetProperty("pokes"));
            Assert.All(delta.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "records", "worldstate" }));
            var expected = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
            foreach (JsonElement r in delta.GetProperty("records").EnumerateArray())
                expected[Convert.ToInt32(r[0].GetString()!, 16)][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
            byte[] expectedState = delta.TryGetProperty("worldstate", out var state) ? Convert.FromHexString(state.GetString()!) : w.WorldState.Bytes.ToArray();
            byte[] settings = w.Settings.Bytes.ToArray();
            var lists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.List(Convert.ToInt32(p.Name, 16)).ToArray());
            var flags = snapshot.GetProperty("flags").EnumerateObject().ToDictionary(p => p.Name, p => w.Value(p.Name));
            Rec?[] records = w.Table(Tables.History).Cast<Rec?>().ToArray();
            if (f.GetProperty("null_record").GetBoolean()) records[c.GetProperty("index").GetInt32()] = null;
            Queue<int> refreshes = new(c.GetProperty("refreshes").EnumerateArray().Select(e => e.GetInt32()));
            var log = new HistoryLog(w, records, f.GetProperty("suppressed").GetBoolean(), refreshes.Count != 0,
                _ => Utf16(c.GetProperty("force_initial").GetString()!), record =>
                {
                    Assert.NotEmpty(refreshes);
                    Assert.Equal(refreshes.Dequeue(), record.Index);
                    Assert.Equal(expectedState, w.WorldState.Bytes);
                    Assert.Equal(expected[Tables.History][record.Index], record.Bytes);
                });
            int Value(string key) => f.GetProperty(key).GetInt32();
            Rec? result = log.Add(new(Value("kind"), Value("corps"), Value("hex"), Value("player"), Value("tick"),
                f.GetProperty("primary").GetString()!, f.GetProperty("secondary").GetString()!, Value("officer"),
                Value("immediate"), Value("flag4"), Value("flag5"), Value("flag6")));
            JsonElement returned = c.GetProperty("returned");
            Assert.Equal(returned.ValueKind == JsonValueKind.Null ? (int?)null : returned.GetInt32(), result?.Index);
            Assert.Empty(refreshes);
            foreach (var (table, rows) in expected)
                for (int i = 0; i < rows.Length; i++)
                    Assert.True(rows[i].AsSpan().SequenceEqual(w.Table(table)[i].Bytes), $"{f.GetProperty("name").GetString()}: {table:X}[{i}]");
            Assert.Equal(expectedState, w.WorldState.Bytes);
            Assert.Equal(settings, w.Settings.Bytes);
            foreach (var (head, items) in lists) Assert.Equal(items, w.List(head));
            foreach (var (key, value) in flags) Assert.Equal(value, w.Value(key));
            cases++;
        }
        Assert.Equal(21, cases);
    }
}

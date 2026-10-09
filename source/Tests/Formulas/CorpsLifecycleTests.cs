using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class CorpsLifecycleTests
{
    [Fact]
    public void MergeAndDissolutionMatchEveryNativeRecordListAndCache()
    {
        JsonElement root = Vectors.Read("corps_merge.json.gz");
        JsonElement snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        int cases = 0, cautions = 0, orderResets = 0, listChanges = 0, gateSelections = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            JsonElement f = c.GetProperty("fixture"), after = c.GetProperty("after");
            Rec? Ref(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null :
                w.Table(Convert.ToInt32(e[0].GetString()![2..], 16))[e[1].GetInt32()];
            Rec Pointer(string p)
            {
                ulong address = Convert.ToUInt64(p[2..], 16);
                return snapshot.GetProperty("tables").EnumerateObject()
                    .SelectMany(t => w.Table(Convert.ToInt32(t.Name[2..], 16)))
                    .Single(r => r.NativeAddress == address);
            }
            var cache = snapshot.GetProperty("corps_cache").EnumerateObject().ToDictionary(
                p => Convert.ToUInt64(p.Name[2..], 16), p => Convert.ToUInt64(p.Value.GetString()![2..], 16));
            var members = snapshot.GetProperty("nested").EnumerateObject().Where(p => p.Name.StartsWith("caution:"))
                .ToDictionary(p => int.Parse(p.Name[8..]), p => p.Value.EnumerateArray().Select(e => (Rec?)Pointer(e.GetString()!)).ToList());
            Vectors.Poke(w, f.GetProperty("pokes"));
            var expectedRecords = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(t => t.Name,
                t => w.Table(Convert.ToInt32(t.Name[2..], 16)).Select(r => r.Bytes.ToArray()).ToArray());
            var expectedLists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("items"));
            byte[] worldBefore = w.WorldState.Bytes.ToArray();
            int result = new CorpsLifecycle(w, cache, members, () => root.GetProperty("pool_enabled").GetBoolean())
                .Merge(Ref(f.GetProperty("source"))!, Ref(f.GetProperty("destination")), f.GetProperty("mode").GetInt32());
            Assert.Equal(c.GetProperty("result").GetInt32(), result);
            foreach (JsonElement r in after.GetProperty("records").EnumerateArray())
            {
                expectedRecords[r[0].GetString()!][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
                if (r[0].GetString() == "0x80ba8") cautions++;
            }
            foreach ((string table, byte[][] records) in expectedRecords)
                for (int i = 0; i < records.Length; i++)
                {
                    byte[] actual = w.Table(Convert.ToInt32(table[2..], 16))[i].Bytes;
                    Assert.True(records[i].SequenceEqual(actual), $"{f.GetProperty("name").GetString()}: {table}[{i}] differs at " +
                        string.Join(",", records[i].Select((b, at) => (b, at)).Where(x => x.b != actual[x.at]).Select(x => $"{x.at:X}")));
                }
            Assert.Equal(worldBefore, w.WorldState.Bytes);
            foreach (JsonProperty section in after.EnumerateObject())
                Assert.Contains(section.Name, new[] { "records", "lists", "nested", "corps_cache" });
            if (after.TryGetProperty("lists", out JsonElement lists))
            {
                foreach (JsonProperty l in lists.EnumerateObject()) expectedLists[l.Name] = l.Value;
                listChanges++;
            }
            foreach ((string head, JsonElement list) in expectedLists)
            {
                Rec?[][] expected = list.EnumerateArray().Select(n => n.EnumerateArray().Select(Ref).ToArray()).ToArray();
                List<Rec?[]> actual = w.List(Convert.ToInt32(head[2..], 16));
                Assert.Equal(expected.Length, actual.Count);
                for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i]);
            }
            JsonElement nested = after.TryGetProperty("nested", out JsonElement changed) ? changed : snapshot.GetProperty("nested");
            foreach (JsonProperty p in nested.EnumerateObject())
                if (p.Name.StartsWith("caution:"))
                    Assert.Equal(p.Value.EnumerateArray().Select(e => (Rec?)Pointer(e.GetString()!)), members[int.Parse(p.Name[8..])]);
                else Assert.Equal(snapshot.GetProperty("nested").GetProperty(p.Name).GetRawText(), p.Value.GetRawText());
            JsonElement expectedCache = after.TryGetProperty("corps_cache", out JsonElement map) ? map : snapshot.GetProperty("corps_cache");
            Assert.Equal(expectedCache.EnumerateObject().Count(), cache.Count);
            foreach (JsonProperty p in expectedCache.EnumerateObject())
                Assert.Equal(Convert.ToUInt64(p.Value.GetString()![2..], 16), cache[Convert.ToUInt64(p.Name[2..], 16)]);
            orderResets += c.GetProperty("events").EnumerateArray().Count(e => e.GetProperty("rva").GetString() == "0x2edf20");
            gateSelections += c.GetProperty("events").EnumerateArray().Count(e => e.GetProperty("rva").GetString() == "0x2827c0" &&
                e.GetProperty("args")[0].ValueKind == JsonValueKind.Array && e.GetProperty("args")[0][0].GetString() == "0xded8");
            cases++;
        }
        Assert.Equal(root.GetProperty("cases").GetArrayLength(), cases);
        Assert.True(cautions > 0);
        Assert.True(orderResets > 0);
        Assert.True(listChanges > 0);
        Assert.True(gateSelections > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnsupportedBranchesFailBeforeMutation(int branch)
    {
        JsonElement root = Vectors.Read("corps_merge.json.gz"), snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        w.WorldState.Bytes[0x3A] = branch == 1 ? (byte)9 : (byte)0;
        Rec source = w.Table(Tables.Corps)[branch == 0 ? 9 : 3], destination = w.Table(Tables.Corps)[1];
        var before = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(t => t.Name,
            t => w.Table(Convert.ToInt32(t.Name[2..], 16)).Select(r => r.Bytes.ToArray()).ToArray());
        var cache = new Dictionary<ulong, ulong> { [source.NativeAddress] = 123 };
        var members = new Dictionary<int, List<Rec?>>();
        Assert.Throws<NotSupportedException>(() => new CorpsLifecycle(w, cache, members, () => branch != 2).Merge(source, destination, 0));
        foreach ((string table, byte[][] records) in before)
            for (int i = 0; i < records.Length; i++) Assert.Equal(records[i], w.Table(Convert.ToInt32(table[2..], 16))[i].Bytes);
        Assert.Equal(123UL, cache[source.NativeAddress]);
        Assert.Empty(members);
    }
}

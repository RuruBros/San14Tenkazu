using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ForceOwnedStorageTests
{
    private static ulong Pointer(string value) => Convert.ToUInt64(value[2..], 16);

    [Fact]
    public void OwnedStorageResetMatchesCompleteNativeBodyAndReleasePhase()
    {
        JsonElement root = Vectors.Read("force_owned_storage.json.gz");
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.False(root.GetProperty("allocator_stubs").GetBoolean());
        Assert.Equal(32, root.GetProperty("allocation_size").GetInt32());
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        int cases = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(world);
            JsonElement f = c.GetProperty("fixture"), after = c.GetProperty("after");
            Vectors.Poke(w, f.GetProperty("pokes"));
            var expected = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
            foreach (JsonElement r in after.GetProperty("records").EnumerateArray())
                expected[Convert.ToInt32(r[0].GetString()!, 16)][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
            var nested = snapshot.GetProperty("nested").EnumerateObject().ToDictionary(p => p.Name,
                p => p.Value.EnumerateArray().Select(e => e.GetString()!).ToList());
            byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
            var lists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.List(Convert.ToInt32(p.Name, 16)).ToArray());
            var flags = snapshot.GetProperty("flags").EnumerateObject().ToDictionary(p => p.Name, p => w.Value(p.Name));
            Rec force = w.Table(Tables.Forces)[f.GetProperty("target").GetInt32()];
            var released = new List<ulong>();
            var cleared = new List<int>();
            ForceReset.Apply(w, force, f.GetProperty("enabled").GetBoolean(), at =>
            {
                Assert.Single(released);
                Assert.Equal(0UL, BitConverter.ToUInt64(force.Bytes, 0x1A0));
                Assert.Equal(0UL, BitConverter.ToUInt64(force.Bytes, 0x1A8));
                cleared.Add(at);
                nested[$"force:{force.Index}:{at:x}"].Clear();
            }, _ => Assert.Fail("These cases have no player city refresh"), pointer =>
            {
                JsonElement release = c.GetProperty("frees")[released.Count];
                Assert.Equal(Pointer(release.GetProperty("pointer").GetString()!), pointer);
                Assert.Equal(Convert.FromHexString(release.GetProperty("record").GetString()!), force.Bytes);
                released.Add(pointer);
            });
            Assert.Equal(c.GetProperty("frees").EnumerateArray().Select(e => Pointer(e.GetProperty("pointer").GetString()!)), released);
            Assert.Equal(c.GetProperty("clear_calls").EnumerateArray().Select(e => e.GetInt32()), cleared);
            foreach (var (table, records) in expected)
                for (int i = 0; i < records.Length; i++)
                    Assert.True(records[i].AsSpan().SequenceEqual(w.Table(table)[i].Bytes), $"{f.GetProperty("name").GetString()}: {table:X}[{i}]");
            foreach (JsonProperty p in (after.TryGetProperty("nested", out var n) ? n : snapshot.GetProperty("nested")).EnumerateObject())
                Assert.Equal(p.Value.EnumerateArray().Select(e => e.GetString()!), nested[p.Name]);
            Assert.Equal(state, w.WorldState.Bytes);
            Assert.Equal(settings, w.Settings.Bytes);
            foreach (var (head, items) in lists) Assert.Equal(items, w.List(head));
            foreach (var (name, value) in flags) Assert.Equal(value, w.Value(name));
            cases++;
        }
        Assert.Equal(9, cases);
    }

    [Fact]
    public void MissingReleaseHostFailsBeforeBilateralMutation()
    {
        JsonElement root = Vectors.Read("force_owned_storage.json.gz"), c = root.GetProperty("cases")[0];
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Vectors.Poke(w, c.GetProperty("fixture").GetProperty("pokes"));
        string[] before = w.Table(Tables.Forces).Select(r => Convert.ToHexString(r.Bytes)).ToArray();
        var clear = new List<int>();
        Assert.Throws<NotSupportedException>(() => ForceReset.Apply(w, w.Table(Tables.Forces)[3], true, clear.Add));
        Assert.Empty(clear);
        Assert.Equal(before, w.Table(Tables.Forces).Select(r => Convert.ToHexString(r.Bytes)));
    }
}

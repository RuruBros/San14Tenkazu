using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ForceCreationRecordTests
{
    [Theory]
    [InlineData("initialize", 15)]
    [InlineData("points", 16)]
    [InlineData("resources", 32)]
    public void CompleteHelpersMatchAll32TablesAndOrderedLogicalLists(string kind, int count)
    {
        JsonElement root = Vectors.Read("force_creation_records.json.gz");
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.True(root.GetProperty("native_pool").GetBoolean());
        foreach (string key in new[] { "gameplay_stubs", "allocator_stubs", "presentation_stubs" })
            Assert.False(root.GetProperty(key).GetBoolean());
        string file = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(file);
        World w = Vectors.LoadWorld(file);
        int[] tables = snapshot.GetProperty("tables").EnumerateObject().Select(p => Convert.ToInt32(p.Name, 16)).ToArray();
        Assert.Equal(32, tables.Length);
        JsonElement[] cases = root.GetProperty("cases").EnumerateArray()
            .Where(c => c.GetProperty("fixture").GetProperty("kind").GetString() == kind).ToArray();
        Assert.Equal(count, cases.Length);
        foreach (JsonElement c in cases)
        {
            Assert.True(c.GetProperty("function_complete").GetBoolean());
            JsonElement f = c.GetProperty("fixture"); string name = f.GetProperty("name").GetString()!;
            byte[][] originalForces = w.Table(Tables.Forces).Select(r => r.Bytes.ToArray()).ToArray();
            Action undo = Vectors.Poke(w, f.GetProperty("pokes"));
            try
            {
                var expected = tables.ToDictionary(t => t, t => w.Table(t).Select(r => r.Bytes.ToArray()).ToArray());
                foreach (JsonElement r in c.GetProperty("after").GetProperty("records").EnumerateArray())
                    expected[Convert.ToInt32(r[0].GetString()!, 16)][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
                byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
                var lists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                    p => w.List(Convert.ToInt32(p.Name, 16)).Select(n => n.ToArray()).ToArray());
                var nested = c.GetProperty("nested_before").EnumerateObject().ToDictionary(p => p.Name,
                    p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToList());
                var clears = new List<int>();
                if (kind == "initialize")
                {
                    int target = f.GetProperty("target").GetInt32();
                    ForceCreation.InitializeRecord(w.Table(Tables.Forces)[target], f.GetProperty("color").GetInt32() != 0,
                        f.GetProperty("enabled").GetBoolean(), at =>
                        {
                            clears.Add(at);
                            nested[$"{(at is 0x250 or 0x260 ? "force" : "creation")}:{target}:{at:x}"].Clear();
                        });
                }
                else if (kind == "resources")
                    RulerPolicy.RefreshResources(w, w.Table(Tables.Forces)[f.GetProperty("target").GetInt32()]);
                else
                    Assert.Equal(c.GetProperty("result").GetInt32(),
                        RulerPolicy.ResourcePoints(w, w.Table(Tables.Officers)[f.GetProperty("person").GetInt32()]));
                Assert.Equal(c.GetProperty("clear_calls").EnumerateArray().Select(v => v.GetInt32()), clears);
                foreach (int table in tables)
                    for (int i = 0; i < expected[table].Length; i++)
                        Assert.True(expected[table][i].AsSpan().SequenceEqual(w.Table(table)[i].Bytes), $"{name}: {table:X}/{i}");
                Assert.Equal(state, w.WorldState.Bytes); Assert.Equal(settings, w.Settings.Bytes);
                foreach (var (head, nodes) in lists) Assert.Equal(nodes, w.List(head));
                foreach (JsonProperty flag in snapshot.GetProperty("flags").EnumerateObject())
                    Assert.Equal(flag.Value.GetInt32(), w.Value(flag.Name));
                JsonElement nativeNested = c.GetProperty("after").TryGetProperty("nested", out JsonElement changed)
                    ? changed : c.GetProperty("nested_before");
                foreach (JsonProperty p in nativeNested.EnumerateObject())
                    Assert.Equal(p.Value.EnumerateArray().Select(v => v.GetString()!), nested[p.Name]);
            }
            finally
            {
                undo();
                for (int i = 0; i < originalForces.Length; i++) originalForces[i].CopyTo(w.Table(Tables.Forces)[i].Bytes, 0);
            }
        }
    }
}

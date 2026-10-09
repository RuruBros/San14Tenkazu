using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ForceCreationTests
{
    [Fact]
    public void NativeSlotSelectionMatchesWithoutChanging32TablesListsOrRng()
    {
        JsonElement root = Vectors.Read("force_creation_slots.json.gz");
        Assert.True(root.GetProperty("complete").GetBoolean());
        foreach (string key in new[] { "gameplay_stubs", "allocator_stubs", "presentation_stubs" }) Assert.False(root.GetProperty(key).GetBoolean());
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        Assert.Equal(27, root.GetProperty("cases").GetArrayLength());
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            Assert.True(c.GetProperty("function_complete").GetBoolean()); Assert.True(c.GetProperty("state_unchanged").GetBoolean());
            JsonElement f = c.GetProperty("fixture");
            World w = Vectors.LoadWorld(world); Vectors.Poke(w, f.GetProperty("pokes"));
            var records = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
            Assert.Equal(32, records.Count);
            var lists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.List(Convert.ToInt32(p.Name, 16)).Select(n => n.ToArray()).ToArray());
            byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
            Rec? selected = ForceCreation.SelectSlot(w, w.Table(Tables.Officers)[f.GetProperty("person").GetInt32()], f.GetProperty("requested").GetInt32());
            int? expected = c.GetProperty("result").ValueKind == JsonValueKind.Null ? null : c.GetProperty("result").GetInt32();
            Assert.True(expected == selected?.Index, $"{f.GetProperty("name").GetString()}: expected {expected}, actual {selected?.Index}");
            Assert.Equal(Convert.ToUInt64(c.GetProperty("native_pointer").GetString()![2..], 16), selected?.NativeAddress ?? 0);
            foreach (var (table, rows) in records)
                for (int i = 0; i < rows.Length; i++) Assert.Equal(rows[i], w.Table(table)[i].Bytes);
            foreach (var (head, nodes) in lists) Assert.Equal(nodes, w.List(head));
            Assert.Equal(state, w.WorldState.Bytes); Assert.Equal(settings, w.Settings.Bytes);
            foreach (JsonProperty p in snapshot.GetProperty("flags").EnumerateObject()) Assert.Equal(p.Value.GetInt32(), w.Value(p.Name));
        }
    }
}

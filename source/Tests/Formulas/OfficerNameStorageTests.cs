using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class OfficerNameStorageTests
{
    private static OfficerLifecycle Lifecycle(World w, OfficerNameStorage names) => new(w,
        new CorpsLifecycle(w, new Dictionary<ulong, ulong>(), new Dictionary<int, List<Rec?>>(), () => true),
        new CommandStorage(w, new Dictionary<int, CommandCancellation.Command>(), [], [], [], 0,
            _ => Assert.Fail("Slot reset does not detach commands"), _ => Assert.Fail("Slot reset does not dispose commands")),
        new Dictionary<int, List<Rec?>>(), [], names);

    private static OfficerDeath Death(World w, OfficerNameStorage names)
    {
        void Unexpected() => Assert.Fail("This death case does not use command or unit hosts");
        var commands = new CommandStorage(w, new Dictionary<int, CommandCancellation.Command>(), [], [], [], 0,
            _ => Unexpected(), _ => Unexpected());
        var corps = new CorpsLifecycle(w, new Dictionary<ulong, ulong>(), new Dictionary<int, List<Rec?>>(), () => true);
        var officers = new OfficerLifecycle(w, corps, commands, new Dictionary<int, List<Rec?>>(), [], names);
        var prisoners = new PrisonerRelease(w, officers, commands, () => throw new InvalidOperationException("No return allocation"), _ => Unexpected());
        var personalities = new UnitPersonalityLifecycle(w, new Dictionary<string, List<Rec?>>(), false, _ => Unexpected());
        var destruction = new UnitDestruction(w, personalities, prisoners, officers, false, _ => Unexpected(),
            (_, _) => Unexpected(), _ => Unexpected(), _ => Unexpected());
        return new OfficerDeath(w, officers, commands, destruction);
    }

    [Theory]
    [InlineData("officer_name_heap.json.gz", 9, false)]
    [InlineData("officer_name_heap_large.json.gz", 2, false)]
    [InlineData("officer_death_heap.json.gz", 5, true)]
    [InlineData("officer_death_heap_large.json.gz", 1, true)]
    public void HeapRemovalMatchesCompleteNativeBodyAndAllocationRelease(string vector, int count, bool death)
    {
        JsonElement root = Vectors.Read(vector);
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.False(root.GetProperty("allocator_stubs").GetBoolean());
        if (vector == "officer_death_heap_large.json.gz")
        {
            Assert.Equal("d26b0d25cc30ae9b1aeeaac56d470e801b0b8b07019a931a89a015d13545072b", root.GetProperty("input_sha256").GetString());
            Assert.Equal(2055, root.GetProperty("cases")[0].GetProperty("buffer").GetProperty("capacity").GetInt32());
        }
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        int cases = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(world);
            JsonElement f = c.GetProperty("fixture"), buffer = c.GetProperty("buffer"), after = c.GetProperty("after");
            Vectors.Poke(w, f.GetProperty("pokes"));
            int[] tables = snapshot.GetProperty("tables").EnumerateObject().Select(p => Convert.ToInt32(p.Name, 16)).ToArray();
            var expected = tables.ToDictionary(t => t, t => w.Table(t).Select(r => r.Bytes.ToArray()).ToArray());
            foreach (JsonElement r in after.GetProperty("records").EnumerateArray())
                expected[Convert.ToInt32(r[0].GetString()!, 16)][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
            ulong pointer = Convert.ToUInt64(buffer.GetProperty("pointer").GetString()![2..], 16);
            ulong allocation = Convert.ToUInt64(buffer.GetProperty("allocation").GetString()![2..], 16);
            byte[] bytes = Convert.FromHexString(buffer.GetProperty("data").GetString()!);
            var buffers = new Dictionary<ulong, byte[]> { [pointer] = bytes };
            var names = new OfficerNameStorage(buffers, new Dictionary<ulong, ulong> { [pointer] = allocation });
            byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
            var lists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16), p => w.List(Convert.ToInt32(p.Name, 16)).ToArray());
            var flags = snapshot.GetProperty("flags").EnumerateObject().ToDictionary(p => p.Name, p => w.Value(p.Name));
            Rec person = w.Table(Tables.Officers)[f.GetProperty("person").GetInt32()];
            if (death) Assert.Equal(c.GetProperty("result").GetInt32(), Death(w, names).ApplyOrdinaryWithoutItems(person, false));
            else Lifecycle(w, names).ResetSlot(person);
            Assert.Empty(buffers);
            Assert.Equal(new byte[2], bytes.AsSpan(0, 2).ToArray());
            Assert.Equal(c.GetProperty("frees").EnumerateArray().Select(e => Convert.ToUInt64(e.GetString()![2..], 16)), names.Released);
            foreach (int table in tables)
                for (int i = 0; i < expected[table].Length; i++)
                    if (!expected[table][i].SequenceEqual(w.Table(table)[i].Bytes))
                        Assert.Fail($"{f.GetProperty("name").GetString()}: {table:X}[{i}] differs at " + string.Join(",", expected[table][i].Select((b, at) => (b, at))
                            .Where(v => v.b != w.Table(table)[i].Bytes[v.at]).Select(v => $"{v.at:X}")));
            Assert.Equal(state, w.WorldState.Bytes);
            Assert.Equal(settings, w.Settings.Bytes);
            foreach (var (head, list) in lists) Assert.Equal(list, w.List(head));
            foreach (var (name, value) in flags) Assert.Equal(value, w.Value(name));
            foreach (JsonProperty p in after.EnumerateObject()) Assert.Equal("records", p.Name);
            cases++;
        }
        Assert.Equal(count, cases);
    }

    [Fact]
    public void MissingCopiedNameFailsBeforeAnyRecordWrite()
    {
        JsonElement root = Vectors.Read("officer_name_heap.json.gz"), c = root.GetProperty("cases")[0];
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Vectors.Poke(w, c.GetProperty("fixture").GetProperty("pokes"));
        Rec person = w.Table(Tables.Officers)[c.GetProperty("fixture").GetProperty("person").GetInt32()];
        byte[] before = person.Bytes.ToArray();
        Assert.Throws<InvalidDataException>(() => Lifecycle(w, new OfficerNameStorage(new Dictionary<ulong, byte[]>(), new Dictionary<ulong, ulong>())).ResetSlot(person));
        Assert.Equal(before, person.Bytes);
    }

    [Fact]
    public void MissingDeathNameFailsBeforeRelationshipOrRecordMutation()
    {
        JsonElement root = Vectors.Read("officer_death_heap.json.gz"), c = root.GetProperty("cases")[1];
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Vectors.Poke(w, c.GetProperty("fixture").GetProperty("pokes"));
        Rec person = w.Table(Tables.Officers)[c.GetProperty("fixture").GetProperty("person").GetInt32()];
        string[] before = w.Table(Tables.Officers).Select(r => Convert.ToHexString(r.Bytes)).ToArray();
        Assert.Throws<InvalidDataException>(() => Death(w, new OfficerNameStorage(new Dictionary<ulong, byte[]>(), new Dictionary<ulong, ulong>())).ApplyOrdinaryWithoutItems(person, false));
        Assert.Equal(before, w.Table(Tables.Officers).Select(r => Convert.ToHexString(r.Bytes)));
    }
}

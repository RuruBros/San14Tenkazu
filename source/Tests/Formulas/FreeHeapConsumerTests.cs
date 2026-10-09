using System.Buffers.Binary;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class FreeHeapConsumerTests
{
    private static ulong Address(string s) => Convert.ToUInt64(s[2..], 16);

    private static OfficerLifecycle Lifecycle(World w, OfficerNameStorage names, List<Rec?> queue,
        IReadOnlyDictionary<int, List<Rec?>> deferred) => new(w,
        new CorpsLifecycle(w, new Dictionary<ulong, ulong>(), new Dictionary<int, List<Rec?>>(), () => true),
        new CommandStorage(w, new Dictionary<int, CommandCancellation.Command>(), [], [], [], 0,
            _ => Assert.Fail("Heap queue consumption does not detach commands"),
            _ => Assert.Fail("Heap queue consumption does not dispose commands")), deferred, queue, names);

    [Theory]
    [InlineData("free_heap_consumer.json.gz")]
    [InlineData("free_heap_consumer_large.json.gz")]
    public void NativeHeapAllocationQueueRegistrationAndWholeConsumptionMatch(string filename)
    {
        JsonElement root = Vectors.Read(filename);
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean()); Assert.False(root.GetProperty("allocator_stubs").GetBoolean());
        bool large = filename == "free_heap_consumer_large.json.gz";
        Assert.Equal(large ? "61856bb5209eb58bc0bbb40856f1c8cb8024f38c320508f10a4fbcb35ef7e242"
            : "7140047b8dab8ec61370a069d7abe679e5842cd8c49b0b593b6684b3ab6a8763", root.GetProperty("input_sha256").GetString());
        Assert.Equal(large ? new[] { 1 } : new[] { 1, 2 }, root.GetProperty("cases").EnumerateArray().Select(c =>
            c.GetProperty("fixture").TryGetProperty("persons", out var people) ? people.GetArrayLength() : 1));
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            if (large) Assert.Equal(2055, c.GetProperty("buffer").GetProperty("capacity").GetInt32());
            VerifyConsumer(root, c);
        }
    }

    private static void VerifyConsumer(JsonElement root, JsonElement c)
    {
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world), fixture = c.GetProperty("fixture"), delta = c.GetProperty("after");
        World w = Vectors.LoadWorld(world);
        Vectors.Poke(w, fixture.GetProperty("pokes"));
        var expected = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
            p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
        Assert.Equal(29, expected.Count);
        var pointers = expected.Keys.SelectMany(t => w.Table(t)).ToDictionary(r => r.NativeAddress);
        var nested = snapshot.GetProperty("nested").EnumerateObject().ToDictionary(p => p.Name,
            p => p.Value.EnumerateArray().Select(e => Address(e.GetString()!) is var a && a != 0 ? pointers[a] : (Rec?)null).ToList());
        var deferred = nested.Where(p => p.Key.StartsWith("force_deferred:")).ToDictionary(p => int.Parse(p.Key[15..]), p => p.Value);
        var lists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16), p => w.List(Convert.ToInt32(p.Name, 16)).ToArray());
        var vectors = expected.Keys.SelectMany(t => w.Table(t)).ToDictionary(r => r,
            r => new[] { r.Vector40, r.Vector50, r.Vector70, r.Vector98, r.VectorA0, r.VectorA8, r.VectorC8, r.Vector1D0 }.Select(v => v?.ToArray()).ToArray());
        byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
        JsonElement[] inputs = c.TryGetProperty("buffers", out var allBuffers) ? allBuffers.EnumerateArray().ToArray() : [c.GetProperty("buffer")];
        var buffers = inputs.ToDictionary(b => Address(b.GetProperty("pointer").GetString()!), b => Convert.FromHexString(b.GetProperty("data").GetString()!));
        var names = new OfficerNameStorage(buffers, inputs.ToDictionary(b => Address(b.GetProperty("pointer").GetString()!), b => Address(b.GetProperty("allocation").GetString()!)));
        var queue = new List<Rec?>();
        var lifecycle = Lifecycle(w, names, queue, deferred);
        Rec[] people = fixture.TryGetProperty("persons", out var batch) ? batch.EnumerateArray().Select(i => w.Table(Tables.Officers)[i.GetInt32()]).ToArray()
            : [w.Table(Tables.Officers)[fixture.GetProperty("person").GetInt32()]];
        foreach (Rec person in people) lifecycle.QueueFree(person);
        JsonElement[] slotInputs = c.TryGetProperty("slot_inputs", out var slots) ? slots.EnumerateArray().ToArray() : [c.GetProperty("slot_input")];
        Assert.Equal(people.Length, slotInputs.Length);
        for (int i = 0; i < people.Length; i++) Assert.Equal(Convert.FromHexString(slotInputs[i].GetString()!), people[i].Bytes);
        Assert.Equal(people, queue);
        int count = lifecycle.ConsumeFree(fixture.GetProperty("count").GetInt32());
        Assert.Equal(c.GetProperty("result").GetInt32(), count);
        Assert.Empty(queue); Assert.Empty(buffers);
        Assert.Equal(c.GetProperty("frees").EnumerateArray().Select(e => Address(e.GetString()!)), names.Released);
        foreach (JsonElement row in delta.GetProperty("records").EnumerateArray())
            expected[Convert.ToInt32(row[0].GetString()!, 16)][row[1].GetInt32()] = Convert.FromHexString(row[2].GetString()!);
        foreach (var (table, records) in expected)
            for (int i = 0; i < records.Length; i++) Assert.True(records[i].AsSpan().SequenceEqual(w.Table(table)[i].Bytes), $"{table:X}[{i}]");
        foreach (var (record, data) in vectors)
            Assert.Equal(data, new[] { record.Vector40, record.Vector50, record.Vector70, record.Vector98, record.VectorA0, record.VectorA8, record.VectorC8, record.Vector1D0 });
        foreach (var (head, initial) in lists)
        {
            Rec?[][] actual = w.List(head).ToArray();
            Rec?[][] expectedList = delta.TryGetProperty("lists", out var nativeLists) && nativeLists.TryGetProperty($"0x{head:x}", out var list)
                ? list.EnumerateArray().Select(n => n.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Null ? null
                    : (Rec?)w.Table(Convert.ToInt32(r[0].GetString()!, 16))[r[1].GetInt32()]).ToArray()).ToArray() : initial;
            Assert.Equal(expectedList, actual);
        }
        foreach (JsonProperty p in (delta.TryGetProperty("nested", out var nativeNested) ? nativeNested : snapshot.GetProperty("nested")).EnumerateObject())
            Assert.Equal(p.Value.EnumerateArray().Select(e => Address(e.GetString()!) is var a && a != 0 ? pointers[a] : (Rec?)null), nested[p.Name]);
        Assert.Equal(state, w.WorldState.Bytes); Assert.Equal(settings, w.Settings.Bytes);
        foreach (JsonProperty p in snapshot.GetProperty("flags").EnumerateObject()) Assert.Equal(p.Value.GetInt32(), w.Value(p.Name));
        Assert.Equal(snapshot.GetProperty("cmdactions").EnumerateArray().Select(e => (e.GetProperty("kind").GetInt32(), string.Join(",", e.GetProperty("officers").EnumerateArray().Select(a => a.GetInt32())))),
            w.CmdActions.Select(a => (a.Kind, string.Join(",", a.Officers))));
        byte[] manager = Convert.FromHexString(c.GetProperty("free_manager").GetString()!);
        BinaryPrimitives.WriteInt32LittleEndian(manager.AsSpan(8), count);
        Assert.Equal(Convert.FromHexString(delta.GetProperty("free_manager").GetString()!), manager);
        Assert.All(delta.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "records", "free_manager", "lists", "nested" }));
    }

    [Fact]
    public void MissingLaterHeapNameRejectsTheWholeQueueBeforeFirstResetOrRelease()
    {
        JsonElement root = Vectors.Read("free_heap_consumer.json.gz"), c = root.GetProperty("cases")[0], buffer = c.GetProperty("buffer");
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Vectors.Poke(w, c.GetProperty("fixture").GetProperty("pokes"));
        Rec first = w.Table(Tables.Officers)[c.GetProperty("fixture").GetProperty("person").GetInt32()], later = w.Table(Tables.Officers)[86];
        BinaryPrimitives.WriteUInt64LittleEndian(later.Bytes.AsSpan(0x1B0), 8);
        BinaryPrimitives.WriteUInt64LittleEndian(later.Bytes.AsSpan(0x198), 0x1234);
        var queue = new List<Rec?> { first, later };
        ulong pointer = Address(buffer.GetProperty("pointer").GetString()!);
        byte[] payload = Convert.FromHexString(buffer.GetProperty("data").GetString()!);
        var buffers = new Dictionary<ulong, byte[]> { [pointer] = payload.ToArray() };
        var names = new OfficerNameStorage(buffers, new Dictionary<ulong, ulong> { [pointer] = Address(buffer.GetProperty("allocation").GetString()!) });
        byte[] a = first.Bytes.ToArray(), b = later.Bytes.ToArray();
        var registered = w.List(Lists.OfficerList).ToArray();
        Assert.Throws<InvalidDataException>(() => Lifecycle(w, names, queue, new Dictionary<int, List<Rec?>>()).ConsumeFree(188));
        Assert.Equal(a, first.Bytes); Assert.Equal(b, later.Bytes); Assert.Equal(new[] { first, later }, queue);
        Assert.Equal(registered, w.List(Lists.OfficerList)); Assert.Empty(names.Released); Assert.Equal(payload, buffers[pointer]);
    }
}

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class RulerSuccessionBodyTests
{
    private static ulong Address(string value) => Convert.ToUInt64(value[2..], 16);
    private static string Text(JsonElement value) => Encoding.Unicode.GetString(Convert.FromHexString(value.GetString()!));

    [Theory]
    [InlineData("ruler_succession_body.json.gz")]
    [InlineData("ruler_succession_departure_gate.json.gz")]
    [InlineData("ruler_succession_departure.json.gz")]
    public void WholeSuppressedNpcSuccessionMatches32TablesRngPolicyMapAndPathCache(string filename)
    {
        JsonElement root = Vectors.Read(filename);
        Assert.True(root.GetProperty("complete").GetBoolean());
        foreach (string key in new[] { "gameplay_stubs", "allocator_stubs", "presentation_stubs" }) Assert.False(root.GetProperty(key).GetBoolean());
        Assert.Single(root.GetProperty("cases").EnumerateArray());
        var setup = Prepare(filename: filename);
        Rec dying = setup.World.Table(Tables.Officers)[557];
        Assert.Equal(Address(setup.Case.GetProperty("result").GetString()!), setup.Body.ApplyNpcWithoutRebellion(dying).NativeAddress);
        setup.Verify();
    }

    [Theory]
    [InlineData("enabled_ui")]
    [InlineData("enabled_history")]
    [InlineData("missing_departure_engine")]
    [InlineData("missing_doctrine")]
    [InlineData("missing_map")]
    public void UnsupportedBodiesFailBeforeRecordsCacheAndPresentation(string guard)
    {
        var setup = Prepare(guard);
        World w = setup.World;
        if (guard == "missing_departure_engine")
        {
            // The completed native departure fixture requires a lifecycle before the first caller mutation.
            BinaryPrimitives.WriteUInt16LittleEndian(w.Table(Tables.Officers)[384].Bytes.AsSpan(0x166), 190);
            BinaryPrimitives.WriteInt16LittleEndian(w.Table(Tables.Officers)[384].Bytes.AsSpan(0x16C), -100);
            BinaryPrimitives.WriteInt32LittleEndian(w.WorldState.Bytes.AsSpan(0x450), 0);
        }
        JsonElement snapshot = Vectors.Read("ruler_succession_body_world.json.gz");
        var records = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
            p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
        byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
        byte[][] maps = setup.Maps.Select(m => m!.Bytes.ToArray()).ToArray();
        byte[] path = setup.Path.Bytes.ToArray();
        if (guard is "missing_doctrine" or "missing_map")
            Assert.Throws<InvalidDataException>(() => setup.Body.ApplyNpcWithoutRebellion(w.Table(Tables.Officers)[557]));
        else Assert.Throws<NotSupportedException>(() => setup.Body.ApplyNpcWithoutRebellion(w.Table(Tables.Officers)[557]));
        Assert.Equal(0, setup.Calls());
        foreach (var (table, rows) in records)
            for (int i = 0; i < rows.Length; i++) Assert.Equal(rows[i], w.Table(table)[i].Bytes);
        Assert.Equal(state, w.WorldState.Bytes); Assert.Equal(settings, w.Settings.Bytes);
        Assert.Equal(path, setup.Path.Bytes);
        Assert.Equal(maps, setup.Maps.Select(m => m!.Bytes));
    }

    private static (World World, RulerSuccessionBody Body, JsonElement Case, Rec?[] Maps, Rec Path, Action Verify, Func<int> Calls)
        Prepare(string guard = "", string filename = "ruler_succession_body.json.gz")
    {
        JsonElement root = Vectors.Read(filename), c = root.GetProperty("cases")[0];
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world), f = c.GetProperty("fixture"), delta = c.GetProperty("after");
        Assert.Equal("01000000", snapshot.GetProperty("ui_event_guard_input").GetProperty("fixture").GetString());
        Assert.Equal(85, snapshot.GetProperty("desertion_loyalty_limit").GetInt32());
        World w = Vectors.LoadWorld(world);
        Vectors.Poke(w, f.GetProperty("pokes"));
        var expected = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
            p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
        Assert.Equal(32, expected.Count);
        foreach (JsonElement row in delta.GetProperty("records").EnumerateArray())
            expected[Convert.ToInt32(row[0].GetString()!, 16)][row[1].GetInt32()] = Convert.FromHexString(row[2].GetString()!);
        var vectors = expected.Keys.SelectMany(w.Table).ToDictionary(r => r,
            r => new[] { r.Vector40, r.Vector50, r.Vector70, r.Vector98, r.VectorA0, r.VectorA8, r.VectorC8, r.Vector1D0 }.Select(v => v?.ToArray()).ToArray());
        byte[] state = w.WorldState.Bytes.ToArray();
        byte[] settings = Convert.FromHexString(delta.GetProperty("settings").GetString()!);
        var pointers = expected.Keys.SelectMany(w.Table).ToDictionary(r => r.NativeAddress);
        Rec? Ref(JsonElement r) => r.ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(r[0].GetString()!, 16))[r[1].GetInt32()];
        Rec? Pointer(JsonElement r) => Address(r.GetString()!) is var a && a != 0 ? pointers[a] : null;
        // Force +0x250 pointers lie outside the copied record tables; the retained native audit covers them.
        var nested = snapshot.GetProperty("nested").EnumerateObject().Where(p => !p.Name.StartsWith("force:") || !p.Name.EndsWith(":250"))
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(Pointer).ToList());
        var cache = snapshot.GetProperty("corps_cache").EnumerateObject().ToDictionary(p => Address(p.Name), p => Address(p.Value.GetString()!));
        var corps = new CorpsLifecycle(w, cache, nested.Where(p => p.Key.StartsWith("caution:"))
            .ToDictionary(p => int.Parse(p.Key[8..]), p => p.Value), () => throw new InvalidOperationException("No corps pool changes"));
        var commands = ReturnCommandTests.Commands(w, snapshot);
        var storage = ReturnCommandTests.Storage(w, snapshot, commands, _ => Assert.Fail("No command detachment"), _ => Assert.Fail("No command deletion"));
        Rec?[] maps = snapshot.GetProperty("map_managers").EnumerateArray().Select((m, i) => m.ValueKind == JsonValueKind.Null ? null :
            new Rec(i, Convert.FromHexString(m.GetProperty("data").GetString()!), nativeAddress: Address(m.GetProperty("address").GetString()!))).ToArray();
        JsonElement pathInput = snapshot.GetProperty("path_cache_input");
        Rec path = new(0, Convert.FromHexString(pathInput.GetProperty("data").GetString()!), nativeAddress: Address(pathInput.GetProperty("address").GetString()!));
        var events = new Queue<JsonElement>(c.GetProperty("events").EnumerateArray());
        int calls = 0;
        JsonElement Boundary(string kind)
        {
            calls++;
            Assert.True(events.Count > 0, $"Unexpected {kind}");
            JsonElement e = events.Dequeue(); Assert.Equal(kind, e.GetProperty("kind").GetString()); return e;
        }
        var rng = new Rng(BitConverter.ToInt32(Convert.FromHexString(snapshot.GetProperty("rng").GetString()!)));
        void Draws()
        {
            while (events.TryPeek(out var e) && e.GetProperty("kind").GetString() == "presentation_rng")
            {
                events.Dequeue(); Assert.Equal(e.GetProperty("before").GetInt32(), rng.State);
                rng.Next(e.GetProperty("n").GetInt32());
                BinaryPrimitives.WriteInt32LittleEndian(w.Settings.Bytes.AsSpan(Rng.StateRva - snapshot.GetProperty("exe_settings_rva").GetInt32()), rng.State);
            }
        }
        void Bind(string kind, int value, int slot)
        {
            JsonElement e = Boundary(kind);
            Assert.Equal(value, e.GetProperty(kind == "force" ? "index" : "id").GetInt32());
            Assert.Equal(slot, e.GetProperty("slot").GetInt32());
        }
        JsonElement historyInput = c.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "history");
        var presentation = new RulerSuccessionPresentation(() => Boundary("reset"), (id, slot) => Bind("force", id, slot),
            (id, slot) => Bind("officer", id, slot), id =>
            {
                Assert.Equal(id, Boundary("message").GetProperty("id").GetInt32()); Draws();
                return id switch { 0x2A88 => Text(historyInput.GetProperty("texts")[0]), 0x533E => Text(historyInput.GetProperty("texts")[1]), _ => "" };
            }, value =>
            {
                JsonElement e = Boundary("script"); Assert.Equal("0x451ff0", e.GetProperty("entry").GetString()); Assert.Equal(value, e.GetProperty("value").GetInt32());
            }, () =>
            {
                JsonElement e = Boundary("script"); Assert.Equal("0x43ff90", e.GetProperty("entry").GetString()); Assert.Equal(0, e.GetProperty("value").GetInt32());
            }, (id, _) => Assert.Equal(id, Boundary("speaker").GetProperty("id").GetInt32()), entry =>
            {
                JsonElement e = Boundary("history");
                Assert.Equal(e.GetProperty("args").EnumerateArray().Select(a => a.GetInt32()),
                    new[] { entry.Kind, entry.Corps, entry.Hex, entry.Player, entry.Tick, entry.Officer, entry.Immediate, entry.Flag4, entry.Flag5, entry.Flag6 });
                Assert.Equal(Text(e.GetProperty("texts")[0]), entry.Primary); Assert.Equal(Text(e.GetProperty("texts")[1]), entry.Secondary);
            });
        var history = new HistoryLog(w, w.Table(Tables.History), guard != "enabled_history", true,
            _ => throw new InvalidOperationException("Suppressed history reads no abbreviation"), _ => Assert.Fail("Suppressed history does not refresh"));
        Rec[] lookup = snapshot.GetProperty("doctrine_byid").EnumerateArray().Select(i => w.Table(Tables.Doctrine)[i.GetInt32()]).ToArray();
        var policy = new RulerPolicy(w, guard == "missing_doctrine" ? [] : lookup, () => Assert.Fail("NPC policy does not notify player"));
        var engine = new RulerSuccessionEngine(guard == "missing_map" ? maps[..5] : maps, path,
            () => Boundary("map_refresh"), () => Boundary("path_cache_reset"));
        var deferred = nested.Where(p => p.Key.StartsWith("force:") && p.Key.EndsWith(":260"))
            .ToDictionary(p => int.Parse(p.Key.Split(':')[1]), p => p.Value);
        var officers = new OfficerLifecycle(w, corps, storage, deferred, []);
        var body = new RulerSuccessionBody(w, corps, storage, policy, history, presentation, engine, guard != "enabled_ui",
            guard == "missing_departure_engine" ? null : officers);
        Rec dying = w.Table(Tables.Officers)[557], successor = w.Table(Tables.Officers)[567];
        var preview = w.List(Lists.OfficerList).Select(n => n[0]!).Where(p => OfficerRelations.RawForce(w, p) == 3)
            .ToDictionary(p => p, p => RulerChange.PreviewLoyalty(w, p, dying, successor));
        void Verify()
        {
            Assert.Empty(events);
            foreach (var (table, rows) in expected)
                for (int i = 0; i < rows.Length; i++) Assert.True(rows[i].AsSpan().SequenceEqual(w.Table(table)[i].Bytes), $"{table:X}[{i}]");
            foreach (var (person, loyalty) in preview)
                if (OfficerRelations.RawForce(w, person) == 3) Assert.Equal(loyalty, person.U8(0xE8));
            foreach (var (record, data) in vectors)
                Assert.Equal(data, new[] { record.Vector40, record.Vector50, record.Vector70, record.Vector98, record.VectorA0, record.VectorA8, record.VectorC8, record.Vector1D0 });
            Assert.Equal(state, w.WorldState.Bytes); Assert.Equal(settings, w.Settings.Bytes);
            foreach (JsonProperty p in snapshot.GetProperty("lists").EnumerateObject())
                Assert.Equal(p.Value.GetProperty("items").EnumerateArray().Select(n => n.EnumerateArray().Select(Ref).ToArray()), w.List(Convert.ToInt32(p.Name, 16)));
            foreach (var (key, members) in nested)
                Assert.Equal(snapshot.GetProperty("nested").GetProperty(key).EnumerateArray().Select(e => Address(e.GetString()!)), members.Select(r => r?.NativeAddress ?? 0));
            Assert.Equal(snapshot.GetProperty("corps_cache").EnumerateObject().Select(p => (Address(p.Name), Address(p.Value.GetString()!))).Order(), cache.Select(p => (p.Key, p.Value)).Order());
            for (int i = 0; i < maps.Length; i++)
                Assert.Equal(delta.GetProperty("map_marks")[i].ValueKind == JsonValueKind.Null ? null : Convert.FromHexString(delta.GetProperty("map_marks")[i].GetString()!), maps[i]?.Bytes.AsSpan(0xA0, 4).ToArray());
            Assert.Equal(Convert.FromHexString(delta.GetProperty("path_cache").GetString()!), path.Bytes);
            Assert.Equal(delta.GetProperty("rng").GetString(), Convert.ToHexString(BitConverter.GetBytes(rng.State)).ToLowerInvariant());
            foreach (var command in commands)
            {
                JsonElement input = snapshot.GetProperty("commands").EnumerateArray().Single(e => Address(e.GetProperty("address").GetString()!) == command.Data.NativeAddress);
                Assert.Equal(Convert.FromHexString(input.GetProperty("data").GetString()!), command.Data.Bytes);
                Assert.Equal(input.GetProperty("officers").EnumerateArray().Select(e => e.GetInt32()), command.Officers);
            }
            JsonElement store = snapshot.GetProperty("command_storage");
            Assert.Equal(store.GetProperty("free_head").GetUInt16(), storage.FreeHead);
            Assert.Equal(store.GetProperty("free_ids").EnumerateArray().Select(e => e.GetUInt16()), storage.FreeIds);
            Assert.Equal(store.GetProperty("by_id").EnumerateObject().Select(p => (int.Parse(p.Name), Address(p.Value.GetString()!))).Order(), storage.ById.Select(p => (p.Key, p.Value.Data.NativeAddress)).Order());
            Assert.Equal(store.GetProperty("active").EnumerateArray().Select(e => Address(e.GetString()!)), storage.Active.Select(p => p.Data.NativeAddress));
            Assert.Equal(store.GetProperty("pending").EnumerateArray().Select(e => Address(e.GetString()!)), storage.Pending.Select(p => p.Data.NativeAddress));
            Assert.Equal(snapshot.GetProperty("cmdactions").EnumerateArray().Select(e => (e.GetProperty("kind").GetInt32(), string.Join(",", e.GetProperty("officers").EnumerateArray().Select(n => n.GetInt32())))), w.CmdActions.Select(e => (e.Kind, string.Join(",", e.Officers))));
            foreach (JsonProperty p in snapshot.GetProperty("flags").EnumerateObject()) Assert.Equal(p.Value.GetInt32(), w.Value(p.Name));
            Assert.All(delta.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "records", "settings", "rng", "map_marks", "path_cache" }));
        }
        return (w, body, c, maps, path, Verify, () => calls);
    }
}

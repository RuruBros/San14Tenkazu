using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ForceFallBodyTests
{
    private static ulong Address(string s) => Convert.ToUInt64(s[2..], 16);

    [Theory]
    [InlineData("force_fall_body.json.gz")]
    [InlineData("force_fall_prisoner.json.gz")]
    public void WholeNonterminalNpcTeardownMatchesAll32TablesListsCachesAndReturnStorage(string filename)
    {
        var setup = Prepare(filename);
        Assert.Equal(0, setup.Body.ApplyNonterminalNpc(setup.World.Table(Tables.Forces)[3], setup.World.Table(Tables.Forces)[23]));
        setup.Verify();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayerOrTerminalBranchesFailBeforeGameplayAndHostRequests(bool terminal)
    {
        var setup = Prepare();
        World w = setup.World;
        if (terminal)
            foreach (Rec force in w.Table(Tables.Forces).Where(f => f.Index < 46 && f.Index != 3))
                BinaryPrimitives.WriteUInt16LittleEndian(force.Bytes.AsSpan(0x10), 0);
        else w.WorldState.Bytes[0x3A] = 3;
        JsonElement snapshot = Vectors.Read("force_fall_body_world.json.gz");
        var records = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
            p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
        byte[] state = w.WorldState.Bytes.ToArray();
        Assert.Throws<NotSupportedException>(() => setup.Body.ApplyNonterminalNpc(w.Table(Tables.Forces)[3], w.Table(Tables.Forces)[23]));
        Assert.Equal(0, setup.Calls());
        foreach (var (table, rows) in records)
            for (int i = 0; i < rows.Length; i++) Assert.Equal(rows[i], w.Table(table)[i].Bytes);
        Assert.Equal(state, w.WorldState.Bytes);
    }

    private static (World World, ForceFallBody Body, Action Verify, Func<int> Calls) Prepare(string filename = "force_fall_body.json.gz")
    {
        JsonElement root = Vectors.Read(filename);
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.False(root.GetProperty("allocator_stubs").GetBoolean());
        Assert.Equal("58e4dd78cf3fbeb9399a1d064b86693292239ec5a60ddb1d2c077032c935443b", root.GetProperty("input_sha256").GetString());
        JsonElement c = Assert.Single(root.GetProperty("cases").EnumerateArray()), delta = c.GetProperty("after");
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        World w = Vectors.LoadWorld(world);
        JsonElement pokes = c.GetProperty("fixture").GetProperty("pokes");
        if (filename == "force_fall_body.json.gz") Assert.Empty(pokes.EnumerateArray());
        Vectors.Poke(w, pokes);
        var expected = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
            p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
        Assert.Equal(32, expected.Count);
        foreach (JsonElement row in delta.GetProperty("records").EnumerateArray())
            expected[Convert.ToInt32(row[0].GetString()!, 16)][row[1].GetInt32()] = Convert.FromHexString(row[2].GetString()!);
        var pointers = expected.Keys.SelectMany(t => w.Table(t)).ToDictionary(r => r.NativeAddress);
        Rec? Ref(JsonElement r) => r.ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(r[0].GetString()!, 16))[r[1].GetInt32()];
        // Force +0x250 stores opaque heap pointers; clearing it never dereferences their payloads.
        var forceLists = snapshot.GetProperty("nested").EnumerateObject().Where(p => p.Name.StartsWith("force:")).ToDictionary(p => p.Name,
            p => p.Value.EnumerateArray().Select(e => Address(e.GetString()!)).ToList());
        var nested = snapshot.GetProperty("nested").EnumerateObject().Where(p => !p.Name.StartsWith("force:")).ToDictionary(p => p.Name,
            p => p.Value.EnumerateArray().Select(e => Address(e.GetString()!) is var a && a != 0 ? pointers[a] : (Rec?)null).ToList());
        var vectors = expected.Keys.SelectMany(t => w.Table(t)).ToDictionary(r => r,
            r => new[] { r.Vector40, r.Vector50, r.Vector70, r.Vector98, r.VectorA0, r.VectorA8, r.VectorC8, r.Vector1D0 }.Select(v => v?.ToArray()).ToArray());
        foreach (JsonProperty p in c.GetProperty("county_vectors").EnumerateObject())
            vectors[w.Table(Tables.Officers)[int.Parse(p.Name)]][7] = Convert.FromHexString(p.Value.GetString()!);
        JsonElement Final(string key) => delta.TryGetProperty(key, out var value) ? value : snapshot.GetProperty(key);
        Dictionary<ulong, ulong> Cache(string key) => snapshot.GetProperty(key).EnumerateObject()
            .ToDictionary(p => Address(p.Name), p => Address(p.Value.GetString()!));
        var forceCache = Cache("force_cache");
        var corpsCache = Cache("corps_cache");
        var troopCache = Cache("troop_cache");
        var unitCache = Cache("unit_cache_pairs");
        JsonElement[] nativeEvents = c.GetProperty("events").EnumerateArray().ToArray();
        var events = new Queue<JsonElement>(nativeEvents);
        var callbacks = new Queue<JsonElement>(c.GetProperty("callbacks").EnumerateArray());
        int calls = 0, allocations = 0;
        JsonElement Next(Queue<JsonElement> queue, string kind)
        {
            calls++;
            Assert.True(queue.Count > 0, $"Unexpected {kind}");
            JsonElement e = queue.Dequeue();
            Assert.Equal(kind, e.GetProperty("kind").GetString());
            return e;
        }
        void CallbackUnit(string kind, Rec unit) => Assert.Equal(unit.Index, Next(callbacks, kind).GetProperty("unit").GetInt32());
        var commands = ReturnCommandTests.Commands(w, snapshot);
        var storage = ReturnCommandTests.Storage(w, snapshot, commands,
            _ => Assert.Fail("Unexpected command UI detach"), _ => Assert.Fail("Unexpected command disposal"));
        Rec Allocate()
        {
            var allocation = c.GetProperty("allocations")[allocations++];
            return new(-1, Convert.FromHexString(allocation.GetProperty("data").GetString()!), nativeAddress: Address(allocation.GetProperty("address").GetString()!));
        }
        var cautions = nested.Where(p => p.Key.StartsWith("caution:")).ToDictionary(p => int.Parse(p.Key[8..]), p => p.Value);
        var deferred = forceLists.Where(p => p.Key.EndsWith(":260")).ToDictionary(p => int.Parse(p.Key.Split(':')[1]),
            p => p.Value.Select(a => (Rec?)pointers[a]).ToList());
        bool pool = snapshot.GetProperty("pool_enabled").GetBoolean();
        var corps = new CorpsLifecycle(w, corpsCache, cautions, () => pool, storage, Allocate,
            _ => Assert.Fail("Unexpected return-command refresh"));
        // Marked teardown is rejected before execution; this scene never visits the free queue.
        var officers = new OfficerLifecycle(w, corps, storage, deferred, []);
        var prisoners = new PrisonerRelease(w, officers, storage, Allocate, _ => Assert.Fail("Unexpected return-command refresh"));
        bool refresh = snapshot.GetProperty("personality_refresh_available").GetBoolean();
        var personalities = new UnitPersonalityLifecycle(w, nested, refresh, unit => CallbackUnit("unit_refresh", unit));
        var destruction = new UnitDestruction(w, personalities, prisoners, officers, refresh,
            unit => CallbackUnit("unit_refresh", unit), (_, _) => Assert.Fail("Unexpected morale effect"),
            _ => Assert.Fail("Unexpected difficulty log"), _ => Assert.Fail("Unexpected recursive unit route"));
        var finalizer = new UnitFinalizer(w, nested["world:130"], cautions, unitCache,
            new(snapshot.GetProperty("unit_listener_available").GetBoolean() ? unit =>
            {
                Assert.Same(unit, Ref(Next(callbacks, "unit_notify").GetProperty("target")));
            } : null, unit => CallbackUnit("unit_detach", unit), () => pool, () => pool,
            (unit, at) => nested[$"unit:{unit.Index}:{at:x}"].Clear(), troop =>
            {
                Assert.Equal(troop.Index, Next(callbacks, "troop_cache").GetProperty("troop").GetInt32());
                UnitLifecycle.RemoveTroopCache(troop, troopCache,
                    obj => Address(snapshot.GetProperty("ui_owners").GetProperty($"0x{obj:x}").GetString()!), (obj, kind) =>
                    {
                        JsonElement e = Next(callbacks, "properties");
                        Assert.Equal(obj, Address(e.GetProperty("object").GetString()!));
                        Assert.Equal(kind, e.GetProperty("type").GetInt32());
                    }, (owner, obj) =>
                    {
                        JsonElement e = Next(callbacks, "destroy");
                        Assert.Equal(owner, Address(e.GetProperty("owner").GetString()!));
                        Assert.Equal(obj, Address(e.GetProperty("object").GetString()!));
                    });
            }));
        string primary = Encoding.Unicode.GetString(Convert.FromHexString(nativeEvents.Single(e => e.GetProperty("kind").GetString() == "history").GetProperty("texts")[0].GetString()!));
        var presentation = new ForceEventPresentation(() => Next(events, "reset"), (force, slot) =>
        {
            JsonElement e = Next(events, "force");
            Assert.Equal(force, e.GetProperty("index").GetInt32()); Assert.Equal(slot, e.GetProperty("slot").GetInt32());
        }, (_, _) => Assert.Fail("Unexpected force-pair localization"), message =>
        {
            Assert.Equal(message, Next(events, "message").GetProperty("id").GetInt32());
            return message == 0x3200 ? primary : $"host message {message:X}";
        }, sound => Assert.Equal(sound, Next(events, "sound").GetProperty("value").GetInt32()), (text, speaker) =>
        {
            Assert.Equal("host message 3201", text); Assert.Null(speaker); Next(events, "announcement");
        });
        var history = new HistoryLog(w, w.Table(Tables.History).Cast<Rec?>().ToArray(), false, true,
            force => { Assert.Equal(0, force.Index); return ""; }, record =>
            {
                JsonElement e = Next(events, "history");
                Assert.Equal(new[] { 5, 0, 0, 1, w.WorldState.I32(0x450), 5012, 1, 0, 0, 0 },
                    e.GetProperty("args").EnumerateArray().Select(a => a.GetInt32()).ToArray());
                Assert.Equal(expected[Tables.History][record.Index], record.Bytes);
                Assert.Equal(record.Index, Next(events, "history_refresh").GetProperty("index").GetInt32());
            });
        Rec profile = new(0, Convert.FromHexString(snapshot.GetProperty("profile").GetString()!));
        byte[] countyBits = Convert.FromHexString(snapshot.GetProperty("county_bits").GetString()!);
        int mapMark = BitConverter.ToInt32(Convert.FromHexString(snapshot.GetProperty("map_repaint_before").GetString()!));
        int managerMark = BitConverter.ToInt32(Convert.FromHexString(snapshot.GetProperty("manager_mark").GetString()!));
        var achievements = new ProfileAchievements(w, profile,
            snapshot.GetProperty("achievement_excluded_bits").EnumerateArray().Select(e => e.GetUInt32()).ToArray(),
            snapshot.GetProperty("notification_available").GetBoolean(),
            () => throw new InvalidOperationException("Unexpected profile clock"), _ => Assert.Fail("Unexpected profile notification"));
        var ownership = new TileOwnership(w, achievements, snapshot.GetProperty("map_repaint_available").GetBoolean(), () => mapMark = 1);
        var transfer = new TileTransfer(w, ownership, countyBits, () => managerMark = 1,
            (_, _, _) => Assert.Fail("Unexpected territory unit notification"), (_, _) => Assert.Fail("Unexpected county announcement"),
            (_, _, _) => Assert.Fail("Unexpected territory effect"));
        var coalition = new ForceCoalition(w, ownership, transfer, snapshot.GetProperty("coalition_days").GetInt32(),
            snapshot.GetProperty("coalition_refresh_available").GetBoolean(), () => Next(events, "coalition_refresh"));
        var forceEvents = new ForceEvents(w, coalition, history, presentation, snapshot.GetProperty("coalition_relation_gain").GetInt32());
        var host = new ForceFallHost(() => pool, snapshot.GetProperty("fall_sound_suppressed").GetBoolean(),
            snapshot.GetProperty("event_log_suppressed").GetBoolean(), snapshot.GetProperty("strategy_refresh_available").GetBoolean(),
            (_, _) => Assert.Fail("Unexpected officer localization"), playback =>
            {
                JsonElement e = Next(callbacks, "fall_event"); byte[] data = Convert.FromHexString(e.GetProperty("data").GetString()!);
                Assert.Equal(BitConverter.ToInt32(data, 0), playback.Kind); Assert.Equal(BitConverter.ToInt32(data, 4), playback.Subkind);
                Assert.Equal(BitConverter.ToInt32(data, 8), playback.Tick); Assert.Equal(e.GetProperty("append").GetInt32(), playback.Append);
                Assert.Equal(3, playback.Force.Index); Assert.Equal(playback.Force.U16(0x10), playback.Ruler);
            }, force => Assert.Same(force, Ref(Next(callbacks, "force_notify").GetProperty("target"))),
            () => Next(callbacks, "strategy_refresh"));
        var body = new ForceFallBody(w, forceEvents, history, presentation, transfer, ownership, destruction, officers,
            corps, finalizer, forceCache, forceLists, host);
        void Verify()
        {
            Assert.Empty(events); Assert.Empty(callbacks); Assert.Equal(c.GetProperty("allocations").GetArrayLength(), allocations);
            foreach (var (table, rows) in expected)
                for (int i = 0; i < rows.Length; i++)
                {
                    byte[] actual = w.Table(table)[i].Bytes;
                    Assert.True(rows[i].AsSpan().SequenceEqual(actual), $"{table:X}[{i}] differs: " +
                        string.Join(",", rows[i].Select((v, at) => (v, at)).Where(p => p.v != actual[p.at]).Select(p => $"+{p.at:X}:{p.v:X2}/{actual[p.at]:X2}")));
                }
            foreach (var (record, data) in vectors)
                Assert.Equal(data, new[] { record.Vector40, record.Vector50, record.Vector70, record.Vector98, record.VectorA0, record.VectorA8, record.VectorC8, record.Vector1D0 });
            Assert.Equal(Convert.FromHexString(Final("worldstate").GetString()!), w.WorldState.Bytes);
            Assert.Equal(Convert.FromHexString(Final("settings").GetString()!), w.Settings.Bytes);
            Assert.Equal(Convert.FromHexString(Final("profile").GetString()!), profile.Bytes);
            byte[] nativeBits = Convert.FromHexString(Final("county_bits").GetString()!);
            JsonElement alias = Assert.Single(root.GetProperty("dirty_storage_aliases").EnumerateArray());
            Assert.Equal("native_destination_list_pool", alias.GetProperty("kind").GetString());
            Assert.Equal(0x2143270, alias.GetProperty("rva").GetInt32());
            int aliasAt = alias.GetProperty("offset").GetInt32(), aliasLength = alias.GetProperty("length").GetInt32();
            Assert.Equal(18 * 8, aliasLength);
            for (int at = 0; at < nativeBits.Length; at++)
                if (at < aliasAt || at >= aliasAt + aliasLength) Assert.Equal(nativeBits[at], countyBits[at]);
            // Physical free/wrapper pointers belong to the native allocator; compare its semantic descriptor count.
            byte[] originalBits = Convert.FromHexString(snapshot.GetProperty("county_bits").GetString()!);
            ulong newDescriptors = (ulong)c.GetProperty("created").EnumerateArray().Sum(e => storage.Active.Single(a =>
                a.Data.NativeAddress == Address(e.GetProperty("address").GetString()!)).Destinations!.Count);
            Assert.Equal(BitConverter.ToUInt64(originalBits, aliasAt + 0x30) + newDescriptors, BitConverter.ToUInt64(nativeBits, aliasAt + 0x30));
            Assert.Equal(Convert.FromHexString(Final("map_mark").GetString()!), BitConverter.GetBytes(mapMark));
            Assert.Equal(Convert.FromHexString(Final("manager_mark").GetString()!), BitConverter.GetBytes(managerMark));
            foreach (JsonProperty p in snapshot.GetProperty("flags").EnumerateObject()) Assert.Equal(p.Value.GetInt32(), w.Value(p.Name));
            foreach (JsonProperty p in snapshot.GetProperty("lists").EnumerateObject())
            {
                JsonElement list = delta.TryGetProperty("lists", out var changed) && changed.TryGetProperty(p.Name, out var items) ? items : p.Value.GetProperty("items");
                Assert.Equal(list.EnumerateArray().Select(n => n.EnumerateArray().Select(Ref).ToArray()).ToArray(), w.List(Convert.ToInt32(p.Name, 16)).ToArray());
            }
            foreach (JsonProperty p in Final("nested").EnumerateObject())
            {
                if (p.Name.StartsWith("force:"))
                {
                    Assert.Equal(p.Value.EnumerateArray().Select(e => Address(e.GetString()!)), forceLists[p.Name]);
                    continue;
                }
                IEnumerable<Rec?> actual = p.Name == "registered_counties" ? w.List(Lists.CountyList).Select(n => n[0])
                    : p.Name.StartsWith("registered:") ? w.List(Convert.ToInt32(p.Name[11..], 16)).Select(n => n[0]) : nested[p.Name];
                Assert.Equal(p.Value.EnumerateArray().Select(e => Address(e.GetString()!) is var a && a != 0 ? pointers[a] : (Rec?)null), actual);
            }
            foreach (var (key, actual) in new[] { ("force_cache", forceCache), ("corps_cache", corpsCache), ("troop_cache", troopCache), ("unit_cache", unitCache) })
            {
                JsonElement value = delta.TryGetProperty(key, out var next) ? next : snapshot.GetProperty(key == "unit_cache" ? "unit_cache_pairs" : key);
                if (key == "unit_cache" && value.ValueKind == JsonValueKind.Array)
                {
                    Assert.Equal(value.EnumerateArray().Select(e => Address(e.GetString()!)).Order(), actual.Keys.Order());
                    foreach (var pair in actual) Assert.Equal(Address(snapshot.GetProperty("unit_cache_pairs").GetProperty($"0x{pair.Key:x}").GetString()!), pair.Value);
                    continue;
                }
                Assert.Equal(value.EnumerateObject().Select(p => (Address(p.Name), Address(p.Value.GetString()!))).Order(), actual.Select(p => (p.Key, p.Value)).Order());
            }
            JsonElement store = Final("command_storage");
            Assert.Equal(store.GetProperty("free_head").GetUInt16(), storage.FreeHead);
            Assert.Equal(store.GetProperty("free_ids").EnumerateArray().Select(e => e.GetUInt16()), storage.FreeIds);
            Assert.Equal(store.GetProperty("by_id").EnumerateObject().Select(e => (int.Parse(e.Name), Address(e.Value.GetString()!))).Order(), storage.ById.Select(e => (e.Key, e.Value.Data.NativeAddress)).Order());
            Assert.Equal(store.GetProperty("active").EnumerateArray().Select(e => Address(e.GetString()!)), storage.Active.Select(e => e.Data.NativeAddress));
            Assert.Equal(store.GetProperty("pending").EnumerateArray().Select(e => Address(e.GetString()!)), storage.Pending.Select(e => e.Data.NativeAddress));
            Assert.Equal(Final("cmdactions").EnumerateArray().Select(e => (e.GetProperty("kind").GetInt32(), string.Join(",", e.GetProperty("officers").EnumerateArray().Select(a => a.GetInt32())))),
                w.CmdActions.Select(a => (a.Kind, string.Join(",", a.Officers))));
            foreach (JsonElement created in c.GetProperty("created").EnumerateArray())
            {
                var command = Assert.Single(storage.Active, a => a.Data.NativeAddress == Address(created.GetProperty("address").GetString()!));
                byte[] bytes = Convert.FromHexString(created.GetProperty("data").GetString()!);
                var physical = root.GetProperty("physical_fields").EnumerateArray().Select(p => (At: p[0].GetInt32(), Length: p[1].GetInt32())).ToArray();
                for (int at = 0; at < bytes.Length; at++) if (!physical.Any(p => at >= p.At && at < p.At + p.Length)) Assert.Equal(bytes[at], command.Data.Bytes[at]);
                JsonElement[] descriptors = created.GetProperty("descriptors").EnumerateArray().ToArray();
                Assert.Equal(descriptors.Length, command.Destinations!.Count);
                for (int i = 0; i < descriptors.Length; i++)
                {
                    byte[] data = Convert.FromHexString(descriptors[i].GetString()!); var d = command.Destinations[i];
                    Assert.Equal(BitConverter.ToUInt64(data, 0), d.Primary.NativeAddress); Assert.Equal(BitConverter.ToInt32(data, 8), d.PrimaryKind);
                    Assert.Equal(BitConverter.ToInt32(data, 12), d.SecondaryKind); Assert.Equal(BitConverter.ToUInt64(data, 16), d.Secondary.NativeAddress);
                }
            }
        }
        return (w, body, Verify, () => calls);
    }
}

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class OfficerDeathPlayerTests
{
    private static ulong Address(string value) => Convert.ToUInt64(value[2..], 16);
    private static string Text(JsonElement value) => Encoding.Unicode.GetString(Convert.FromHexString(value.GetString()!));

    [Theory]
    [InlineData("officer_death_player.json.gz", 3)]
    [InlineData("officer_death_player_execution.json.gz", 1)]
    [InlineData("officer_death_player_extra.json.gz", 2)]
    [InlineData("officer_death_player_marked.json.gz", 1)]
    public void WholeOrdinaryPlayerDeathMatches32TablesHistoryRngAndHostRequests(string filename, int count)
    {
        JsonElement root = Vectors.Read(filename);
        Assert.True(root.GetProperty("complete").GetBoolean());
        foreach (string key in new[] { "gameplay_stubs", "allocator_stubs", "presentation_stubs" }) Assert.False(root.GetProperty(key).GetBoolean());
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        int cases = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            var setup = Prepare(snapshot, world, c);
            JsonElement f = c.GetProperty("fixture");
            Rec person = setup.World.Table(Tables.Officers)[f.GetProperty("person").GetInt32()];
            Assert.Equal(0, setup.Death.ApplyPlayerOrdinary(person, f.GetProperty("mode").GetInt32(), f.GetProperty("history").GetBoolean(), setup.Notice));
            setup.Verify();
            cases++;
        }
        Assert.Equal(count, cases);
    }

    [Theory]
    [InlineData("missing_adviser")]
    [InlineData("invalid_office_cutscene")]
    public void UnsupportedNoticeFailsBeforeHistoryRecordsRelationsAndHostRequests(string guard)
    {
        JsonElement root = Vectors.Read("officer_death_player.json.gz"), c = root.GetProperty("cases")[0];
        string world = root.GetProperty("world").GetString()!;
        var setup = Prepare(Vectors.Read(world), world, c);
        World w = setup.World;
        if (guard == "missing_adviser") BinaryPrimitives.WriteUInt16LittleEndian(w.Table(Tables.Forces)[3].Bytes.AsSpan(0x52), 0);
        else
        {
            w.Table(Tables.Officers)[86].Bytes[0xE7] = 0;
            w.Table(Tables.Offices)[0].Bytes[0x37] = 1;
            Assert.False(Objects.Valid(w, w.Table(Tables.Offices)[0]));
        }
        var records = Vectors.Read(world).GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
            p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
        byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
        Assert.Throws<NotSupportedException>(() => setup.Death.ApplyPlayerOrdinary(w.Table(Tables.Officers)[86], 0, true, setup.Notice));
        Assert.Equal(0, setup.Calls());
        foreach (var (table, rows) in records)
            for (int i = 0; i < rows.Length; i++) Assert.Equal(rows[i], w.Table(table)[i].Bytes);
        Assert.Equal(state, w.WorldState.Bytes); Assert.Equal(settings, w.Settings.Bytes);
    }

    private static (World World, OfficerDeath Death, OfficerDeathNotice Notice, Action Verify, Func<int> Calls)
        Prepare(JsonElement snapshot, string world, JsonElement c)
    {
        World w = Vectors.LoadWorld(world);
        JsonElement f = c.GetProperty("fixture"), delta = c.GetProperty("after");
        Vectors.Poke(w, f.GetProperty("pokes"));
        var expected = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
            p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
        Assert.Equal(32, expected.Count);
        foreach (JsonElement row in delta.GetProperty("records").EnumerateArray())
            expected[Convert.ToInt32(row[0].GetString()!, 16)][row[1].GetInt32()] = Convert.FromHexString(row[2].GetString()!);
        byte[] state = delta.TryGetProperty("worldstate", out var nextState) ? Convert.FromHexString(nextState.GetString()!) : w.WorldState.Bytes.ToArray();
        byte[] settings = delta.TryGetProperty("settings", out var nextSettings) ? Convert.FromHexString(nextSettings.GetString()!) : w.Settings.Bytes.ToArray();
        var vectors = expected.Keys.SelectMany(t => w.Table(t)).ToDictionary(r => r,
            r => new[] { r.Vector40, r.Vector50, r.Vector70, r.Vector98, r.VectorA0, r.VectorA8, r.VectorC8, r.Vector1D0 }.Select(v => v?.ToArray()).ToArray());
        JsonElement Final(string key) => delta.TryGetProperty(key, out var value) ? value : snapshot.GetProperty(key);
        var lists = Final("lists").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
            p => p.Value.ValueKind == JsonValueKind.Object ? p.Value.GetProperty("items") : p.Value);
        Rec? Ref(JsonElement r) => r.ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(r[0].GetString()!, 16))[r[1].GetInt32()];
        var pointers = expected.Keys.SelectMany(t => w.Table(t)).ToDictionary(r => r.NativeAddress);
        var nested = snapshot.GetProperty("nested").EnumerateObject().Where(p => !p.Name.StartsWith("force:") && !p.Name.StartsWith("registered:"))
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => Address(e.GetString()!) is var a && a != 0 ? pointers[a] : (Rec?)null).ToList());
        var corpsCache = snapshot.GetProperty("corps_cache").EnumerateObject().ToDictionary(p => Address(p.Name), p => Address(p.Value.GetString()!));
        var deferred = snapshot.GetProperty("nested").EnumerateObject().Where(p => p.Name.StartsWith("force:") && p.Name.EndsWith(":260"))
            .ToDictionary(p => int.Parse(p.Name.Split(':')[1]), p => p.Value.EnumerateArray().Select(e => (Rec?)pointers[Address(e.GetString()!)]).ToList());
        var commands = ReturnCommandTests.Commands(w, snapshot);
        var storage = ReturnCommandTests.Storage(w, snapshot, commands,
            _ => Assert.Fail("This player death does not detach a command"), _ => Assert.Fail("This player death does not dispose a command"));
        var corps = new CorpsLifecycle(w, corpsCache, nested.Where(p => p.Key.StartsWith("caution:"))
            .ToDictionary(p => int.Parse(p.Key[8..]), p => p.Value), () => throw new InvalidOperationException("No corps pool changes"));
        var freeOfficers = snapshot.TryGetProperty("free_officers", out var freeInput)
            ? freeInput.EnumerateArray().Select(e => (Rec?)pointers[Address(e.GetString()!)]).ToList() : [];
        var nameBuffers = new Dictionary<ulong, byte[]>();
        var nameBases = new Dictionary<ulong, ulong>();
        if (c.TryGetProperty("buffer", out JsonElement buffer))
        {
            ulong pointer = Address(buffer.GetProperty("pointer").GetString()!);
            nameBuffers[pointer] = Convert.FromHexString(buffer.GetProperty("data").GetString()!);
            nameBases[pointer] = Address(buffer.GetProperty("allocation").GetString()!);
        }
        var names = new OfficerNameStorage(nameBuffers, nameBases);
        var officers = new OfficerLifecycle(w, corps, storage, deferred, freeOfficers, names);
        var prisoners = new PrisonerRelease(w, officers, storage, () => throw new InvalidOperationException("No return allocation"), _ => Assert.Fail("No return refresh"));
        var personalities = new UnitPersonalityLifecycle(w, nested, false, _ => Assert.Fail("No unit refresh"));
        var destruction = new UnitDestruction(w, personalities, prisoners, officers, false, _ => Assert.Fail("No unit refresh"),
            (_, _) => Assert.Fail("No morale effect"), _ => Assert.Fail("No difficulty log"), _ => Assert.Fail("No recursive unit route"));
        JsonElement[] requests = c.GetProperty("events").EnumerateArray().ToArray();
        Assert.DoesNotContain(requests, e => e.GetProperty("kind").GetString() == "script");
        Assert.Contains(requests, e => e.GetProperty("kind").GetString() == "player_slots");
        var events = new Queue<JsonElement>(requests.Where(e => e.GetProperty("kind").GetString() != "player_slots"));
        int calls = 0;
        JsonElement Boundary(string kind)
        {
            calls++;
            Assert.True(events.Count != 0, $"Unexpected {kind}");
            JsonElement e = events.Dequeue(); Assert.Equal(kind, e.GetProperty("kind").GetString()); return e;
        }
        var rng = new Rng(BitConverter.ToInt32(Convert.FromHexString(snapshot.GetProperty("rng").GetString()!)));
        var messages = new Dictionary<int, string>();
        JsonElement? historyEvent = requests.Where(e => e.GetProperty("kind").GetString() == "history").Select(e => (JsonElement?)e).SingleOrDefault();
        if (historyEvent is { } h)
        {
            int mode = f.GetProperty("mode").GetInt32();
            messages[mode switch { 1 => 0x2A30, 2 => 0x2A31, _ => 0x2A2E }] = Text(h.GetProperty("texts")[0]);
            messages[mode switch { 1 => 0x52AB, 2 => 0x52C6, _ => 0x528F }] = Text(h.GetProperty("texts")[1]);
        }
        void Draws()
        {
            while (events.TryPeek(out JsonElement e) && e.GetProperty("kind").GetString() == "presentation_rng")
            {
                events.Dequeue(); Assert.Equal(e.GetProperty("before").GetInt32(), rng.State);
                rng.Next(e.GetProperty("n").GetInt32());
                BinaryPrimitives.WriteInt32LittleEndian(w.Settings.Bytes.AsSpan(Rng.StateRva - snapshot.GetProperty("exe_settings_rva").GetInt32()), rng.State);
            }
        }
        void Bind(string kind, int value, int slot)
        {
            JsonElement e = Boundary(kind);
            Assert.Equal(value, e.GetProperty(kind == "force" ? "index" : kind == "officer" ? "id" : "value").GetInt32());
            Assert.Equal(slot, e.GetProperty("slot").GetInt32());
        }
        var presentation = new OfficerDeathPresentation(() => Boundary("reset"), (value, slot) => Bind("force", value, slot),
            (value, slot) => Bind("officer", value, slot), (text, slot) =>
            {
                JsonElement e = Boundary("text"); Assert.Equal(Text(e.GetProperty("text")), text); Assert.Equal(slot, e.GetProperty("slot").GetInt32());
            }, (value, slot) => Bind("number", value, slot), _ => throw new InvalidOperationException("This fixture has no office name"), id =>
            {
                Assert.Equal(id, Boundary("message").GetProperty("id").GetInt32()); Draws();
                return messages.TryGetValue(id, out string? text) ? text : $"host message {id:X}";
            }, value => Assert.Equal(value, Boundary("sound").GetProperty("value").GetInt32()), text =>
            {
                Assert.Equal("host message 2A2D", text); Boundary("announcement"); Draws();
            });
        var history = new HistoryLog(w, w.Table(Tables.History).Cast<Rec?>().ToArray(), false, true, force =>
        {
            JsonElement input = snapshot.GetProperty("force_initial_input");
            Assert.Equal(force.Index, input.GetProperty("force").GetInt32()); Assert.Equal(force.U8(0x48), input.GetProperty("title").GetInt32());
            return Text(input.GetProperty("text"));
        }, record =>
        {
            JsonElement e = Boundary("history");
            Rec person = w.Table(Tables.Officers)[f.GetProperty("person").GetInt32()], force = Objects.ForceOf(w, person);
            int speaker = w.OfficerById(force.U16(0x52))!.U16(0x10);
            Assert.Equal(new[] { 1, CorpsLifecycle.MainCorps(w, force).Index, CommandTravel.OfficerLocation(w, storage, person)!.Index,
                0, w.WorldState.I32(0x450), speaker, 1, 1, 0, 0 }, e.GetProperty("args").EnumerateArray().Select(a => a.GetInt32()));
            Assert.Equal(expected[Tables.History][record.Index], record.Bytes);
            Assert.Equal(record.Index, Boundary("history_refresh").GetProperty("index").GetInt32());
        });
        var notice = new OfficerDeathNotice(w, history, presentation, snapshot.GetProperty("playback_input").GetProperty("state").GetInt32() == 0,
            requests.Any(e => e.GetProperty("kind").GetString() == "player_slots_refresh"), () => Boundary("player_slots_refresh"));
        var death = new OfficerDeath(w, officers, storage, destruction);
        void Verify()
        {
            Assert.Empty(events);
            foreach (var (table, rows) in expected)
                for (int i = 0; i < rows.Length; i++)
                    Assert.True(rows[i].AsSpan().SequenceEqual(w.Table(table)[i].Bytes), $"{f.GetProperty("name").GetString()}: {table:X}[{i}]");
            foreach (var (record, data) in vectors)
                Assert.Equal(data, new[] { record.Vector40, record.Vector50, record.Vector70, record.Vector98, record.VectorA0, record.VectorA8, record.VectorC8, record.Vector1D0 });
            Assert.Equal(state, w.WorldState.Bytes); Assert.Equal(settings, w.Settings.Bytes);
            Assert.Empty(nameBuffers);
            Assert.Equal(c.TryGetProperty("frees", out var frees) ? frees.EnumerateArray().Select(e => Address(e.GetString()!)) : [], names.Released);
            Assert.Equal(delta.TryGetProperty("free_officers", out var freeAfter) ? freeAfter.EnumerateArray().Select(e => Address(e.GetString()!)) :
                snapshot.TryGetProperty("free_officers", out var freeBefore) ? freeBefore.EnumerateArray().Select(e => Address(e.GetString()!)) : [],
                freeOfficers.Select(r => r!.NativeAddress));
            if (snapshot.TryGetProperty("free_manager", out var managerInput))
            {
                byte[] before = Convert.FromHexString(managerInput.GetString()!), after = delta.TryGetProperty("free_manager", out var managerAfter)
                    ? Convert.FromHexString(managerAfter.GetString()!) : before;
                Assert.Equal(before.AsSpan(8, 4).ToArray(), after.AsSpan(8, 4).ToArray());
            }
            foreach (var (head, data) in lists)
                Assert.Equal(data.EnumerateArray().Select(n => n.EnumerateArray().Select(Ref).ToArray()), w.List(head));
            Assert.Equal(Final("rng").GetString(), Convert.ToHexString(BitConverter.GetBytes(rng.State)).ToLowerInvariant());
            foreach (var (key, members) in nested)
                Assert.Equal(Final("nested").GetProperty(key).EnumerateArray().Select(e => Address(e.GetString()!)), members.Select(r => r?.NativeAddress ?? 0));
            foreach (var (force, members) in deferred)
                Assert.Equal(Final("nested").GetProperty($"force:{force}:260").EnumerateArray().Select(e => Address(e.GetString()!)), members.Select(r => r?.NativeAddress ?? 0));
            Assert.Equal(Final("corps_cache").EnumerateObject().Select(p => (Address(p.Name), Address(p.Value.GetString()!))).Order(),
                corpsCache.Select(p => (p.Key, p.Value)).Order());
            JsonElement store = Final("command_storage");
            Assert.Equal(store.GetProperty("free_head").GetUInt16(), storage.FreeHead);
            Assert.Equal(store.GetProperty("free_ids").EnumerateArray().Select(e => e.GetUInt16()), storage.FreeIds);
            Assert.Equal(store.GetProperty("by_id").EnumerateObject().Select(p => (int.Parse(p.Name), Address(p.Value.GetString()!))).Order(),
                storage.ById.Select(p => (p.Key, p.Value.Data.NativeAddress)).Order());
            Assert.Equal(store.GetProperty("active").EnumerateArray().Select(e => Address(e.GetString()!)), storage.Active.Select(c => c.Data.NativeAddress));
            Assert.Equal(store.GetProperty("pending").EnumerateArray().Select(e => Address(e.GetString()!)), storage.Pending.Select(c => c.Data.NativeAddress));
            Assert.Equal(Final("cmdactions").EnumerateArray().Select(e => (e.GetProperty("kind").GetInt32(), string.Join(",", e.GetProperty("officers").EnumerateArray().Select(n => n.GetInt32())))),
                w.CmdActions.Select(e => (e.Kind, string.Join(",", e.Officers))));
            foreach (var command in commands)
            {
                JsonElement input = snapshot.GetProperty("commands").EnumerateArray().Single(e => Address(e.GetProperty("address").GetString()!) == command.Data.NativeAddress);
                Assert.Equal(Convert.FromHexString(input.GetProperty("data").GetString()!), command.Data.Bytes);
                Assert.Equal(input.GetProperty("officers").EnumerateArray().Select(e => e.GetInt32()), command.Officers);
            }
            Assert.All(delta.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "records", "worldstate", "settings", "rng", "lists", "nested", "free_officers", "free_manager" }));
            foreach (JsonProperty flag in snapshot.GetProperty("flags").EnumerateObject()) Assert.Equal(flag.Value.GetInt32(), w.Value(flag.Name));
        }
        return (w, death, notice, Verify, () => calls);
    }
}

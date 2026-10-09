using System.Buffers.Binary;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ForceEventsTests
{
    private static string Utf16(string hex)
    {
        byte[] data = Convert.FromHexString(hex);
        return new string(Enumerable.Range(0, data.Length / 2)
            .Select(i => (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2))).ToArray());
    }

    [Fact]
    public void WholeForceEventsMatchHistoryRelationsAndOrderedHostRequests() =>
        Verify("force_events.json.gz", "cases", 14);

    [Fact]
    public void WholeVictoryMatchesPlayerAndNpcSpeakerRequestsInItsOwnScene() =>
        Verify("force_events_speakers.json.gz", "cases", 2);

    [Fact]
    public void ScenarioGuardsMatchWholeNativeIncluding719720AndFutureDates() =>
        Verify("force_events.json.gz", "scenario_cases", 14);

    private static void Verify(string file, string group, int count)
    {
        JsonElement root = Vectors.Read(file);
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.False(root.GetProperty("allocator_stubs").GetBoolean());
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        int cases = 0;
        foreach (JsonElement c in root.GetProperty(group).EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture"), delta = c.GetProperty("after");
            string name = f.GetProperty("name").GetString()!;
            World w = Vectors.LoadWorld(world);
            Vectors.Poke(w, f.GetProperty("pokes"));
            Assert.All(delta.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "records", "worldstate", "settings", "rng" }));
            var expected = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
            var vectors = expected.Keys.SelectMany(t => w.Table(t)).ToDictionary(r => r,
                r => new[] { r.Vector40, r.Vector50, r.Vector70, r.Vector98, r.VectorA0, r.VectorA8, r.VectorC8, r.Vector1D0 }
                    .Select(v => v?.ToArray()).ToArray());
            foreach (JsonElement r in delta.GetProperty("records").EnumerateArray())
                expected[Convert.ToInt32(r[0].GetString()!, 16)][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
            byte[] expectedState = delta.TryGetProperty("worldstate", out var state) ? Convert.FromHexString(state.GetString()!) : w.WorldState.Bytes.ToArray();
            byte[] settings = w.Settings.Bytes.ToArray();
            byte[] expectedSettings = delta.TryGetProperty("settings", out var changedSettings) ? Convert.FromHexString(changedSettings.GetString()!) : settings;
            var rng = new Rng(BitConverter.ToInt32(Convert.FromHexString(snapshot.GetProperty("rng").GetString()!)));
            var lists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.List(Convert.ToInt32(p.Name, 16)).ToArray());
            var flags = snapshot.GetProperty("flags").EnumerateObject().ToDictionary(p => p.Name, p => w.Value(p.Name));
            JsonElement[] requests = c.GetProperty("events").EnumerateArray().ToArray();
            Queue<JsonElement> events = new(requests);
            JsonElement Boundary(string kind)
            {
                Assert.True(events.Count != 0, $"{name}: unexpected {kind}");
                JsonElement e = events.Dequeue();
                Assert.Equal(kind, e.GetProperty("kind").GetString());
                return e;
            }
            void HostDraws()
            {
                // Localization selects a message variant using the shared native RNG before speaker presentation.
                while (events.TryPeek(out JsonElement draw) && draw.GetProperty("kind").GetString() == "presentation_rng")
                {
                    events.Dequeue();
                    Assert.Equal(draw.GetProperty("before").GetInt32(), rng.State);
                    rng.Next(draw.GetProperty("n").GetInt32());
                    int offset = Rng.StateRva - snapshot.GetProperty("exe_settings_rva").GetInt32();
                    BinaryPrimitives.WriteInt32LittleEndian(w.Settings.Bytes.AsSpan(offset), rng.State);
                }
            }
            if (group == "scenario_cases") ForceEvents.UpdateVictoryScenario(w);
            else
            {
                // Native history strings are localization inputs; announcements exercise independently supplied host text.
                var messages = new Dictionary<int, string>();
                var selected = new List<int>();
                foreach (JsonElement e in requests)
                {
                    if (e.GetProperty("kind").GetString() == "message") selected.Add(e.GetProperty("id").GetInt32());
                    if (e.GetProperty("kind").GetString() == "history")
                    {
                        Assert.True(selected.Count >= 2);
                        messages[selected[^2]] = Utf16(e.GetProperty("texts")[0].GetString()!);
                        messages[selected[^1]] = Utf16(e.GetProperty("texts")[1].GetString()!);
                    }
                }
                string Message(int id)
                {
                    Assert.Equal(id, Boundary("message").GetProperty("id").GetInt32());
                    HostDraws();
                    return messages.TryGetValue(id, out string? text) ? text : $"host message {id:X}";
                }
                var presentation = new ForceEventPresentation(() => Boundary("reset"), (force, slot) =>
                {
                    JsonElement e = Boundary("force");
                    Assert.Equal(e.GetProperty("index").GetInt32(), force);
                    Assert.Equal(e.GetProperty("slot").GetInt32(), slot);
                }, (defeated, central) =>
                {
                    Assert.Equal(new[] { defeated, central, 0, 0, 0 },
                        Boundary("pair").GetProperty("args").EnumerateArray().Select(e => e.GetInt32()).ToArray());
                }, Message, sound => Assert.Equal(sound, Boundary("sound").GetProperty("value").GetInt32()), (text, speaker) =>
                {
                    int message = speaker is null ? (name == "leader_defeated" ? 0x3204 : 0x3207)
                        : (Objects.IsPlayerForce(w, w.Entry(Tables.Forces, w.WorldState.U8(0x1657), 51)) ? 0x3215 : 0x321A);
                    Assert.Equal($"host message {message:X}", text);
                    JsonElement e = Boundary(speaker is null ? "announcement" : "speaker");
                    if (speaker is not null) Assert.Equal(e.GetProperty("id").GetInt32(), speaker);
                    HostDraws();
                });
                var history = new HistoryLog(w, w.Table(Tables.History).Cast<Rec?>().ToArray(), false,
                    requests.Any(e => e.GetProperty("kind").GetString() == "history_refresh"), force =>
                    {
                        Assert.Equal(0, force.Index);
                        Assert.False(Objects.ForceValid(force));
                        return ""; // 0x2ACD50 returns an empty abbreviation for an invalid force.
                    }, record =>
                    {
                        JsonElement e = Boundary("history");
                        Assert.Equal(new[] { 1, 0, 0, 1, w.WorldState.I32(0x450), 5012, 0, 0, 0, 0 },
                            e.GetProperty("args").EnumerateArray().Select(v => v.GetInt32()).ToArray());
                        Assert.Equal(expected[Tables.History][record.Index], record.Bytes);
                        Assert.Equal(record.Index, Boundary("history_refresh").GetProperty("index").GetInt32());
                    });
                Rec profile = new(0, Convert.FromHexString(snapshot.GetProperty("profile").GetString()!));
                byte[] bits = Convert.FromHexString(snapshot.GetProperty("county_bits").GetString()!);
                byte[] originalBits = bits.ToArray();
                var achievements = new ProfileAchievements(w, profile,
                    snapshot.GetProperty("achievement_excluded_bits").EnumerateArray().Select(e => e.GetUInt32()).ToArray(),
                    snapshot.GetProperty("notification_available").GetBoolean(),
                    () => throw new InvalidOperationException("No clock in these event fixtures"), _ => Assert.Fail("Unexpected profile notification"));
                var ownership = new TileOwnership(w, achievements, snapshot.GetProperty("map_repaint_available").GetBoolean(),
                    () => Assert.Fail("No scenery repaint in these event fixtures"));
                var transfer = new TileTransfer(w, ownership, bits, () => Assert.Fail("No territory transfer in these fixtures"),
                    (_, _, _) => Assert.Fail("Unexpected unit notification"), (_, _) => Assert.Fail("Unexpected county announcement"),
                    (_, _, _) => Assert.Fail("Unexpected territory effect"));
                var coalition = new ForceCoalition(w, ownership, transfer, snapshot.GetProperty("coalition_days").GetInt32(),
                    snapshot.GetProperty("coalition_refresh_available").GetBoolean(), () => Boundary("coalition_refresh"));
                var body = new ForceEvents(w, coalition, history, presentation, f.GetProperty("gain").GetInt32());
                if (f.GetProperty("rva").GetInt32() == 0x277140)
                    body.LeaderDefeated(w.Entry(Tables.Forces, f.GetProperty("defeated").GetInt32(), 51));
                else body.TargetDefeated(w.Entry(Tables.Forces, f.GetProperty("defeated").GetInt32(), 51),
                    w.Entry(Tables.Forces, f.GetProperty("victor").GetInt32(), 51));
                Assert.Equal(originalBits, bits);
                Assert.Equal(Convert.FromHexString(snapshot.GetProperty("profile").GetString()!), profile.Bytes);
            }
            Assert.Empty(events);
            foreach (var (table, rows) in expected)
                for (int i = 0; i < rows.Length; i++)
                    Assert.True(rows[i].AsSpan().SequenceEqual(w.Table(table)[i].Bytes), $"{name}: {table:X}[{i}]");
            foreach (var (record, data) in vectors)
                Assert.Equal(data, new[] { record.Vector40, record.Vector50, record.Vector70, record.Vector98, record.VectorA0, record.VectorA8, record.VectorC8, record.Vector1D0 });
            Assert.Equal(expectedState, w.WorldState.Bytes);
            Assert.Equal(expectedSettings, w.Settings.Bytes);
            int expectedRng = BitConverter.ToInt32(Convert.FromHexString(delta.TryGetProperty("rng", out var nextRng)
                ? nextRng.GetString()! : snapshot.GetProperty("rng").GetString()!));
            Assert.Equal(expectedRng, rng.State);
            foreach (var (head, items) in lists) Assert.Equal(items, w.List(head));
            foreach (var (key, value) in flags) Assert.Equal(value, w.Value(key));
            cases++;
        }
        Assert.Equal(count, cases);
    }
}

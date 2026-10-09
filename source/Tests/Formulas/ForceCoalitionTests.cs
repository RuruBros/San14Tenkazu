using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ForceCoalitionTests
{
    [Fact]
    public void NativeCalendarMatchesSignedNarrowArithmeticAndOrderedOverlappingCopies()
    {
        JsonElement root = Vectors.Read("native_calendar.json.gz");
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        int cases = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture");
            byte[] data = Convert.FromHexString(f.GetProperty("data").GetString()!);
            if (f.GetProperty("kind").GetString() == "add") NativeCalendar.AddHours(data, f.GetProperty("hours").GetInt32());
            else NativeCalendar.CopyFive(data.AsSpan(f.GetProperty("source").GetInt32()), data.AsSpan(f.GetProperty("target").GetInt32()));
            Assert.Equal(Convert.FromHexString(c.GetProperty("after").GetString()!), data);
            cases++;
        }
        Assert.Equal(29, cases);
    }

    [Fact]
    public void CoalitionCleanupMatchesWholeNativeZeroModeAndRefreshPhase()
    {
        JsonElement root = Vectors.Read("force_coalition.json.gz");
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.False(root.GetProperty("allocator_stubs").GetBoolean());
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        int cases = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture");
            World w = Vectors.LoadWorld(world);
            Vectors.Poke(w, f.GetProperty("pokes"));
            JsonElement delta = c.GetProperty("after");
            Assert.All(delta.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "records", "worldstate" }));
            var expected = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
            foreach (JsonElement r in delta.GetProperty("records").EnumerateArray())
                expected[Convert.ToInt32(r[0].GetString()!, 16)][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
            byte[] expectedState = delta.TryGetProperty("worldstate", out var state) ? Convert.FromHexString(state.GetString()!) : w.WorldState.Bytes.ToArray();
            byte[] settings = w.Settings.Bytes.ToArray();
            var lists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.List(Convert.ToInt32(p.Name, 16)).ToArray());
            var flags = snapshot.GetProperty("flags").EnumerateObject().ToDictionary(p => p.Name, p => w.Value(p.Name));
            Rec profile = new(0, Convert.FromHexString(snapshot.GetProperty("profile").GetString()!));
            var achievements = new ProfileAchievements(w, profile,
                snapshot.GetProperty("achievement_excluded_bits").EnumerateArray().Select(e => e.GetUInt32()).ToArray(), false,
                () => throw new InvalidOperationException("No clock in these coalition cases"));
            var ownership = new TileOwnership(w, achievements, false, () => Assert.Fail("No scenery repaint in these cases"));
            var transfer = new TileTransfer(w, ownership, Convert.FromHexString(snapshot.GetProperty("county_bits").GetString()!),
                () => Assert.Fail("No territory transfer in these cases"), (_, _, _) => Assert.Fail("No unit notification"),
                (_, _) => Assert.Fail("No county announcement"), (_, _, _) => Assert.Fail("No effect"));
            JsonElement phase = c.GetProperty("refresh_phase");
            int refreshes = 0;
            new ForceCoalition(w, ownership, transfer, f.GetProperty("days").GetInt32(), f.GetProperty("ui_available").GetBoolean(), () =>
            {
                Assert.Equal(expectedState, w.WorldState.Bytes);
                if (phase.ValueKind != JsonValueKind.Null)
                {
                    JsonElement call = phase[refreshes];
                    Assert.Equal(Convert.FromHexString(call.GetProperty("worldstate").GetString()!), w.WorldState.Bytes);
                    int i = 0;
                    foreach (JsonElement record in call.GetProperty("forces").EnumerateArray())
                        Assert.Equal(Convert.FromHexString(record.GetString()!), w.Table(Tables.Forces)[i++].Bytes);
                }
                refreshes++;
            }).ClearWithoutUnitReset();
            Assert.Equal(f.GetProperty("ui_available").GetBoolean() ? 1 : 0, refreshes);
            if (phase.ValueKind != JsonValueKind.Null) Assert.Equal(phase.GetArrayLength(), refreshes);
            foreach (var (table, records) in expected)
                for (int i = 0; i < records.Length; i++)
                    Assert.True(records[i].AsSpan().SequenceEqual(w.Table(table)[i].Bytes),
                        $"{f.GetProperty("name").GetString()}: {table:X}[{i}]");
            Assert.Equal(expectedState, w.WorldState.Bytes);
            Assert.Equal(settings, w.Settings.Bytes);
            Assert.Equal(Convert.FromHexString(snapshot.GetProperty("profile").GetString()!), profile.Bytes);
            foreach (var (head, items) in lists) Assert.Equal(items, w.List(head));
            foreach (var (name, value) in flags) Assert.Equal(value, w.Value(name));
            cases++;
        }
        Assert.Equal(10, cases);
    }

    [Fact]
    public void CoalitionTerritoryRepaintMatchesWholeNativeBodyAndHostRequestOrder()
    {
        JsonElement root = Vectors.Read("force_coalition_repaint.json.gz");
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.False(root.GetProperty("allocator_stubs").GetBoolean());
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world), f = root.GetProperty("fixture"), output = root.GetProperty("output");
        World w = Vectors.LoadWorld(world);
        Vectors.Poke(w, f.GetProperty("pokes"));
        var expected = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
            p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
        JsonElement delta = output.GetProperty("after");
        Assert.All(delta.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "records", "worldstate" }));
        foreach (JsonElement r in delta.GetProperty("records").EnumerateArray())
            expected[Convert.ToInt32(r[0].GetString()!, 16)][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
        var lists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
            p => w.List(Convert.ToInt32(p.Name, 16)).ToArray());
        var flags = snapshot.GetProperty("flags").EnumerateObject().ToDictionary(p => p.Name, p => w.Value(p.Name));
        byte[] settings = w.Settings.Bytes.ToArray();
        byte[] bits = Convert.FromHexString(snapshot.GetProperty("county_bits").GetString()!);
        int managerMark = BitConverter.ToInt32(Convert.FromHexString(snapshot.GetProperty("manager_mark").GetString()!));
        int mapMark = BitConverter.ToInt32(Convert.FromHexString(snapshot.GetProperty("map_repaint_before").GetString()!));
        Rec profile = new(0, Convert.FromHexString(snapshot.GetProperty("profile").GetString()!));
        Rec target = w.Table(Tables.Tiles)[f.GetProperty("tile").GetInt32()];
        Queue<JsonElement> events = new(root.GetProperty("events").EnumerateArray());
        JsonElement Boundary(string kind)
        {
            Assert.NotEmpty(events);
            JsonElement e = events.Dequeue();
            Assert.Equal(kind, e.GetProperty("kind").GetString());
            if (e.TryGetProperty("phase", out var phase))
            {
                Assert.Equal(Convert.FromHexString(phase.GetProperty("worldstate").GetString()!), w.WorldState.Bytes);
                Assert.Equal(Convert.FromHexString(phase.GetProperty("tile").GetString()!), target.Bytes);
                Assert.Equal(Convert.FromHexString(phase.GetProperty("county_bits").GetString()!), bits);
                int i = 0;
                foreach (JsonElement record in phase.GetProperty("forces").EnumerateArray())
                    Assert.Equal(Convert.FromHexString(record.GetString()!), w.Table(Tables.Forces)[i++].Bytes);
            }
            return e;
        }
        var achievements = new ProfileAchievements(w, profile,
            snapshot.GetProperty("achievement_excluded_bits").EnumerateArray().Select(e => e.GetUInt32()).ToArray(),
            snapshot.GetProperty("notification_available").GetBoolean(),
            () => throw new InvalidOperationException("No clock in this repaint case"), _ => Assert.Fail("No profile notification"));
        var ownership = new TileOwnership(w, achievements, snapshot.GetProperty("map_repaint_available").GetBoolean(), () => mapMark = 1);
        var transfer = new TileTransfer(w, ownership, bits, () =>
        {
            Assert.Equal(1, Boundary("mark").GetProperty("value").GetInt32());
            managerMark = 1;
        }, (_, _, _) => Assert.Fail("This case has no matching county/scenery unit notification"),
            (_, _) => Assert.Fail("County announcement is disabled"), (tile, acquired, destination) =>
            {
                Assert.Same(target, tile);
                Assert.Equal(destination, tile.U8(0x14));
                Assert.Equal(5 - (acquired ? 1 : 0), Boundary("effect").GetProperty("mode").GetInt32());
            });
        new ForceCoalition(w, ownership, transfer, f.GetProperty("days").GetInt32(),
            snapshot.GetProperty("coalition_refresh_available").GetBoolean(), () => Boundary("refresh")).ClearWithoutUnitReset();
        Assert.Empty(events);
        foreach (var (table, records) in expected)
            for (int i = 0; i < records.Length; i++) Assert.Equal(records[i], w.Table(table)[i].Bytes);
        Assert.Equal(Convert.FromHexString(delta.GetProperty("worldstate").GetString()!), w.WorldState.Bytes);
        Assert.Equal(Convert.FromHexString(output.GetProperty("county_bits").GetString()!), bits);
        Assert.Equal(BitConverter.ToInt32(Convert.FromHexString(output.GetProperty("manager_mark").GetString()!)), managerMark);
        Assert.Equal(BitConverter.ToInt32(Convert.FromHexString(output.GetProperty("map_mark").GetString()!)), mapMark);
        Assert.Equal(Convert.FromHexString(output.GetProperty("profile").GetString()!), profile.Bytes);
        Assert.Equal(settings, w.Settings.Bytes);
        foreach (var (head, items) in lists) Assert.Equal(items, w.List(head));
        foreach (var (name, value) in flags) Assert.Equal(value, w.Value(name));
    }
}

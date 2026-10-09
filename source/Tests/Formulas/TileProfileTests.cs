using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TileProfileTests
{
    private sealed class NotificationBoundary : Exception;

    [Theory]
    [InlineData("profile_rules.json.gz", "eligible", 36)]
    [InlineData("profile_rules.json.gz", "bit", 12)]
    [InlineData("profile_rules.json.gz", "territory", 8)]
    [InlineData("tile_ownership.json.gz", "tile", 14)]
    public void CopiedProfileAndTileBytesMatchNative(string vector, string kind, int expectedCases)
    {
        JsonElement root = Vectors.Read(vector);
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.False(root.GetProperty("allocator_stubs").GetBoolean());
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        int cases = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture");
            if (f.GetProperty("kind").GetString() != kind) continue;
            World w = Vectors.LoadWorld(world);
            Vectors.Poke(w, f.GetProperty("pokes"));
            var expected = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.Table(Convert.ToInt32(p.Name, 16)).Select(r => r.Bytes.ToArray()).ToArray());
            JsonElement delta = c.GetProperty("after");
            Assert.Equal(["records"], delta.EnumerateObject().Select(p => p.Name));
            foreach (JsonElement r in delta.GetProperty("records").EnumerateArray())
                expected[Convert.ToInt32(r[0].GetString()!, 16)][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
            byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
            var lists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => Convert.ToInt32(p.Name, 16),
                p => w.List(Convert.ToInt32(p.Name, 16)).ToArray());
            var flags = snapshot.GetProperty("flags").EnumerateObject().ToDictionary(p => p.Name, p => w.Value(p.Name));
            Rec profile = new(0, Convert.FromHexString(f.GetProperty("profile").GetString()!));
            Queue<JsonElement> clocks = new(c.GetProperty("clock_tm").EnumerateArray());
            JsonElement[] notifications = c.GetProperty("notifications").EnumerateArray().ToArray();
            int notificationCount = 0;
            var repaintWrites = new List<int>();
            bool complete = c.GetProperty("complete").GetBoolean();
            var achievements = new ProfileAchievements(w, profile,
                f.GetProperty("rules").EnumerateArray().Select(e => e.GetUInt32()).ToArray(),
                snapshot.GetProperty("notification_available").GetBoolean(), () =>
                {
                    Assert.NotEmpty(clocks);
                    byte[] tm = Convert.FromHexString(clocks.Dequeue().GetString()!);
                    Assert.Equal(36, tm.Length);
                    return new(BitConverter.ToInt32(tm, 20), BitConverter.ToInt32(tm, 16),
                        BitConverter.ToInt32(tm, 12), BitConverter.ToInt32(tm, 8));
                }, index =>
                {
                    Assert.True(notificationCount < notifications.Length);
                    JsonElement request = notifications[notificationCount++];
                    Assert.Equal(request.GetProperty("index").GetInt32(), index);
                    Assert.Equal(Convert.FromHexString(request.GetProperty("profile").GetString()!), profile.Bytes);
                    if (!complete) throw new NotificationBoundary();
                });
            int? result = null;
            void Apply()
            {
                switch (kind)
                {
                    case "eligible": result = achievements.CanRecord() ? 1 : 0; break;
                    case "bit": achievements.RecordBit(f.GetProperty("index").GetInt32(),
                        f.GetProperty("enabled").GetInt32(), f.GetProperty("silent").GetInt32()); break;
                    case "territory": result = achievements.Territory(f.GetProperty("require").GetInt32(),
                        f.GetProperty("all").GetInt32(), f.GetProperty("silent").GetInt32()); break;
                    case "tile": new TileOwnership(w, achievements, f.GetProperty("map_available").GetBoolean(),
                        () => repaintWrites.Add(1)).Apply(w.Table(Tables.Tiles)[f.GetProperty("tile").GetInt32()],
                            f.GetProperty("owner").GetInt32()); break;
                    default: Assert.Fail($"Unknown native fixture kind {kind}"); break;
                }
            }
            if (complete) Apply();
            else Assert.Throws<NotificationBoundary>(Apply);
            if (result.HasValue) Assert.Equal(c.GetProperty("result").GetInt32(), result);
            Assert.Empty(clocks);
            Assert.Equal(notifications.Length, notificationCount);
            Assert.Equal(c.GetProperty("repaints").EnumerateArray().Select(e => e.GetInt32()), repaintWrites);
            Assert.Equal(c.GetProperty("map_mark").GetInt32(), repaintWrites.Count == 0 ? 0 : repaintWrites[^1]);
            Assert.Equal(Convert.FromHexString(c.GetProperty("profile").GetString()!), profile.Bytes);
            foreach (var (table, records) in expected)
                for (int i = 0; i < records.Length; i++)
                    Assert.True(records[i].AsSpan().SequenceEqual(w.Table(table)[i].Bytes),
                        $"{f.GetProperty("name").GetString()}: {table:X}[{i}]");
            Assert.Equal(state, w.WorldState.Bytes);
            Assert.Equal(settings, w.Settings.Bytes);
            foreach (var (head, items) in lists) Assert.Equal(items, w.List(head));
            foreach (var (name, value) in flags) Assert.Equal(value, w.Value(name));
            cases++;
        }
        Assert.Equal(expectedCases, cases);
    }

    [Fact]
    public void MissingNotificationHostFailsBeforeProfileMutation()
    {
        JsonElement root = Vectors.Read("profile_rules.json.gz");
        JsonElement f = root.GetProperty("cases").EnumerateArray().Single(c =>
            c.GetProperty("fixture").GetProperty("name").GetString() == "notify_before_first_date").GetProperty("fixture");
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Vectors.Poke(w, f.GetProperty("pokes"));
        Rec profile = new(0, Convert.FromHexString(f.GetProperty("profile").GetString()!));
        byte[] before = profile.Bytes.ToArray();
        var achievements = new ProfileAchievements(w, profile,
            f.GetProperty("rules").EnumerateArray().Select(e => e.GetUInt32()).ToArray(), true,
            () => throw new InvalidOperationException("Clock must not be read"));
        Assert.Throws<NotSupportedException>(() => achievements.RecordBit(16, 1, 0));
        Assert.Equal(before, profile.Bytes);
    }
}

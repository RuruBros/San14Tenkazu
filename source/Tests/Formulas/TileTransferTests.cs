using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class TileTransferTests
{
    [Fact]
    public void WholeTransferWithoutPresentationMatchesNativeRecordsAndDirtyStorage()
    {
        JsonElement root = Vectors.Read("tile_transfer.json.gz");
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
            Rec tile = w.Table(Tables.Tiles)[f.GetProperty("tile").GetInt32()];
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
            Rec profile = new(0, Convert.FromHexString(snapshot.GetProperty("profile").GetString()!));
            byte[] bits = Convert.FromHexString(snapshot.GetProperty("county_bits").GetString()!);
            int managerMark = BitConverter.ToInt32(Convert.FromHexString(snapshot.GetProperty("manager_mark").GetString()!));
            int mapMark = BitConverter.ToInt32(Convert.FromHexString(snapshot.GetProperty("map_mark").GetString()!));
            if (f.GetProperty("clear_dirty").GetBoolean())
            {
                bits[tile.U16(0x12) >> 3] &= (byte)~(1 << (tile.U16(0x12) & 7));
                managerMark = 0;
            }
            Rec counter = new(0, BitConverter.GetBytes(f.GetProperty("counter").GetInt32()));
            var achievements = new ProfileAchievements(w, profile,
                snapshot.GetProperty("achievement_excluded_bits").EnumerateArray().Select(e => e.GetUInt32()).ToArray(),
                snapshot.GetProperty("notification_available").GetBoolean(),
                () => throw new InvalidOperationException("These fixtures cannot read the profile clock"),
                _ => Assert.Fail("These fixtures cannot request a profile notification"));
            var ownership = new TileOwnership(w, achievements, snapshot.GetProperty("map_repaint_available").GetBoolean(),
                () => mapMark = 1);
            int marks = 0;
            var transfer = new TileTransfer(w, ownership, bits, () => { marks++; managerMark = 1; },
                (_, _, _) => Assert.Fail("No matching city unit notification in these native cases"),
                (_, _) => Assert.Fail("County announcements are explicitly disabled"),
                (_, _, _) => Assert.Fail("Visual effects are explicitly disabled"), counter);
            int result = transfer.Apply(tile, f.GetProperty("destination").GetInt32(), f.GetProperty("city").GetInt32(),
                f.GetProperty("visuals").GetInt32(), f.GetProperty("announce").GetInt32(), f.GetProperty("override").GetInt32());
            Assert.Equal(c.GetProperty("result").GetInt32(), result);
            Assert.Equal(result, marks);
            Assert.Empty(c.GetProperty("presentation").EnumerateArray());
            Assert.Equal(Convert.FromHexString(c.GetProperty("county_bits").GetString()!), bits);
            Assert.Equal(BitConverter.ToInt32(Convert.FromHexString(c.GetProperty("manager_mark").GetString()!)), managerMark);
            Assert.Equal(BitConverter.ToInt32(Convert.FromHexString(c.GetProperty("map_mark").GetString()!)), mapMark);
            Assert.Equal(c.GetProperty("counter").GetInt32(), counter.I32(0));
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
        Assert.Equal(38, cases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingDirtyWindowOrChallengeCounterFailsBeforeTileMutation(bool challenge)
    {
        JsonElement root = Vectors.Read("tile_transfer.json.gz");
        JsonElement f = root.GetProperty("cases")[0].GetProperty("fixture");
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        World w = Vectors.LoadWorld(world);
        Vectors.Poke(w, f.GetProperty("pokes"));
        if (challenge) w.SetValue("challenge_active", 1);
        Rec tile = w.Table(Tables.Tiles)[f.GetProperty("tile").GetInt32()];
        byte[] before = tile.Bytes.ToArray();
        Rec profile = new(0, Convert.FromHexString(snapshot.GetProperty("profile").GetString()!));
        var achievements = new ProfileAchievements(w, profile,
            snapshot.GetProperty("achievement_excluded_bits").EnumerateArray().Select(e => e.GetUInt32()).ToArray(), false,
            () => throw new InvalidOperationException("Clock must not be read"));
        var ownership = new TileOwnership(w, achievements, false, () => Assert.Fail("No repaint"));
        var transfer = new TileTransfer(w, ownership, challenge ? new byte[0x2000] : [],
            () => Assert.Fail("No dirty mark before preflight"), (_, _, _) => Assert.Fail("No unit notification"),
            (_, _) => Assert.Fail("No announcement"), (_, _, _) => Assert.Fail("No visual effect"));
        if (challenge) Assert.Throws<NotSupportedException>(() => transfer.Apply(tile, 0, 0, 0, 0, 0));
        else Assert.Throws<InvalidDataException>(() => transfer.Apply(tile, 0, 0, 0, 0, 0));
        Assert.Equal(before, tile.Bytes);
    }
}

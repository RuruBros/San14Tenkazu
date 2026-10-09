namespace San14Tenkazu.Tests;

// The running game's tables are read only. Retained snapshots model editor cards, pending edits and map workers.
public class SnapshotLiveReadTests
{
    [LiveGameFact]
    public void RepeatedAndConcurrentReadsPreserveRetainedSnapshotData()
    {
        GameIdentity identity = GameMemory.FindGame();
        GameLayout layout = GameMemory.Resolve(identity, null, CancellationToken.None);
        GameSnapshot first = GameMemory.ReadSnapshot(identity, layout);
        byte[][] records = first.Cities.Select(row => row.Data)
            .Concat(first.Counties.Select(row => row.Data)).Concat(first.Officers.Select(row => row.Data))
            .Concat(first.Units.Select(row => row.Data)).Concat(first.ForceRecords.Values.Select(row => row.Data))
            .Concat(first.CastleByCity.Values.Select(row => row.Data)).Concat(first.Gates.Select(row => row.Data))
            .Concat(first.Facilities.Select(row => row.Building?.Data).OfType<byte[]>())
            .Concat(first.Items.Select(row => row.Data)).Concat(first.Personalities.Select(row => row.Data))
            .Append(first.TileOwners).ToArray();
        byte[][] expected = records.Select(data => (byte[])data.Clone()).ToArray();
        ushort[] expectedCounties = (ushort[])first.TileCounties.Clone();

        GameSnapshot latest = first;
        for (int i = 0; i < 16; i++) latest = GameMemory.ReadSnapshot(identity, layout, latest);
        Parallel.For(0, 8, _ =>
        {
            GameSnapshot data = GameMemory.ReadSnapshot(identity, layout, first);
            Assert.Equal(GameMemory.TileCount, data.TileOwners.Length);
            Assert.Equal(GameMemory.TileCount, data.TileCounties.Length);
            Assert.All(data.Cities, row => Assert.Equal(GameMemory.CityStride, row.Data.Length));
            Assert.All(data.Officers, row => Assert.Equal(GameMemory.OfficerStride, row.Data.Length));
        });
        for (int i = 0; i < records.Length; i++) Assert.Equal(expected[i], records[i]);
        Assert.Equal(expectedCounties, first.TileCounties);
    }
}

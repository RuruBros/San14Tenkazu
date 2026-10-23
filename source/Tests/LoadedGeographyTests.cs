namespace San14Tenkazu.Tests;

public sealed class LoadedGeographyTests
{
    [Fact]
    public void MissingNameAndCastleDoNotBorrowOriginalTribalMetadata()
    {
        var data = GeographyFixture.Create(51);
        Array.Clear(data.Cities[51].Data, CityFields.NameOffset, 8);
        data.CastleByCity.Remove(51);
        Assert.Equal($"{L.Columns.City} 51", data.CityName(51));
        Assert.Null(data.CityTile(51));
        Assert.True(data.IsCity(51));
        Assert.Single(CityEdits.Plan(data, 51, new([new(CityFields.Gold, 1234)], [], null)).Edits);
        var missing = CityEdits.Plan(data, 51, new([new(CityFields.Gold, 1234), new(CityFields.Durability, 5000)], [], null));
        Assert.True(missing.Refused);
        Assert.Empty(missing.Edits);
        Assert.Equal(L.Compatibility.MissingCastle(data.CityName(51)), missing.Refusal);
    }

    [Fact]
    public void OriginalAndDifferentExpandedWorldsKeepTheirOwnClassificationAndNames()
    {
        var original = GeographyFixture.Create();
        var expanded = GeographyFixture.Create(51);
        var another = GeographyFixture.Create(49);
        Assert.Equal(46, original.CityIds.Count());
        Assert.Equal(5, original.Tribes.Length);
        Assert.Equal(51, expanded.CityIds.Count());
        Assert.Empty(expanded.Tribes);
        Assert.Equal(49, another.CityIds.Count());
        Assert.Equal(2, another.Tribes.Length);
        Assert.False(original.IsCity(51));
        Assert.True(expanded.IsCity(51));
        Assert.Equal("도시51", expanded.BaseName(51));
        Assert.Equal("새군 (도시51)", expanded.PlaceName(new UnitRecord(1, 1, new byte[GameMemory.UnitStride], 1)));
        Assert.Equal(51, expanded.CitiesOf(1).Count());
        Assert.Equal(46, original.CitiesOf(1).Count());
    }

    [Fact]
    public void AddedCityUsesRealRecordInEditingTransfersAndRevival()
    {
        var expanded = GeographyFixture.Create(51);
        var original = GeographyFixture.Create();
        var plan = CityEdits.Plan(expanded, 51, new([new(CityFields.Gold, 1234)], [], null));
        Assert.Single(plan.Edits);
        Assert.Equal(expanded.Cities[51].Address, plan.Edits[0].Address);
        Assert.Equal("도시51", plan.Edits[0].Label);
        Assert.True(BaseGrants.IsBase(expanded, 51));
        Assert.False(BaseGrants.IsBase(original, 51));
        Assert.Contains(Transfers.Targets(expanded).SelectMany(f => f.Bases), b => b.Base == 51 && b.Name == "도시51");
        Assert.DoesNotContain(Transfers.Targets(original).SelectMany(f => f.Bases), b => b.Base == 51);
        Assert.Contains(OfficerRevival.Targets(expanded, RevivalMode.Free), b => b.Base == 51);
        Assert.DoesNotContain(OfficerRevival.Targets(original, RevivalMode.Free), b => b.Base == 51);
        Assert.Equal(1234, expanded.With(plan.Edits).Cities[51][CityFields.Gold]);
    }

    [Fact]
    public void TerrainChangesCopyOnlyOnChangeAndNeverRewriteRetainedSnapshots()
    {
        var tiles = new byte[GameMemory.TileCount * GameMemory.TileStride];
        byte[] first = GameMemory.ReadTerrainMap(tiles, null);
        Assert.Same(first, GameMemory.ReadTerrainMap(tiles, first));
        tiles[(GameMemory.TileCount - 1) * GameMemory.TileStride + 0x10] = 17;
        byte[] second = GameMemory.ReadTerrainMap(tiles, first);
        Assert.NotSame(first, second);
        Assert.Equal(0, first[^1]);
        Assert.Equal(17, second[^1]);
        Assert.Same(second, GameMemory.ReadTerrainMap(tiles, second));
        Assert.Same(second, GeographyFixture.Create(51, terrain: second).With([]).Terrain);
        tiles[0x10] = 255;
        Assert.Throws<InvalidDataException>(() => GameMemory.ReadTerrainMap(tiles, second));
        Assert.Equal(0, second[0]);
        Assert.Throws<ArgumentException>(() => GameMemory.ReadTerrainMap(new byte[32], second));
    }
}

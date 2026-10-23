using System.Buffers.Binary;
using System.Text;

namespace San14Tenkazu.Tests;

internal static class GeographyFixture
{
    internal static GameSnapshot Create(int ordinaryCount = 46, int move = 0, byte[]? terrain = null, CityRecord[]? nativeCities = null)
    {
        var seed = new TestGame().Force(1, 1, 1).Officer(1, "군주", 1, 1, 1).City(1, 1).Build();
        var cities = Enumerable.Range(0, 52).Select(id =>
        {
            byte[] bytes = new byte[GameMemory.CityStride];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, TestGame.Classes.City);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(CityFields.IdOffset), (ushort)id);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(CityFields.CorpsOffset), 1);
            bytes[CityFields.Scale.Offset] = 1;
            bytes[0xCE] = (byte)(id <= ordinaryCount ? 1 : 18 + (id - ordinaryCount - 1) % 5);
            string name = id <= 46 ? CityNames.ById[id] : $"도시{id}";
            Encoding.Unicode.GetBytes(name).CopyTo(bytes, CityFields.NameOffset);
            return new CityRecord(id, (ulong)(0x1000000 + id * 0x1000), bytes);
        }).ToArray();
        if (nativeCities is not null) cities = nativeCities;
        var castles = cities.Skip(1).ToDictionary(c => c.CityId, c =>
        {
            var bytes = new byte[GameMemory.BuildingStride];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, TestGame.Classes.Building);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(GameMemory.BuildingTileOffset), (ushort)(1000 + c.CityId * 11 + move));
            bytes[GameMemory.BuildingTypeOffset] = 1;
            return new BuildingRecord(c.CityId, (ulong)(0x2000000 + c.CityId * 0x1000), bytes);
        });
        var county = new byte[GameMemory.CountyStride];
        county[CountyFields.HomeCityOffset] = county[CountyFields.CurrentCityOffset] = 51;
        Encoding.Unicode.GetBytes("새군").CopyTo(county, CountyFields.NameOffset);
        return new GameSnapshot
        {
            Cities = cities, Tribes = cities.Where(c => c.CityId > 0 && c.IsTribe).ToArray(),
            Counties = [new CountyRecord(1, 0x3000000, county)], CastleByCity = castles,
            TileOwners = new byte[GameMemory.TileCount], TileCounties = new ushort[GameMemory.TileCount],
            Terrain = terrain, Forces = seed.Forces, CorpsForce = seed.CorpsForce,
            ForceRecords = seed.ForceRecords, OfficerById = seed.OfficerById, Officers = seed.Officers, Units = [], Date = seed.Date
        };
    }
}

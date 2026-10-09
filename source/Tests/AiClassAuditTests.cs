using System.Buffers.Binary;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiClassAuditTests
{
    [Fact]
    public void EveryRegisteredClassAndLinearLoaderMatchesNativeConstruction()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiClassAuditVectors.json")));
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.Equal(AiCatalog.Classes.Count, cases.GetArrayLength());
        foreach (JsonElement row in cases.EnumerateArray())
        {
            AiClass cls = Assert.IsType<AiClass>(AiCatalog.Class(row.GetProperty("hash").GetUInt32()));
            Assert.Equal(cls.Name, row.GetProperty("name").GetString());
            Assert.Equal(cls.Leaf, row.GetProperty("kind").GetString() == "leaf");
            Assert.Equal(row.GetProperty("vt").GetInt32(), row.GetProperty("constructedVtable").GetInt32());
            if (!row.TryGetProperty("params", out JsonElement values)) continue;
            byte[] loaded = Convert.FromHexString(row.GetProperty("loaded").GetString()!);
            int fieldCount = row.GetProperty("offsets").EnumerateObject().Count();
            if (fieldCount != values.GetArrayLength()) Assert.Null(cls.Offsets);
            foreach (JsonProperty field in row.GetProperty("offsets").EnumerateObject())
            {
                int index = int.Parse(field.Name), offset = field.Value.GetInt32();
                if (cls.Offsets is not null) Assert.Equal(offset, cls.Offsets[index]);
                Assert.Equal(values[index].GetUInt32(), BinaryPrimitives.ReadUInt32LittleEndian(loaded.AsSpan(offset)));
            }
        }
    }
}

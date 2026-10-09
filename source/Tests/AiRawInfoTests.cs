using System.Buffers.Binary;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiRawInfoTests
{
    [Fact]
    public void UnnamedScalarStorageMatchesNativeNodesIncludingMyself()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiRawInfoVectors.json")));
        foreach (JsonElement row in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = row.GetProperty("class").GetString()!;
            string family = name.Replace("CVariableGet", "").Replace("CGetMyself", "").Replace("InfoNode", "");
            int kind = row.GetProperty("kind").GetInt32();
            var field = AiRawInfo.GetField(family, kind);
            Assert.Equal(row.GetProperty("offset").GetInt32(), field.Offset);
            byte[] record = new byte[0x200];
            uint stored = row.GetProperty("stored").GetUInt32();
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(field.Offset), stored);
            Assert.Equal(row.GetProperty("native").GetInt32(), AiRawInfo.Read(family, kind, record));
        }
    }
}

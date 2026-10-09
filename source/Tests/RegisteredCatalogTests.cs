using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class RegisteredCatalogTests
{
    [Fact]
    public void AvailabilityDoesNotBypassTheOriginalObjectValidityGate()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RegisteredCatalogVectors.json")));
        var rows = source.RootElement.GetProperty("boundaryVectors").EnumerateArray().ToArray();
        Assert.Equal(8, rows.Length);
        foreach (var row in rows)
        {
            int id = row.GetProperty("id").GetInt32();
            byte[] record = Convert.FromHexString(row.GetProperty("record").GetString()!);
            bool personality = row.GetProperty("kind").GetString() == "personality";
            bool actual = personality ? RegisteredOfficerCatalog.Personality(id, record, (_, _) => null)
                : RegisteredOfficerCatalog.Tactic(id, record, (_, _) => null);
            Assert.Equal(row.GetProperty("eligible").GetBoolean(), actual);
        }
    }

    [Fact]
    public void AllCapturedChoicesMatchOriginalNativeEligibilityInstructions()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RegisteredCatalogVectors.json")));
        int count = 0;
        foreach (var row in source.RootElement.GetProperty("vectors").EnumerateArray())
        {
            int id = row.GetProperty("id").GetInt32();
            byte[] record = Convert.FromHexString(row.GetProperty("record").GetString()!);
            byte[] vector = Convert.FromHexString(row.GetProperty("vector").GetString()!);
            bool personality = row.GetProperty("kind").GetString() == "personality";
            byte[]? Read(ulong _, int size) => vector[..size];
            bool actual = personality ? RegisteredOfficerCatalog.Personality(id, record, Read) : RegisteredOfficerCatalog.Tactic(id, record, Read);
            Assert.Equal(row.GetProperty("eligible").GetBoolean(), actual);
            count++;
        }
        Assert.Equal(557, count);
    }

    [Fact]
    public void LegacyAvailabilityFlagsAreReadAndUnreadableEncodedVectorsAreNotOffered()
    {
        var personality = new byte[232]; personality[0xBE] = 1; personality[0xBF] = 16;
        var tactic = new byte[144]; tactic[0x40] = 1; tactic[0x44] = 2;
        byte[]? Missing(ulong _, int __) => null;
        Assert.True(RegisteredOfficerCatalog.Personality(1, personality, Missing));
        Assert.True(RegisteredOfficerCatalog.Tactic(1, tactic, Missing));
        BitConverter.GetBytes((ulong)4).CopyTo(personality, 0xD0);
        BitConverter.GetBytes((ulong)40).CopyTo(tactic, 0x78);
        Assert.False(RegisteredOfficerCatalog.Personality(1, personality, Missing));
        Assert.False(RegisteredOfficerCatalog.Tactic(1, tactic, Missing));
        personality[0xB6] = 1;
        Assert.True(RegisteredOfficerCatalog.Personality(256, personality, Missing)); // native's nonencoded DLC branch
        personality[0xB6] = 0;
        Assert.False(RegisteredOfficerCatalog.Personality(256, personality, Missing)); // native validity precedes availability
    }
}

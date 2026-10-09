using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiDistrictPredicateTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiDistrictPredicateVectors.json")));

    [Fact]
    public void SignedForceAndDistrictFieldsAndUnsignedTroopsPurposeMatchNative()
    {
        using JsonDocument document = Vectors();
        JsonElement cases = document.RootElement.GetProperty("cases").GetProperty("agent");
        Assert.Equal(196, cases.GetArrayLength());
        foreach (JsonElement row in cases.EnumerateArray())
        {
            int stored = row[2].GetInt32(), wanted = row[3].GetInt32();
            bool actual = row[0].GetInt32() switch
            {
                25 => AiDistrictPredicates.ForceGoalIs((sbyte)stored, wanted),
                26 => AiDistrictPredicates.DistrictTypeIs((sbyte)stored, wanted),
                27 => AiDistrictPredicates.TroopsKindIs((ushort)stored, wanted),
                _ => throw new InvalidDataException(),
            };
            Assert.Equal(row[4].GetInt32() != 0, actual);
        }
    }

    [Fact]
    public void EveryPermissionSelectorAndStorageBitMatchNative()
    {
        using JsonDocument document = Vectors();
        JsonElement cases = document.RootElement.GetProperty("cases").GetProperty("flags");
        Assert.Equal(1276, cases.GetArrayLength());
        foreach (JsonElement row in cases.EnumerateArray())
            Assert.Equal(row[3].GetInt32() != 0, AiDistrictPredicates.PermissionAllows(
                row[0].GetByte(), row[1].GetUInt32(), row[2].GetInt32()));
    }

    [Fact]
    public void DirectiveRangeAndOffsetMatchNative()
    {
        using JsonDocument document = Vectors();
        JsonElement cases = document.RootElement.GetProperty("cases").GetProperty("directive");
        Assert.Equal(64, cases.GetArrayLength());
        foreach (JsonElement row in cases.EnumerateArray())
            Assert.Equal(row[2].GetInt32() != 0, AiDistrictPredicates.DirectiveIs(row[0].GetByte(), row[1].GetInt32()));
    }

    [Fact]
    public void SettingFieldOrderAndFallbackToZeroMatchNative()
    {
        using JsonDocument document = Vectors();
        JsonElement cases = document.RootElement.GetProperty("cases").GetProperty("settings");
        Assert.Equal(144, cases.GetArrayLength());
        foreach (JsonElement row in cases.EnumerateArray())
            Assert.Equal(row[5].GetInt32() != 0, AiDistrictPredicates.SettingIs(
                row[0].GetByte(), row[1].GetByte(), row[2].GetByte(), row[3].GetInt32(), row[4].GetInt32()));
    }

    [Fact]
    public void MissingAndNullObjectsNeverPassNativeComparisons()
    {
        using JsonDocument document = Vectors();
        JsonElement cases = document.RootElement.GetProperty("cases").GetProperty("missing");
        Assert.Equal(9, cases.GetArrayLength());
        foreach (JsonElement row in cases.EnumerateArray())
        {
            bool actual = row[0].GetInt32() switch
            {
                25 => AiDistrictPredicates.ForceGoalIs(0, 0, false),
                26 => AiDistrictPredicates.DistrictTypeIs(0, 0, false),
                27 => AiDistrictPredicates.TroopsKindIs(0, 0, false),
                108 => AiDistrictPredicates.PermissionAllows(255, uint.MaxValue, 0, false),
                110 => AiDistrictPredicates.DirectiveIs(1, 0, false),
                111 => AiDistrictPredicates.SettingIs(0, 0, 0, 0, 0, false),
                _ => throw new InvalidDataException(),
            };
            Assert.Equal(row[2].GetInt32() != 0, actual);
            Assert.False(actual);
        }
    }
}

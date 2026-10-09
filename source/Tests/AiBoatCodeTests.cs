using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiBoatCodeTests
{
    [Fact]
    public void NativeWaterGuardAcceptsOnlyFourCodesAndDoesNotValidateOtherTerrainKinds()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AiBoatCodeVectors.json")));
        JsonElement[] rows = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(1820, rows.Length);
        foreach (JsonElement row in rows)
            Assert.Equal(row.GetProperty("native").GetInt32() != 0,
                AiBoatCodes.Allowed(row.GetProperty("terrain").GetInt32(), row.GetProperty("code").GetInt32()));
        Assert.Equal(new[] { 12, 13, 17, 18 }, rows.Where(r => r.GetProperty("terrain").GetInt32() == 24 && r.GetProperty("native").GetInt32() != 0)
            .Select(r => r.GetProperty("code").GetInt32()));
    }
}

using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiInfoAuditTests
{
    [Fact]
    public void AllDocumentedKindsRunWithNativeOutputContracts()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiInfoAuditVectors.json")));
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.Equal(342, cases.GetArrayLength());
        Assert.Equal(18, cases.EnumerateArray().Select(r => S(r, "class")).Distinct().Count());
        foreach (JsonElement row in cases.EnumerateArray())
        {
            string family = S(row, "class").Replace("CVariableGet", "").Replace("CGetMyself", "").Replace("InfoNode", "");
            int kind = I(row, "kind"), status = I(row, "status");
            bool written = row.GetProperty("written").GetBoolean();
            if (S(row, "type") == "-")
                Assert.Equal(AiInfoRules.StatusWithoutOutput(family, kind), status);
            else if (family == "District" && kind is >= 8 and <= 11)
            {
                Assert.Equal(2, status);
                Assert.False(written);
            }
            else Assert.Equal(1, status);

            if (!row.TryGetProperty("referenceRule", out JsonElement rule)) continue;
            Assert.True(written);
            switch (rule.GetString())
            {
                case "copy-list": Assert.Equal(UArray(row, "members"), UArray(row, "native")); break;
                case "valid-list":
                case "selected-list":
                    Assert.Equal(AiInfoRules.SelectList(UArray(row, "members"),
                        row.GetProperty("validity").EnumerateArray().Select(v => v.GetBoolean()).ToArray()), UArray(row, "native"));
                    break;
                case "force-index": Assert.Equal(UArray(row, "forceTable")[AiInfoRules.ForceIndex((byte)I(row, "index"))], row.GetProperty("native").GetUInt64()); break;
                case "pointer": Assert.Equal(row.GetProperty("sourcePointer").GetUInt64(), row.GetProperty("native").GetUInt64()); break;
                case "caution-number": Assert.Equal(I(row, "stored"), I(row, "native")); break;
                default: throw new InvalidOperationException();
            }
        }
        Assert.Equal(30, document.RootElement.GetProperty("contextCases").GetArrayLength());
        foreach (JsonElement row in document.RootElement.GetProperty("contextCases").EnumerateArray())
        {
            Assert.Equal(1, I(row, "status"));
            if (S(row, "rule") == "pointer-alias") Assert.Equal(row.GetProperty("input").GetUInt64(), row.GetProperty("native").GetUInt64());
            if (S(row, "rule") == "force-index") Assert.Equal(UArray(row, "forceTable")[AiInfoRules.ForceIndex((byte)I(row, "index"))], row.GetProperty("native").GetUInt64());
        }
    }
    private static string S(JsonElement row, string name) => row.GetProperty(name).GetString()!;
    private static int I(JsonElement row, string name) => row.GetProperty(name).GetInt32();
    private static ulong[] UArray(JsonElement row, string name) => row.GetProperty(name).EnumerateArray().Select(v => v.GetUInt64()).ToArray();
}

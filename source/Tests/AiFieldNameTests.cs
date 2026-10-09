using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiFieldNameTests
{
    [Fact]
    public void OptionLabelsUseTheFieldsActuallyCopiedIntoTheGameDialog()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Equal(41, root.GetProperty("cases").GetArrayLength());
        foreach (JsonElement identity in root.GetProperty("identities").EnumerateArray())
        {
            int predicate = identity.GetProperty("predicate").GetInt32();
            int relative = AiGameFieldLabels.OptionCopyOffset(identity.GetProperty("stack").GetInt32());
            Assert.Equal(identity.GetProperty("preset").GetInt32(), relative + 8);
            Assert.Contains(identity.GetProperty("label").GetString()!, AiLabels.Predicate(predicate));
            foreach (JsonElement row in Cases(root, "optionCopy").Where(r => r.GetProperty("predicate").GetInt32() == predicate))
            {
                Assert.Equal(relative, row.GetProperty("relative").GetInt32());
                Assert.Equal(row.GetProperty("input").GetInt32(), row.GetProperty("native").GetInt32());
            }
        }
    }

    [Fact]
    public void HanAndTradeLabelsBelongToTheirOwnColumnFormatterRecords()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        ulong exe = root.GetProperty("clockBefore").GetProperty("exe").GetUInt64();
        foreach (JsonElement row in root.GetProperty("metadata").EnumerateArray())
        {
            AiGameColumn column = AiGameColumn.Read(Convert.FromHexString(row.GetProperty("bytes").GetString()!));
            Assert.Equal(row.GetProperty("formatter").GetUInt64(), column.Formatter - exe);
            Assert.Equal(row.GetProperty("labelId").GetUInt32(), column.MessageId);
            if (row.GetProperty("role").GetString() == "han")
                Assert.Contains(row.GetProperty("label").GetString()!, L.Named(AiLabels.InfoTable, "CVariableGetPersonInfoNode.31"));
        }
        foreach (JsonElement row in Cases(root, "hanDisplay"))
            Assert.Equal(row.GetProperty("native").GetInt32(), AiGameFieldLabels.HanDisplayLevel(row.GetProperty("input").GetInt16()));
        foreach (JsonElement row in Cases(root, "tradeDisplay"))
        {
            ushort value = row.GetProperty("input").GetUInt16();
            Assert.Equal(value, row.GetProperty("native").GetUInt16());
            Assert.Equal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), row.GetProperty("text").GetString());
        }
        Assert.Equal(L.Ai.TraitHan, Assert.Single(AiTraits.Fields, f => f.Offset == 0x17A).Label);
    }

    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiFieldNameVectors.json")));
    private static IEnumerable<JsonElement> Cases(JsonElement root, string kind) => root.GetProperty("cases").EnumerateArray()
        .Where(r => r.GetProperty("kind").GetString() == kind);
}

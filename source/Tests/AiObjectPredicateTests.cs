using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiObjectPredicateTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiObjectPredicateVectors.json")));

    [Theory]
    [InlineData(120, 1793)]
    [InlineData(121, 513)]
    [InlineData(146, 261)]
    [InlineData(159, 70)]
    public void FieldSelectionAndDifferentValidityGuardsMatchNative(int predicate, int count)
    {
        using JsonDocument document = Vectors();
        JsonElement[] cases = document.RootElement.GetProperty("cases").EnumerateArray()
            .Where(row => row.GetProperty("predicate").GetInt32() == predicate).ToArray();
        Assert.Equal(count, cases.Length);
        foreach (JsonElement row in cases)
        {
            bool lookup = row.GetProperty("lookup").GetBoolean(), valid = row.GetProperty("valid").GetBoolean();
            bool actual;
            switch (predicate)
            {
                case 120:
                    byte[] rankFlags = Enumerable.Repeat(row.GetProperty("selected").GetByte(), 13).ToArray();
                    rankFlags[0] = row.GetProperty("fallback").GetByte();
                    actual = AiObjectPredicates.RankHasFlag(row.GetProperty("rank").GetByte(), rankFlags,
                        row.GetProperty("selector").GetInt32(), lookup);
                    break;
                case 121:
                    actual = AiObjectPredicates.ForceBit1(row.GetProperty("flags").GetByte(), lookup);
                    break;
                case 146:
                    actual = AiObjectPredicates.ArmyByte140IsZero(row.GetProperty("field").GetByte(), valid, lookup);
                    break;
                case 159:
                    actual = AiObjectPredicates.PersonBit14(row.GetProperty("field").GetUInt16(), valid, lookup);
                    break;
                default: throw new InvalidDataException();
            }
            Assert.Equal(row.GetProperty("native").GetInt32() != 0, actual);
        }
        // Each predicate was also executed on readable records whose native Obj_IsValid returns false.
        Assert.Contains(cases, row => row.GetProperty("lookup").GetBoolean() && !row.GetProperty("valid").GetBoolean());
    }
}

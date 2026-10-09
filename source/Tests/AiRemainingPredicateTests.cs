using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiRemainingPredicateTests
{
    [Fact]
    public void NumericInputsAndSchemeOutcomesMatchNative()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiRemainingPredicateVectors.json")));
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.Equal(12, cases.EnumerateArray().Select(row => row.GetProperty("predicate").GetInt32()).Distinct().Count());
        foreach (JsonElement row in cases.EnumerateArray())
        {
            int predicate = I(row, "predicate");
            bool actual = predicate switch
            {
                30 => AiRemainingPredicates.CommanderHomeIs((ushort)I(row, "value"), (ushort)I(row, "other"), B(row, "valid"), true),
                35 => AiRemainingPredicates.PersonAt((ushort)I(row, "value"), (ushort)I(row, "other"), B(row, "valid"), true),
                45 => AiRemainingPredicates.FormerLordIs((ushort)I(row, "value"), (ushort)I(row, "other"), B(row, "valid"), true),
                83 => AiRemainingPredicates.DateReached(Date(row, "stored"), Date(row, "now"), B(row, "valid")),
                141 => AiRemainingPredicates.InChallenge(B(row, "manager"), B(row, "world"), (sbyte)I(row, "challenge")),
                137 => FakeNews(row),
                _ => AiRemainingPredicates.SchemeOutcome(predicate, I(row, "outcome"), B(row, "playerTarget"), B(row, "prerequisites")),
            };
            Assert.Equal(I(row, "native") != 0, actual);
        }
        foreach (int predicate in new[] { 30, 35, 45 })
            Assert.Contains(cases.EnumerateArray(), row => I(row, "predicate") == predicate && I(row, "native") == 1);
    }

    private static int I(JsonElement row, string name) => row.GetProperty(name).GetInt32();
    private static bool B(JsonElement row, string name) => row.GetProperty(name).GetBoolean();
    private static int Date(JsonElement row, string name)
    {
        int[] v = row.GetProperty(name).EnumerateArray().Select(x => x.GetInt32()).ToArray();
        return AiRemainingPredicates.DateValue((short)v[0], (sbyte)v[1], (sbyte)v[2], (sbyte)v[3]);
    }
    private static bool FakeNews(JsonElement row)
    {
        byte[] v = row.GetProperty("ailments").EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray();
        return AiRemainingPredicates.FakeNewsTarget(v[0], v[1], v[2], (byte)I(row, "byte140"),
            (byte)I(row, "kind"), I(row, "areaCount"), B(row, "valid"), B(row, "valid"));
    }
}

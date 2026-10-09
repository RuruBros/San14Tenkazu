using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiTradeSelectionTests
{
    [Fact]
    public void CandidateFilterAndPriorityOrderMatchNative()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiTradeSelectionVectors.json")));
        Assert.Equal(17, document.RootElement.GetProperty("survey").GetArrayLength());
        foreach (JsonElement row in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (row.GetProperty("kind").GetString() == "selection")
            {
                ulong[] candidates = Pointers(row.GetProperty("candidates"));
                JsonElement[] relations = row.GetProperty("relations").EnumerateArray().ToArray();
                var others = row.GetProperty("otherKinds").EnumerateArray().Select(x => (Pointer: x[0].GetUInt64(), Kind: x[1].GetInt32())).ToArray();
                ulong[] survivors = candidates.Where((pointer, i) => AiTradeRules.CandidateAllowed(
                    relations[i].EnumerateArray().Select(x => x.GetByte()).ToArray(), others.Where(x => x.Pointer != pointer).Select(x => x.Kind))).ToArray();
                Assert.Equal(survivors.Length == 0 ? 2 : 1, row.GetProperty("status").GetInt32());
                if (survivors.Length == 1) Assert.Equal(survivors[0], row.GetProperty("nativeSelected").GetUInt64());
            }
            else
            {
                var kinds = row.GetProperty("countryKinds").EnumerateArray().ToDictionary(x => x[0].GetUInt64(), x => x[1].GetInt32());
                ushort[] values = row.GetProperty("values").EnumerateArray().Select(x => x.GetUInt16()).ToArray();
                bool Before(ulong a, ulong b) => AiTradeRules.Precedes(kinds[a], values[Math.Clamp(kinds[a], 0, 4)], kinds[b], values[Math.Clamp(kinds[b], 0, 4)]);
                var comparer = Comparer<ulong>.Create((a, b) => Before(a, b) ? -1 : Before(b, a) ? 1 : 0);
                Assert.Equal(Pointers(row.GetProperty("input")).Order(comparer), Pointers(row.GetProperty("native")));
                foreach (JsonElement call in row.GetProperty("events").EnumerateArray())
                    Assert.Equal(Before(call.GetProperty("args")[1].GetUInt64(), call.GetProperty("args")[2].GetUInt64()), (call.GetProperty("result").GetUInt32() & 0xFF) != 0);
            }
        }
    }
    private static ulong[] Pointers(JsonElement array) => array.EnumerateArray().Select(x => x.GetUInt64()).ToArray();
}

using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiRelayFaultTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiRelayFaultVectors.json")));
    private static JsonElement[] Items(JsonElement row, string name) => row.GetProperty(name).EnumerateArray().ToArray();

    [Fact]
    public void OneAndTwoRelayInputsMatchTheNativeStateInitialization()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        JsonElement[] completed = Items(root, "cases"), failed = Items(root, "failures");
        Assert.Equal(9, completed.Length);
        Assert.Equal(3, failed.Length);
        foreach (JsonElement row in completed.Concat(failed))
        {
            ulong[] pointers = Items(row, "relayPointers").Select(p => p.GetUInt64()).ToArray();
            Assert.All(pointers, p => Assert.NotEqual(0UL, p));
            var expected = AiExecutorRules.InitialFacilityRelayTargets(pointers);
            JsonElement initialized = Items(row, "frames").Single(f => f.GetProperty("rva").GetUInt32() == 0x66E9B);
            Assert.Equal(expected.First, initialized.GetProperty("firstTarget").GetUInt64());
            Assert.Equal(expected.Second, initialized.GetProperty("secondTarget").GetUInt64());
        }
        Assert.All(completed, row =>
        {
            bool own = row.GetProperty("relation").GetString() == "own";
            Assert.Equal(own ? 1 : 3, row.GetProperty("status").GetInt32());
            if (!own)
            {
                Assert.Equal(2, Items(row, "inputRelays").Length);
                Assert.Equal(new uint[] { 0x671BB, 0x671C7 }, Items(row, "frames")
                    .Where(f => f.GetProperty("rva").GetUInt32() != 0x66E9B).Select(f => f.GetProperty("rva").GetUInt32()));
            }
        });
    }

    [Fact]
    public void SingleHostileRelayFaultsAreLinkedToTheActualNullArgument()
    {
        using JsonDocument document = Vectors();
        foreach (JsonElement row in Items(document.RootElement, "failures"))
        {
            Assert.False(row.TryGetProperty("status", out _));
            Assert.Equal("hostile", row.GetProperty("relation").GetString());
            Assert.Single(Items(row, "inputRelays"));
            Assert.Equal(0x229EA0U, row.GetProperty("faultRva").GetUInt32());
            Assert.Equal(0UL, row.GetProperty("faultRcx").GetUInt64());
            Assert.Contains("0x12", row.GetProperty("error").GetString());
            JsonElement call = Items(row, "frames").Last();
            Assert.Equal(0x671BBU, call.GetProperty("rva").GetUInt32());
            Assert.Equal(0UL, call.GetProperty("rcx").GetUInt64());
            Assert.Equal(0UL, call.GetProperty("secondTarget").GetUInt64());
            Assert.NotEqual(0UL, call.GetProperty("firstTarget").GetUInt64());
            Assert.DoesNotContain(Items(row, "frames"), f => f.GetProperty("rva").GetUInt32() == 0x671C7);
        }
    }
}

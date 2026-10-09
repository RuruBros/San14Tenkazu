using System.Buffers.Binary;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiStateAuditTests
{
    [Fact]
    public void PopulatedRelayPurposeGateAndStateResetMatchNative()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiStateAuditVectors.json")));
        JsonElement rows = document.RootElement.GetProperty("cases");
        Assert.Equal(2, rows.EnumerateArray().Select(r => r.GetProperty("class").GetString()).Distinct().Count());
        Assert.Equal(1, AiExecutorRules.InitialRelayStatus(false, 7));
        foreach (JsonElement row in rows.EnumerateArray())
        {
            int status = row.GetProperty("status").GetInt32();
            byte[] state = Convert.FromHexString(row.GetProperty("stateAfter").GetString()!);
            ushort purpose = row.GetProperty("inputPurpose").GetUInt16();
            int? gate = AiExecutorRules.InitialRelayStatus(true, purpose);
            if (gate.HasValue)
            {
                Assert.Equal(gate.Value, status);
                Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(state.AsSpan(8)));
                Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(state.AsSpan(16)));
                Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(24)));
            }
            else if (status == 3)
            {
                Assert.Equal("CExecuteAreaOccupyNode", row.GetProperty("class").GetString());
                Assert.NotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(state.AsSpan(8)));
                Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(24)));
                Assert.Equal(0, state[28]); // No building phase started in these native frames.
                Assert.Equal(48400U, BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(32)));
            }
            else Assert.Equal(1, status);
        }
        // Failed emulator calls are separate diagnostics, never native answer rows.
        Assert.All(document.RootElement.GetProperty("failures").EnumerateArray(),
            r => Assert.False(r.TryGetProperty("status", out _)));
    }
}

using System.Text.Json;
using System.Buffers.Binary;

namespace San14Tenkazu.Tests;

public sealed class AiRelayPersistentTests
{
    [Fact]
    public void NativeTicksRetainTheSameStateAndRecordActualMovementWithoutClaimingCompletion()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AiRelayPersistentVectors.json")));
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.Equal(13, root.GetProperty("cases").GetArrayLength());
        int ticks = 0, moves = 0, changedHexes = 0;
        foreach (JsonElement trial in root.GetProperty("cases").EnumerateArray())
        {
            string previous = trial.GetProperty("initialState").GetString()!;
            foreach (JsonElement frame in trial.GetProperty("frames").EnumerateArray())
            {
                ticks++;
                Assert.Equal(previous, frame.GetProperty("stateBefore").GetString());
                previous = frame.GetProperty("stateAfter").GetString()!;
                AiRelayState state = AiRelayState.Read(Convert.FromHexString(previous));
                if (frame.GetProperty("status").GetInt32() == 3)
                {
                    Assert.NotEqual(0UL, state.Current);
                    Assert.Equal(1, state.Initialized);
                }
                else Assert.Equal(1, frame.GetProperty("status").GetInt32());
                if (!frame.TryGetProperty("movement", out JsonElement movement)) continue;
                foreach (JsonElement move in movement.EnumerateArray())
                {
                    moves++;
                    byte[] before = Convert.FromHexString(move.GetProperty("before").GetString()!);
                    byte[] after = Convert.FromHexString(move.GetProperty("after").GetString()!);
                    if (BinaryPrimitives.ReadUInt16LittleEndian(before.AsSpan(0x2A)) != BinaryPrimitives.ReadUInt16LittleEndian(after.AsSpan(0x2A))) changedHexes++;
                    Assert.Contains(move.GetProperty("events").EnumerateArray(), e => e.GetProperty("rva").GetUInt32() == 0x3011A0);
                }
            }
        }
        Assert.Equal(172, ticks);
        Assert.Equal(140, moves);
        Assert.Equal(13, changedHexes);
        foreach (JsonElement fault in root.GetProperty("failures").EnumerateArray())
        {
            Assert.False(fault.TryGetProperty("status", out _));
            Assert.Empty(fault.GetProperty("frames").EnumerateArray());
            Assert.Contains("rva 0x229ea0", fault.GetProperty("error").GetString());
        }
        Assert.Equal(3, root.GetProperty("failures").GetArrayLength());
    }
}

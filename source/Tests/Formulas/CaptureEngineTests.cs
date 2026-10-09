using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class CaptureEngineTests
{
    [Fact]
    public void PlayerTribeFieldsAndGateModeOneInputsMatchTheNativeBodies()
    {
        JsonElement root = Vectors.Read("capture_frontier.json.gz");
        Assert.True(root.GetProperty("native_handlers").GetBoolean());
        Assert.Equal(9, root.GetProperty("presentation").GetArrayLength());
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Rec Ref(JsonElement e) => w.Table(Convert.ToInt32(e[0].GetString()![2..], 16))[e[1].GetInt32()];
        int tribes = 0, gates = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture"), expected = c.GetProperty("expected");
            Action undo = Vectors.Poke(w, f.GetProperty("pokes"));
            try
            {
                Rec target = Ref(f.GetProperty("target")), attacker = Ref(f.GetProperty("attacker"));
                if (f.GetProperty("name").GetString() == "player_tribe")
                {
                    CaptureEngine.TribeConquest result = CaptureEngine.PlayerTribeConquest(w, target, attacker)!.Value;
                    Assert.Equal(expected.GetProperty("flags_after").GetUInt32(), result.Flags);
                    Assert.Equal(expected.GetProperty("suzerain").GetInt32(), result.Suzerain);
                    Assert.Equal(f.GetProperty("force").GetInt32(), result.Force.Index);
                    JsonElement forceAfter = c.GetProperty("after").GetProperty("records").EnumerateArray()
                        .Single(r => r[0].GetString() == "0xdf30" && r[1].GetInt32() == result.Force.Index);
                    Assert.Equal(result.Suzerain, Convert.FromHexString(forceAfter[2].GetString()!)[0x194]);
                    byte[] worldAfter = Convert.FromHexString(c.GetProperty("after").GetProperty("worldstate").GetString()!);
                    Assert.Equal(result.Flags, BitConverter.ToUInt32(worldAfter, 0x169C));
                    tribes++;
                }
                else
                {
                    DestructionQueue.EngineCall result = CaptureEngine.ForceRemovalGateRequest(w, target)!.Value;
                    Assert.Equal(DestructionQueue.Handler.BaseCaptured, result.Kind);
                    Assert.Equal(target, result.Target);
                    Assert.Equal(w.Table(Tables.Units)[0], result.AttackerUnit);
                    Assert.Equal(w.Table(Tables.Corps)[0], result.AttackerCorps);
                    Assert.Equal(expected.GetProperty("capture_mode").GetInt32(), result.CaptureMode);
                    Assert.Equal(new[] { target.NativeAddress, result.AttackerCorps!.NativeAddress,
                        result.AttackerUnit!.NativeAddress, (ulong)result.CaptureMode },
                        f.GetProperty("args").EnumerateArray().Select(x => x.GetUInt64()));
                    Assert.False(Objects.Valid(w, result.AttackerUnit));
                    Assert.False(Objects.Valid(w, result.AttackerCorps));
                    // This exclusion is proved by the caller's 52..61 filter, not by executing invalid native cases.
                    foreach (Rec city in w.Table(Tables.Cities).Skip(1))
                        Assert.Null(CaptureEngine.ForceRemovalGateRequest(w, city));
                    gates++;
                }
            }
            finally { undo(); }
        }
        Assert.Equal(1, tribes);
        Assert.Equal(1, gates);
    }
}

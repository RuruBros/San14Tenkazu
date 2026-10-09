using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ResultCombatTests
{
    [Fact]
    public void ZeroMoraleRequestsMatchTheNativeImmediateEngineBoundary()
    {
        JsonElement root = Vectors.Read("result_zero.json.gz");
        Assert.True(root.GetProperty("native_handlers").GetBoolean());
        Assert.True(root.GetProperty("independent_native_match").GetBoolean());
        Assert.Equal(9, root.GetProperty("presentation").GetArrayLength());
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Rec first = w.Table(Tables.Officers)[root.GetProperty("officers")[0].GetInt32()];
        Rec second = w.Table(Tables.Officers)[root.GetProperty("officers")[1].GetInt32()];
        Rec? Ref(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null
            : w.Table(Convert.ToInt32(e[0].GetString()![2..], 16))[e[1].GetInt32()];
        int cases = 0, players = 0, swapped = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture"), trace = c.GetProperty("trace");
            Action undo = Vectors.Poke(w, f.GetProperty("pokes"));
            try
            {
                ResultCombat.MoraleRequest request = ResultCombat.ComputeMoraleRequest(w, first, second)!.Value;
                Assert.Equal(new[] { request.TargetUnit.Index, request.Delta, request.SourceHex, 1, 0 },
                    trace.GetProperty("morale")[0].EnumerateArray().Select(i => i.GetInt32()));
                Assert.Single(trace.GetProperty("morale").EnumerateArray());
                Assert.Equal(0, request.SetterValue);
                Assert.Equal(0, request.Morale);
                Assert.Equal(new[] { request.TargetUnit.Index, request.SetterValue },
                    trace.GetProperty("setter")[0].EnumerateArray().Select(i => i.GetInt32()));
                Assert.Single(trace.GetProperty("setter").EnumerateArray());
                DestructionQueue.EngineCall actual = ResultCombat.ImmediateRouteFromSourceHex(w, request)!.Value;
                JsonElement expected = trace.GetProperty("engine")[0];
                Assert.Single(trace.GetProperty("engine").EnumerateArray());
                Assert.Equal(DestructionQueue.Handler.UnitDestroyed, actual.Kind);
                Assert.Equal(Ref(expected[0]), actual.Target);
                Assert.Equal(Ref(expected[1]), actual.AttackerUnit);
                Assert.Equal(Ref(expected[2]), actual.AttackerBase);
                Assert.Equal(expected[3].GetInt32(), actual.Mode);
                Assert.Equal(expected[4].GetInt32(), actual.Reason);
                Assert.Empty(trace.GetProperty("queue").EnumerateArray());
                Assert.Empty(trace.GetProperty("injury").EnumerateArray());
                Assert.Equal(request.PlayerNotice ? 1 : 0, trace.GetProperty("player").GetArrayLength());
                if (request.PlayerNotice)
                {
                    Assert.Equal(new[] { 1, second.Index }, trace.GetProperty("player")[0].EnumerateArray().Select(i => i.GetInt32()));
                    players++;
                }
                swapped += f.GetProperty("selector").GetInt32() == 1 ? 1 : 0;
            }
            finally { undo(); cases++; }
        }
        Assert.Equal(3, cases);
        Assert.Equal(1, players);
        Assert.Equal(1, swapped);
    }

    [Fact]
    public void ReasonZeroRequestsAndInjuryStateMatchTheCompleteNativeCaller()
    {
        JsonElement root = Vectors.Read("result_reason0.json.gz");
        Assert.True(root.GetProperty("native_full_body").GetBoolean());
        Assert.Empty(root.GetProperty("native_stubs").EnumerateArray());
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Rec[] people = root.GetProperty("officers").EnumerateArray().Select(i => w.Table(Tables.Officers)[i.GetInt32()]).ToArray();
        Rec[] units = root.GetProperty("units").EnumerateArray().Select(i => w.Table(Tables.Units)[i.GetInt32()]).ToArray();
        int cases = 0, injuries = 0, players = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture"), output = c.GetProperty("observed"), trace = output.GetProperty("trace");
            Action undo = Vectors.Poke(w, f.GetProperty("pokes"));
            try
            {
                Rec a = w.Table(Tables.Officers)[f.GetProperty("officers")[0].GetInt32()];
                Rec b = w.Table(Tables.Officers)[f.GetProperty("officers")[1].GetInt32()];
                ResultCombat.Result? got = ResultCombat.ComputePositiveMorale(w, a, b);
                if (got is not { } result)
                {
                    foreach (JsonProperty p in trace.EnumerateObject()) Assert.Empty(p.Value.EnumerateArray());
                    continue;
                }
                Assert.Equal(new[] { result.TargetUnit.Index, result.Delta, result.SourceHex, 1, 0 },
                    trace.GetProperty("morale")[0].EnumerateArray().Select(i => i.GetInt32()));
                Assert.Single(trace.GetProperty("morale").EnumerateArray());
                Assert.Equal(new[] { result.TargetUnit.Index, result.SetterValue },
                    trace.GetProperty("setter")[0].EnumerateArray().Select(i => i.GetInt32()));
                Assert.Equal(result.Morale, trace.GetProperty("morale_after_set")[0].GetInt32());
                Assert.Equal(result.PlayerNotice ? 1 : 0, trace.GetProperty("player").GetArrayLength());
                if (result.PlayerNotice)
                {
                    Assert.Equal(new[] { 1, b.Index }, trace.GetProperty("player")[0].EnumerateArray().Select(i => i.GetInt32()));
                    players++;
                }
                Assert.Equal(result.InjuryRequested ? 1 : 0, trace.GetProperty("injury").GetArrayLength());
                if (result.InjuryRequested)
                {
                    Assert.Equal(new[] { b.Index, 3, a.Index, 0 }, trace.GetProperty("injury")[0].EnumerateArray().Select(i => i.GetInt32()));
                    injuries++;
                }
                Assert.False(result.Injury.Died);
                Assert.True(result.Morale > 0);
                for (int i = 0; i < 2; i++)
                {
                    byte[] officer = (byte[])people[i].Bytes.Clone(), unit = (byte[])units[i].Bytes.Clone();
                    if (people[i] == b) officer[0x161] = (byte)result.Injury.State;
                    if (units[i] == result.TargetUnit) unit[0x1A] = (byte)result.Morale;
                    Assert.Equal(officer, Convert.FromHexString(output.GetProperty("officers")[i].GetString()!));
                    Assert.Equal(unit, Convert.FromHexString(output.GetProperty("units")[i].GetString()!));
                }
            }
            finally { undo(); cases++; }
        }
        Assert.True(cases >= 13 && injuries >= 2 && players >= 3,
            $"cases {cases}, injury calls {injuries}, player notices {players}");
    }
}

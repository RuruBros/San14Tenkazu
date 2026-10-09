using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class DestructionConsumerTests
{
    [Fact]
    public void DispatchMatchesInputsCapturedDuringActualNativeEngineProcessing()
    {
        JsonElement root = Vectors.Read("destruction_engine.json.gz");
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Assert.True(root.GetProperty("native_handlers").GetBoolean());
        Assert.True(root.GetProperty("direct_native_match").GetBoolean());
        Rec? Ref(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(e[0].GetString()![2..], 16))[e[1].GetInt32()];
        TacticEffects.PendingDestruction Request(JsonElement e) => new(Ref(e[0]), Ref(e[1]), Ref(e[2]), Ref(e[3]), e[4].GetInt32(), e[5].GetInt32(), e[6].GetInt32());
        DestructionQueue.EngineCall Expected(JsonElement e) => new((DestructionQueue.Handler)e[0].GetInt32(), Ref(e[1])!, Ref(e[2]), Ref(e[3]), Ref(e[4]),
            e[5].GetInt32(), e[6].GetInt32(), e[7].GetInt32(), e[8].GetBoolean());
        int cases = 0, inputs = 0, unit = 0, captured = 0, tribe = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            foreach (JsonElement input in c.GetProperty("inputs").EnumerateArray())
            {
                Action restore = Vectors.Poke(w, c.GetProperty("pokes"));
                var undo = new List<(byte[] Bytes, byte[] Old)>();
                try
                {
                    // These bytes are native inputs captured before dispatch, not outputs of a C# engine.
                    JsonElement state = input.GetProperty("state");
                    foreach (JsonElement r in state.GetProperty("records").EnumerateArray())
                    {
                        byte[] bytes = w.Table(Convert.ToInt32(r[0].GetString()![2..], 16))[r[1].GetInt32()].Bytes;
                        undo.Add((bytes, bytes.ToArray()));
                        Convert.FromHexString(r[2].GetString()!).CopyTo(bytes, 0);
                    }
                    if (state.GetProperty("worldstate").ValueKind != JsonValueKind.Null)
                    {
                        undo.Add((w.WorldState.Bytes, w.WorldState.Bytes.ToArray()));
                        Convert.FromHexString(state.GetProperty("worldstate").GetString()!).CopyTo(w.WorldState.Bytes, 0);
                    }
                    var calls = new List<DestructionQueue.EngineCall>();
                    var queue = new LinkedList<TacticEffects.PendingDestruction>([Request(input.GetProperty("request"))]);
                    DestructionQueue.Consume(w, queue, calls.Add);
                    JsonElement expected = input.GetProperty("call");
                    DestructionQueue.EngineCall[] expectedCalls = expected.ValueKind == JsonValueKind.Null ? [] : [Expected(expected)];
                    Assert.True(calls.SequenceEqual(expectedCalls), $"actual engine case {cases}, input {inputs}: {input}");
                    Assert.Empty(queue);
                    unit += calls.Count(x => x.Kind == DestructionQueue.Handler.UnitDestroyed);
                    captured += calls.Count(x => x.Kind == DestructionQueue.Handler.BaseCaptured);
                    tribe += calls.Count(x => x.Kind == DestructionQueue.Handler.TribeCaptured);
                    inputs++;
                }
                finally
                {
                    for (int i = undo.Count - 1; i >= 0; i--) undo[i].Old.CopyTo(undo[i].Bytes, 0);
                    restore();
                }
            }
            cases++;
        }
        Assert.True(cases >= 8 && inputs >= 11 && unit >= 6 && captured >= 4 && tribe >= 1,
            $"actual cases {cases}, inputs {inputs}, units {unit}, bases {captured}, tribes {tribe}");
    }

    [Fact]
    public void OrderedEngineCallsCurrentValidityAndQueueClearMatchTheGamesConsumer()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        int n = 0, unit = 0, captured = 0, tribe = 0, recheck = 0, replaced = 0, appended = 0;
        Rec? Ref(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(e[0].GetString()![2..], 16))[e[1].GetInt32()];
        TacticEffects.PendingDestruction Request(JsonElement e) => new(Ref(e[0]), Ref(e[1]), Ref(e[2]), Ref(e[3]), e[4].GetInt32(), e[5].GetInt32(), e[6].GetInt32());
        DestructionQueue.EngineCall Expected(JsonElement e) => new((DestructionQueue.Handler)e[0].GetInt32(), Ref(e[1])!, Ref(e[2]), Ref(e[3]), Ref(e[4]),
            e[5].GetInt32(), e[6].GetInt32(), e[7].GetInt32(), e[8].GetBoolean());
        foreach (JsonElement c in Vectors.Read("destruction_consumer.json.gz").GetProperty("cases").EnumerateArray())
        {
            Action restore = Vectors.Poke(w, c[4]);
            var changed = new List<(byte[] Bytes, int At, byte[] Old)>();
            try
            {
                var queue = new LinkedList<TacticEffects.PendingDestruction>(c[0].EnumerateArray().Select(Request));
                var calls = new List<DestructionQueue.EngineCall>();
                DestructionQueue.Consume(w, queue, call =>
                {
                    calls.Add(call);
                    foreach (JsonElement action in c[1].EnumerateArray().Where(a => a[0].GetInt32() == calls.Count - 1).Select(a => a[1]))
                    {
                        switch (action[0].GetString())
                        {
                            case "poke":
                                byte[] bytes = w.Table(Convert.ToInt32(action[1].GetString()![2..], 16))[action[2].GetInt32()].Bytes;
                                int at = action[3].GetInt32();
                                byte[] data = Convert.FromHexString(action[4].GetString()!);
                                changed.Add((bytes, at, bytes.AsSpan(at, data.Length).ToArray()));
                                data.CopyTo(bytes, at); recheck++;
                                break;
                            case "replace":
                                var node = queue.First!;
                                for (int i = 0; i < action[1].GetInt32(); i++) node = node.Next!;
                                node.Value = Request(action[2]); replaced++;
                                break;
                            case "append": queue.AddLast(Request(action[1])); appended++; break;
                            default: throw new InvalidOperationException($"Unknown boundary action: {action}");
                        }
                    }
                });
                Assert.True(calls.SequenceEqual(c[2].EnumerateArray().Select(Expected)), $"consumer case {n}: {c}");
                Assert.Empty(queue);
                Assert.Equal(new[] { 0, 0, 0 }, c[3].EnumerateArray().Select(x => x.GetInt32()).ToArray());
                unit += calls.Count(x => x.Kind == DestructionQueue.Handler.UnitDestroyed);
                captured += calls.Count(x => x.Kind == DestructionQueue.Handler.BaseCaptured);
                tribe += calls.Count(x => x.Kind == DestructionQueue.Handler.TribeCaptured);
            }
            finally
            {
                for (int i = changed.Count - 1; i >= 0; i--) changed[i].Old.CopyTo(changed[i].Bytes, changed[i].At);
                restore();
            }
            n++;
        }
        Assert.True(n >= 800 && unit > 0 && captured > 0 && tribe > 0 && recheck > 0 && replaced > 0 && appended > 0,
            $"cases {n}, units {unit}, bases {captured}, tribes {tribe}, rechecks {recheck}, replacements {replaced}, appends {appended}");
    }
}

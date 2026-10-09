using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class DestructionQueueTests
{
    [Fact]
    public void RegistrationMatchesCompleteCombatCallersAtTheNativeEngineBoundary()
    {
        JsonElement root = Vectors.Read("destruction_overflow.json.gz");
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Assert.True(root.GetProperty("native_handlers").GetBoolean());
        Assert.True(root.GetProperty("independent_native_match").GetBoolean());
        Rec? Ref(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null
            : w.Table(Convert.ToInt32(e[0].GetString()![2..], 16))[e[1].GetInt32()];
        TacticEffects.PendingDestruction Request(JsonElement e) => new(Ref(e[0]), Ref(e[1]), Ref(e[2]), Ref(e[3]),
            e[4].GetInt32(), e[5].GetInt32(), e[6].GetInt32());
        int cases = 0, fullDuplicate = 0, appended = 0, unit = 0, captured = 0, tribe = 0, faults = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            Action restore = Vectors.Poke(w, c.GetProperty("pokes"));
            try
            {
                var queue = c.GetProperty("initial").EnumerateArray().Select(Request).ToList();
                int initialCount = queue.Count, ordinal = 0;
                foreach (JsonElement input in c.GetProperty("queue_inputs").EnumerateArray())
                {
                    bool accepted = DestructionQueue.TryAdd(w, queue, Request(input));
                    Assert.Equal(c.GetProperty("accepted")[ordinal++].GetInt32() != 0, accepted);
                }
                Assert.True(queue.SequenceEqual(c.GetProperty("queue_after").EnumerateArray().Select(Request)),
                    $"complete native caller case {cases}: {c.GetProperty("fixture")}");
                Assert.Equal(c.GetProperty("accepted").GetArrayLength(), ordinal);
                appended += queue.Count > initialCount ? 1 : 0;
                JsonElement fixture = c.GetProperty("fixture"), calls = c.GetProperty("root_calls");
                if (initialCount == 128 && fixture.GetProperty("duplicate").GetBoolean())
                {
                    Assert.Equal(0, c.GetProperty("accepted")[0].GetInt32());
                    Assert.Single(calls.EnumerateArray());
                    fullDuplicate++;
                }
                foreach (JsonElement call in calls.EnumerateArray())
                {
                    int kind = call[0].GetInt32();
                    unit += kind == 0 ? 1 : 0;
                    captured += kind == 1 ? 1 : 0;
                    tribe += kind == 2 ? 1 : 0;
                    if (kind == 0)
                    {
                        Assert.Equal(fixture.TryGetProperty("command_reason", out _) ? 0 : 1, call[5].GetInt32());
                        Assert.Equal(c.GetProperty("request")[5].GetInt32(), call[6].GetInt32());
                    }
                }
                JsonElement fault = c.GetProperty("native_fault");
                if (fault.ValueKind != JsonValueKind.Null)
                {
                    // This is a verified native null read, not a successful engine completion.
                    Assert.Single(calls.EnumerateArray());
                    Assert.Equal(1, calls[0][0].GetInt32());
                    Assert.Single(c.GetProperty("frames").EnumerateArray());
                    Assert.Equal(JsonValueKind.Null, c.GetProperty("frames")[0].ValueKind);
                    string rva = fault.GetProperty("rva").GetString()!;
                    Assert.Contains(rva, new[] { "0x284dd1", "0x22cc46" });
                    Assert.Equal(rva == "0x284dd1" ? "0x10" : "0x12", fault.GetProperty("address").GetString());
                    faults++;
                }
                else
                {
                    Assert.Equal(c.GetProperty("calls").GetArrayLength(), c.GetProperty("frames").GetArrayLength());
                    Assert.All(c.GetProperty("frames").EnumerateArray(), frame => Assert.NotEqual(JsonValueKind.Null, frame.ValueKind));
                }
            }
            finally { restore(); }
            cases++;
        }
        Assert.True(cases >= 128 && fullDuplicate >= 26 && appended >= 26 && unit > 0 && captured > 0 && tribe >= 10 && faults > 0,
            $"cases {cases}, full duplicates {fullDuplicate}, appended {appended}, units {unit}, bases {captured}, tribes {tribe}, faults {faults}");
    }

    [Fact]
    public void CompletePayloadsDuplicatePriorityAndCapacityMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        int n = 0, appended = 0, duplicate = 0, overflow = 0, fullDuplicate = 0;
        Rec? Ref(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(e[0].GetString()![2..], 16))[e[1].GetInt32()];
        TacticEffects.PendingDestruction Request(JsonElement e) => new(Ref(e[0]), Ref(e[1]), Ref(e[2]), Ref(e[3]), e[4].GetInt32(), e[5].GetInt32());
        foreach (JsonElement c in Vectors.Read("destruction_queue.json.gz").GetProperty("cases").EnumerateArray())
        {
            var queue = c[0].EnumerateArray().Select(Request).ToList();
            var request = Request(c[1]);
            bool wasDuplicate = queue.Contains(request);
            int initial = queue.Count;
            bool accepted = DestructionQueue.TryAdd(w, queue, request);
            Assert.True(accepted == (c[2].GetInt32() != 0) && queue.SequenceEqual(c[3].EnumerateArray().Select(Request)), $"case {n}: {c}");
            appended += accepted && queue.Count > initial ? 1 : 0;
            duplicate += accepted && queue.Count == initial ? 1 : 0;
            overflow += !accepted ? 1 : 0;
            fullDuplicate += initial >= 128 && wasDuplicate && !accepted ? 1 : 0;
            n++;
        }
        Assert.True(n >= 1_800 && appended > 0 && duplicate > 0 && overflow > 0 && fullDuplicate > 0,
            $"cases {n}, appended {appended}, duplicate {duplicate}, overflow {overflow}, full duplicate {fullDuplicate}");
    }
}

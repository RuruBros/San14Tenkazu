using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class UnitLifecycleTests
{
    [Fact]
    public void CompleteRecordResetAndExternalListOrderMatchNative()
    {
        JsonElement root = Vectors.Read("unit_lifecycle.json.gz");
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Assert.True(root.GetProperty("native_clear").GetBoolean());
        int n = 0, disabled = 0, clears = 0;
        void Check(Rec unit, bool enabled, JsonElement before, JsonElement after, JsonElement expectedCalls)
        {
            byte[] old = unit.Bytes.ToArray();
            try
            {
                Convert.FromHexString(before.GetString()!).CopyTo(unit.Bytes, 0);
                var calls = new List<int>();
                UnitLifecycle.Reset(unit, enabled, calls.Add);
                Assert.True(unit.Bytes.SequenceEqual(Convert.FromHexString(after.GetString()!)), $"complete reset case {n}");
                Assert.Equal(expectedCalls.EnumerateArray().Select(x => x.GetInt32()).ToArray(), calls.ToArray());
                Assert.False(Objects.UnitValid(unit));
                clears += calls.Count;
            }
            finally { old.CopyTo(unit.Bytes, 0); }
        }
        foreach (JsonElement c in root.GetProperty("resets").EnumerateArray())
        {
            bool enabled = c[1].GetBoolean();
            Check(w.Table(Tables.Units)[c[0].GetInt32()], enabled, c[2], c[3], c[4]);
            if (!enabled) disabled++;
            n++;
        }
        Assert.Equal(600, n);
        Assert.Equal(120, disabled);
        Assert.Equal(root.GetProperty("stats").GetProperty("reset_clears").GetInt32(), clears);

        // These are reset inputs captured inside the real finalizer, not replayed engine output states.
        JsonElement engine = Vectors.Read("unit_finalization.json.gz");
        World ew = Vectors.LoadWorld(engine.GetProperty("world").GetString()!);
        int actual = 0;
        foreach (JsonElement c in engine.GetProperty("cases").EnumerateArray())
            foreach (JsonElement reset in c.GetProperty("resets").EnumerateArray())
            {
                Rec unit = ew.Table(Tables.Units)[reset[0][1].GetInt32()];
                Check(unit, reset[1].GetBoolean(), reset[2], reset[3], reset[4]);
                actual++;
                n++;
            }
        Assert.Equal(engine.GetProperty("stats").GetProperty("resets").GetInt32(), actual);
        Assert.True(actual >= 5);
    }

    [Fact]
    public void PendingTraversalIncludesNullInvalidAndChangedNodesInNativeOrder()
    {
        JsonElement root = Vectors.Read("unit_lifecycle.json.gz");
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Rec? Ref(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[e.GetInt32()];
        int n = 0, callbacks = 0, nulls = 0, invalid = 0, replaced = 0, appended = 0;
        foreach (JsonElement c in root.GetProperty("traversal").EnumerateArray())
        {
            var pending = new LinkedList<Rec?>(c[0].EnumerateArray().Select(Ref));
            var calls = new List<Rec?>();
            UnitLifecycle.ConsumePending(pending, unit =>
            {
                calls.Add(unit);
                foreach (JsonElement action in c[1].EnumerateArray().Where(a => a[0].GetInt32() == calls.Count-1).Select(a => a[1]))
                {
                    if (action[0].GetString() == "replace")
                    {
                        var node = pending.First!;
                        for (int i = 0; i < action[1].GetInt32(); i++) node = node.Next!;
                        node.Value = Ref(action[2]);
                        replaced++;
                    }
                    else if (action[0].GetString() == "append")
                    {
                        pending.AddLast(Ref(action[1]));
                        appended++;
                    }
                    else throw new InvalidOperationException($"Unknown pending boundary action: {action}");
                }
            });
            Assert.True(calls.SequenceEqual(c[2].EnumerateArray().Select(Ref)), $"pending traversal case {n}: {c}");
            Assert.Empty(pending);
            callbacks += calls.Count;
            nulls += calls.Count(x => x is null);
            invalid += calls.Count(x => x is not null && !Objects.UnitValid(x));
            n++;
        }
        Assert.Equal(800, n);
        Assert.Equal(root.GetProperty("stats").GetProperty("callbacks").GetInt32(), callbacks);
        Assert.Equal(root.GetProperty("stats").GetProperty("null").GetInt32(), nulls);
        Assert.True(invalid > 0 && replaced > 0 && appended > 0);
    }

    [Fact]
    public void PendingOrderMatchesInputsOfTheActualNativeFinalizer()
    {
        JsonElement root = Vectors.Read("unit_finalization.json.gz");
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Assert.True(root.GetProperty("native_finalizer").GetBoolean());
        Assert.True(root.GetProperty("direct_native_match").GetBoolean());
        Rec? Ref(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[e[1].GetInt32()];
        int n = 0, callbacks = 0, nulls = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            // ConsumePending delegates the body; its test does not apply the native finalizer's output as C# logic.
            var pending = new LinkedList<Rec?>(c.GetProperty("pending").EnumerateArray().Select(Ref));
            var calls = new List<Rec?>();
            UnitLifecycle.ConsumePending(pending, calls.Add);
            Assert.True(calls.SequenceEqual(c.GetProperty("calls").EnumerateArray().Select(Ref)), $"native engine case {n}");
            Assert.Empty(pending);
            callbacks += calls.Count;
            nulls += calls.Count(x => x is null);
            n++;
        }
        Assert.Equal(8, n);
        Assert.Equal(root.GetProperty("stats").GetProperty("finalizers").GetInt32(), callbacks);
        Assert.Equal(1, nulls);
    }
}

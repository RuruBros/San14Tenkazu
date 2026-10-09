using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class UnitGroupCacheTests
{
    private static readonly int[] Focus = [Tables.Units, Tables.Troops];
    private static ulong Pointer(JsonElement e) => Convert.ToUInt64(e.GetString()![2..], 16);
    private static Dictionary<ulong, ulong> Cache(JsonElement e) => e.EnumerateArray().ToDictionary(r => Pointer(r[0]), r => Pointer(r[1]));
    private static Dictionary<int, byte[][]> Records(World w) => Focus.ToDictionary(t => t, t => w.Table(t).Select(r => r.Bytes.ToArray()).ToArray());
    private static void Apply(Dictionary<int, byte[][]> records, JsonElement changes)
    {
        foreach (JsonElement row in changes.EnumerateArray())
            records[Convert.ToInt32(row[0].GetString()!, 16)][row[1].GetInt32()] = Convert.FromHexString(row[2].GetString()!);
    }
    private static void CopyTo(World w, Dictionary<int, byte[][]> records)
    {
        foreach ((int t, byte[][] rows) in records)
            for (int i = 0; i < rows.Length; i++) rows[i].CopyTo(w.Table(t)[i].Bytes, 0);
    }
    private static void AssertRecords(World w, Dictionary<int, byte[][]> records, string name)
    {
        foreach ((int t, byte[][] rows) in records)
            for (int i = 0; i < rows.Length; i++)
                Assert.True(rows[i].SequenceEqual(w.Table(t)[i].Bytes), $"{name}: {t:X}, entry {i}");
    }
    private static void AssertCache(Dictionary<ulong, ulong> cache, JsonElement expected) =>
        Assert.Equal(Cache(expected).OrderBy(e => e.Key).ToArray(), cache.OrderBy(e => e.Key).ToArray());
    private static void UnitList(World w, int head, JsonElement indices)
    {
        w.List(head).Clear();
        w.List(head).AddRange(indices.EnumerateArray().Select(i => new Rec?[] { w.Table(Tables.Units)[i.GetInt32()] }));
    }

    [Fact]
    public void GroupDissolutionAssignmentResetAndUiCallbacksMatchNative()
    {
        JsonElement root = Vectors.Read("unit_group_cache.json.gz");
        Assert.True(root.GetProperty("native_cache_and_ui").GetBoolean());
        var owners = root.GetProperty("ui_owners").EnumerateObject().ToDictionary(p => Convert.ToUInt64(p.Name[2..], 16), p => Pointer(p.Value));
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Dictionary<int, byte[][]> baseline = Records(w);
        int n = 0, callbacks = 0, releases = 0, recursiveCases = 0, preservedAssociation = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray().Where(e => e.GetProperty("kind").GetString() != "unit_cache"))
        {
            string name = c.GetProperty("name").GetString()!;
            Dictionary<int, byte[][]> input = Records(w);
            Apply(input, c.GetProperty("inputs"));
            CopyTo(w, input);
            UnitList(w, Lists.UnitList, c.GetProperty("active"));
            UnitList(w, Lists.EngagedUnits, c.GetProperty("pending"));
            var groups = Cache(c.GetProperty("caches_before").GetProperty("troop"));
            var units = Cache(c.GetProperty("caches_before").GetProperty("unit"));
            JsonElement[] checkpoints = c.GetProperty("cache_checkpoints").EnumerateArray().ToArray();
            JsonElement[] uiCheckpoints = c.GetProperty("ui_checkpoints").EnumerateArray().ToArray();
            var uiTrace = new List<(string Kind, ulong Object, int Type, ulong Owner)>();
            int checkpoint = 0, uiCheckpoint = 0;
            void AssertCheckpoint(JsonElement native, string label)
            {
                Dictionary<int, byte[][]> atCall = input.ToDictionary(p => p.Key, p => p.Value.Select(b => b.ToArray()).ToArray());
                Apply(atCall, native.GetProperty("records"));
                AssertRecords(w, atCall, name + label);
                AssertCache(groups, native.GetProperty("caches").GetProperty("troop"));
                AssertCache(units, native.GetProperty("caches").GetProperty("unit"));
            }
            void RemoveCache(Rec troop)
            {
                JsonElement native = checkpoints[checkpoint++];
                Assert.Equal(native.GetProperty("troop").GetInt32(), troop.Index);
                AssertCheckpoint(native, " at cache callback");
                UnitLifecycle.RemoveTroopCache(troop, groups, obj => owners[obj],
                    (obj, type) =>
                    {
                        AssertCheckpoint(uiCheckpoints[uiCheckpoint++], " before property cleanup");
                        uiTrace.Add(("properties", obj, type, 0));
                    },
                    (owner, obj) =>
                    {
                        AssertCheckpoint(uiCheckpoints[uiCheckpoint++], " before object destruction");
                        uiTrace.Add(("destroy", obj, 0, owner));
                    });
            }
            try
            {
                JsonElement target = c.GetProperty("target");
                Rec? troop = target.ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Troops)[target.GetInt32()];
                switch (c.GetProperty("kind").GetString())
                {
                    case "dissolve":
                        Assert.Equal(c.GetProperty("return").GetBoolean(), UnitLifecycle.DissolveTroop(w, troop, RemoveCache));
                        break;
                    case "set":
                        UnitLifecycle.SetTroop(w, w.Table(Tables.Units)[target.GetInt32()], c.GetProperty("assigned").GetInt32(), RemoveCache);
                        break;
                    case "reset":
                        UnitLifecycle.ResetTroop(troop!, c.GetProperty("manager_available").GetBoolean(), RemoveCache);
                        break;
                    case "troop_cache":
                        foreach (JsonElement i in c.GetProperty("targets").EnumerateArray()) RemoveCache(w.Table(Tables.Troops)[i.GetInt32()]);
                        break;
                }
                Dictionary<int, byte[][]> expected = input.ToDictionary(p => p.Key, p => p.Value.Select(b => b.ToArray()).ToArray());
                Apply(expected, c.GetProperty("records_after"));
                AssertRecords(w, expected, name);
                AssertCache(groups, c.GetProperty("caches_after").GetProperty("troop"));
                AssertCache(units, c.GetProperty("caches_after").GetProperty("unit"));
                Assert.Equal(checkpoints.Length, checkpoint);
                Assert.Equal(uiCheckpoints.Length, uiCheckpoint);
                var nativeUi = c.GetProperty("trace").EnumerateArray().Where(e => e[0].GetString() is "properties" or "destroy")
                    .Select(e => (e[0].GetString()!, Pointer(e[1]), e[0].GetString() == "properties" ? e[2].GetInt32() : 0,
                        e[0].GetString() == "destroy" ? Pointer(e[2]) : 0UL)).ToArray();
                Assert.Equal(nativeUi, uiTrace);
                if (checkpoint > 1) recursiveCases++;
                if (c.GetProperty("kind").GetString() == "reset" && !c.GetProperty("manager_available").GetBoolean() &&
                    BitConverter.ToUInt64(input[Tables.Troops][target.GetInt32()], 0x30) != 0)
                {
                    Assert.NotEqual(0UL, BitConverter.ToUInt64(troop!.Bytes, 0x30));
                    Assert.Empty(uiTrace);
                    preservedAssociation++;
                }
                // Native operations change member fields, while preserving both external list orders.
                Assert.Equal(c.GetProperty("active").EnumerateArray().Select(e => e.GetInt32()), w.List(Lists.UnitList).Select(e => e[0]!.Index));
                Assert.Equal(c.GetProperty("pending").EnumerateArray().Select(e => e.GetInt32()), w.List(Lists.EngagedUnits).Select(e => e[0]!.Index));
                callbacks += checkpoint; releases += uiTrace.Count(e => e.Kind == "properties"); n++;
            }
            finally { CopyTo(w, baseline); }
        }
        Assert.Equal(root.GetProperty("stats").GetProperty("cases").GetInt32() - root.GetProperty("stats").GetProperty("unit_cache").GetInt32(), n);
        Assert.Equal(root.GetProperty("stats").GetProperty("ui_releases").GetInt32(), releases);
        Assert.True(callbacks > releases);
        Assert.True(recursiveCases > 0);
        Assert.Equal(1, preservedAssociation);
    }

    [Fact]
    public void UnitPointerCacheRemovalPreservesAllRecordsAndOtherValues()
    {
        JsonElement root = Vectors.Read("unit_group_cache.json.gz");
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Dictionary<int, byte[][]> baseline = Records(w);
        int n = 0, erased = 0, complete = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "unit_cache"))
        {
            Dictionary<int, byte[][]> expected = Records(w);
            Apply(expected, c.GetProperty("inputs"));
            CopyTo(w, expected);
            var cache = Cache(c.GetProperty("caches_before").GetProperty("unit"));
            int before = cache.Count;
            try
            {
                foreach (JsonElement key in c.GetProperty("keys").EnumerateArray()) UnitLifecycle.RemoveUnitCache(cache, Pointer(key));
                Assert.Empty(c.GetProperty("records_after").EnumerateArray());
                AssertRecords(w, expected, c.GetProperty("name").GetString()!);
                AssertCache(cache, c.GetProperty("caches_after").GetProperty("unit"));
                erased += before - cache.Count;
                if (before > 0 && cache.Count == 0) complete++;
                n++;
            }
            finally { CopyTo(w, baseline); }
        }
        Assert.Equal(root.GetProperty("stats").GetProperty("unit_cache").GetInt32(), n);
        Assert.Equal(root.GetProperty("stats").GetProperty("unit_erases").GetInt32(), erased);
        Assert.Equal(3, complete);
    }
}

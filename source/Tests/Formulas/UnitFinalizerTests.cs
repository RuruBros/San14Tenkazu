using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class UnitFinalizerTests
{
    private static readonly int[] Focus = [Tables.Units, Tables.Troops, Tables.CautionTroops, Tables.CautionUnits];
    private static ulong Pointer(JsonElement e) => Convert.ToUInt64(e.GetString()![2..], 16);
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
    private static void AssertRecords(World w, Dictionary<int, byte[][]> input, JsonElement changes, string label)
    {
        Dictionary<int, byte[][]> expected = input.ToDictionary(p => p.Key, p => p.Value.ToArray());
        Apply(expected, changes);
        foreach ((int t, byte[][] rows) in expected)
            for (int i = 0; i < rows.Length; i++)
                Assert.True(rows[i].SequenceEqual(w.Table(t)[i].Bytes), $"{label}: table {t:X}, entry {i}");
    }
    private static List<Rec?>? Members(World w, int table, JsonElement e) => e.ValueKind == JsonValueKind.Null ? null :
        e.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? null : w.Table(table)[v.GetInt32()]).ToList();
    private static List<ulong>? Values(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.EnumerateArray().Select(Pointer).ToList();
    private static Dictionary<ulong, ulong> Cache(JsonElement e) => e.EnumerateArray().ToDictionary(r => Pointer(r[0]), r => Pointer(r[1]));
    private static void SetList(World w, int head, JsonElement e)
    {
        w.List(head).Clear();
        w.List(head).AddRange((Members(w, Tables.Units, e) ?? []).Select(r => new Rec?[] { r }));
    }

    [Fact]
    public void CompleteFinalizerAndLivePendingConsumerMatchNativeStateAndBoundaries()
    {
        JsonElement root = Vectors.Read("unit_finalizer.json.gz");
        Assert.True(root.GetProperty("native_full_body").GetBoolean());
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Dictionary<int, byte[][]> baseline = Records(w);
        var owners = root.GetProperty("ui_owners").EnumerateObject().ToDictionary(p => Convert.ToUInt64(p.Name[2..], 16), p => Pointer(p.Value));
        int n = 0, resets = 0, detaches = 0, clears = 0, groupCalls = 0, exhausted = 0, dynamic = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("kind").GetString() is "unit" or "pending"))
        {
            string name = c.GetProperty("name").GetString()!;
            JsonElement inputState = c.GetProperty("inputs");
            Dictionary<int, byte[][]> input = Records(w);
            Apply(input, inputState.GetProperty("records")); CopyTo(w, input);
            SetList(w, Lists.UnitList, inputState.GetProperty("active"));
            SetList(w, Lists.EngagedUnits, inputState.GetProperty("pending"));
            List<Rec?>? cautions = Members(w, Tables.CautionUnits, inputState.GetProperty("cautions"));
            var cautionGroups = inputState.GetProperty("caution_troops").EnumerateArray().Select((e, i) => (i, list: Members(w, Tables.Units, e)))
                .Where(p => p.list is not null).ToDictionary(p => p.i, p => p.list!);
            var lists = root.GetProperty("unit_lists").EnumerateObject().ToDictionary(p => p.Name, p => Values(p.Value));
            foreach (JsonProperty p in inputState.GetProperty("unit_lists").EnumerateObject()) lists[p.Name] = Values(p.Value);
            var unitCache = Cache(inputState.GetProperty("caches").GetProperty("unit"));
            var troopCache = Cache(inputState.GetProperty("caches").GetProperty("troop"));
            JsonElement[] events = c.GetProperty("events").EnumerateArray().ToArray();
            bool[] availability = c.GetProperty("availability").EnumerateArray().Select(e => e.GetBoolean()).ToArray();
            int eventAt = 0, availableAt = 0; bool acted = false;
            void Check(JsonElement s, string label)
            {
                AssertRecords(w, input, s.GetProperty("records"), name + label);
                foreach ((string key, List<Rec?[]> actual) in new[] { ("active", w.List(Lists.UnitList)), ("pending", w.List(Lists.EngagedUnits)) })
                    Assert.Equal(Members(w, Tables.Units, s.GetProperty(key)) ?? [], actual.Select(v => v[0]).ToList());
                Assert.Equal(Members(w, Tables.CautionUnits, s.GetProperty("cautions")), cautions);
                foreach ((int i, List<Rec?> list) in cautionGroups)
                    Assert.Equal(Members(w, Tables.Units, s.GetProperty("caution_troops")[i]), list);
                foreach (JsonProperty p in s.GetProperty("unit_lists").EnumerateObject()) Assert.Equal(Values(p.Value), lists[p.Name]);
                Assert.Equal(Cache(s.GetProperty("caches").GetProperty("unit")).OrderBy(p => p.Key).ToArray(), unitCache.OrderBy(p => p.Key).ToArray());
                Assert.Equal(Cache(s.GetProperty("caches").GetProperty("troop")).OrderBy(p => p.Key).ToArray(), troopCache.OrderBy(p => p.Key).ToArray());
            }
            JsonElement Next(string kind)
            {
                Assert.True(eventAt < events.Length, $"{name}: unexpected {kind} callback");
                JsonElement e = events[eventAt++]; Assert.Equal(kind, e.GetProperty("kind").GetString());
                Check(e.GetProperty("state"), " before " + kind); return e;
            }
            void Notify(Rec unit)
            {
                Assert.Equal(unit.Index, Next("notify").GetProperty("unit").GetInt32());
                if (acted) return; acted = true;
                foreach (JsonElement a in c.GetProperty("actions").EnumerateArray())
                {
                    if (a[0].GetString() == "replace") w.List(Lists.EngagedUnits)[a[1].GetInt32()][0] = w.Table(Tables.Units)[a[2].GetInt32()];
                    else if (a[0].GetString() == "append")
                    {
                        Rec extra = w.Table(Tables.Units)[a[1].GetInt32()];
                        if (!w.List(Lists.EngagedUnits).Any(v => ReferenceEquals(v[0], extra))) w.List(Lists.EngagedUnits).Add([extra]);
                    }
                    else throw new InvalidOperationException("Unknown external notification action");
                    dynamic++;
                }
            }
            var engine = new UnitFinalizerEngine(c.GetProperty("listener").GetBoolean() ? Notify : null,
                unit => { Assert.Equal(unit.Index, Next("detach").GetProperty("unit").GetInt32()); detaches++; },
                () => { Assert.True(availableAt < availability.Length); bool available = availability[availableAt++]; if (!available) exhausted++; return available; },
                () => true,
                (unit, at) =>
                {
                    JsonElement e = Next("clear_unit_list");
                    Assert.Equal(unit.Index, e.GetProperty("unit").GetInt32()); Assert.Equal(at, e.GetProperty("offset").GetInt32());
                    lists[$"{unit.Index}:{at:x}"]?.Clear(); clears++;
                },
                troop =>
                {
                    Assert.Equal(troop.Index, Next("troop_cache").GetProperty("troop").GetInt32()); groupCalls++;
                    UnitLifecycle.RemoveTroopCache(troop, troopCache, obj => owners[obj],
                        (obj, type) =>
                        {
                            JsonElement e = Next("properties"); Assert.Equal(obj, Pointer(e.GetProperty("object"))); Assert.Equal(type, e.GetProperty("type").GetInt32());
                        },
                        (owner, obj) =>
                        {
                            JsonElement e = Next("destroy"); Assert.Equal(obj, Pointer(e.GetProperty("object"))); Assert.Equal(owner, Pointer(e.GetProperty("owner")));
                        });
                });
            try
            {
                var finalizer = new UnitFinalizer(w, cautions, cautionGroups, unitCache, engine);
                if (c.GetProperty("kind").GetString() == "pending") finalizer.ConsumePending();
                else finalizer.Apply(c.GetProperty("target").ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[c.GetProperty("target").GetInt32()]);
                Check(c.GetProperty("after"), " after full body");
                Assert.Equal(events.Length, eventAt); Assert.Equal(availability.Length, availableAt);
                resets += c.GetProperty("phases").EnumerateArray().Count(p => p[0].GetString() == "reset"); n++;
            }
            finally { CopyTo(w, baseline); }
        }
        JsonElement stats = root.GetProperty("stats");
        Assert.Equal(stats.GetProperty("unit").GetInt32()+stats.GetProperty("pending").GetInt32(), n);
        Assert.Equal(stats.GetProperty("resets").GetInt32(), resets);
        Assert.Equal(stats.GetProperty("ui_detaches").GetInt32(), detaches);
        Assert.Equal(stats.GetProperty("unit_list_clears").GetInt32(), clears);
        Assert.Equal(stats.GetProperty("troop_cache_calls").GetInt32(), groupCalls);
        Assert.Equal(1, exhausted); Assert.Equal(2, dynamic);
    }

    [Fact]
    public void ActiveAliasErasureAndLiveMemberCountMatchNative()
    {
        JsonElement root = Vectors.Read("unit_finalizer.json.gz");
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Dictionary<int, byte[][]> baseline = Records(w);
        int erase = 0, count = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("kind").GetString() is "erase" or "count"))
        {
            JsonElement inputState = c.GetProperty("inputs"); Dictionary<int, byte[][]> input = Records(w);
            Apply(input, inputState.GetProperty("records")); CopyTo(w, input);
            SetList(w, Lists.UnitList, inputState.GetProperty("active")); SetList(w, Lists.EngagedUnits, inputState.GetProperty("pending"));
            JsonElement target = c.GetProperty("target");
            try
            {
                if (c.GetProperty("kind").GetString() == "erase")
                {
                    Assert.Equal(c.GetProperty("return").GetInt32() != 0, UnitLifecycle.RemoveActiveUnit(w,
                        target.ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Units)[target.GetInt32()])); erase++;
                }
                else
                {
                    Assert.Equal(c.GetProperty("return").GetInt32(), UnitLifecycle.TroopMemberCount(w, w.Table(Tables.Troops)[target.GetInt32()])); count++;
                }
                AssertRecords(w, input, c.GetProperty("after").GetProperty("records"), c.GetProperty("name").GetString()!);
                Assert.Equal(Members(w, Tables.Units, c.GetProperty("after").GetProperty("active")), w.List(Lists.UnitList).Select(v => v[0]).ToList());
            }
            finally { CopyTo(w, baseline); }
        }
        Assert.Equal(root.GetProperty("stats").GetProperty("erase").GetInt32(), erase);
        Assert.Equal(root.GetProperty("stats").GetProperty("count").GetInt32(), count);
    }
}

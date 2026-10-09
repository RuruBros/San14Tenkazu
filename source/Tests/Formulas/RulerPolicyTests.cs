using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class RulerPolicyTests
{
    [Theory]
    [InlineData("reset")]
    [InlineData("advisor")]
    [InlineData("field")]
    [InlineData("cost")]
    [InlineData("refund")]
    [InlineData("doctrine")]
    [InlineData("gate")]
    [InlineData("facility")]
    [InlineData("change")]
    public void CompleteNativePolicyBodiesMatchRecordsAndEngineState(string group)
    {
        JsonElement root = Vectors.Read(group == "change" ? "ruler_policy_composition.json.gz" : "ruler_policy.json.gz");
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.False(root.GetProperty("presentation_stubs").GetBoolean());
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        int cases = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture"), after = c.GetProperty("after");
            string kind = f.GetProperty("kind").GetString()!;
            if (kind != group) continue;
            World w = Vectors.LoadWorld(world);
            Vectors.Poke(w, f.GetProperty("pokes"));
            int[] tables = snapshot.GetProperty("tables").EnumerateObject().Select(p => Convert.ToInt32(p.Name, 16)).ToArray();
            var expected = tables.ToDictionary(t => t, t => w.Table(t).Select(r => r.Bytes.ToArray()).ToArray());
            var pointers = tables.SelectMany(w.Table).ToDictionary(r => r.NativeAddress);
            Rec? Pointer(string value) => value == "0x0" ? null : pointers[Convert.ToUInt64(value[2..], 16)];
            Rec? Object(int table, string key) => f.GetProperty(key).ValueKind == JsonValueKind.Null ? null : w.Table(table)[f.GetProperty(key).GetInt32()];
            Rec? Ref(JsonElement r) => r.ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(r[0].GetString()!, 16))[r[1].GetInt32()];
            foreach (JsonElement r in after.GetProperty("records").EnumerateArray())
                expected[Convert.ToInt32(r[0].GetString()!, 16)][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
            var cache = snapshot.GetProperty("corps_cache").EnumerateObject().ToDictionary(p => Convert.ToUInt64(p.Name[2..], 16), p => Convert.ToUInt64(p.Value.GetString()![2..], 16));
            var members = snapshot.GetProperty("nested").EnumerateObject().Where(p => p.Name.StartsWith("caution:"))
                .ToDictionary(p => int.Parse(p.Name[8..]), p => p.Value.EnumerateArray().Select(e => Pointer(e.GetString()!)).ToList());
            string wrapper = root.GetProperty("output_wrapper").GetString()!;
            var output = snapshot.GetProperty("nested").GetProperty(wrapper).EnumerateArray().Select(e => Pointer(e.GetString()!)).ToList();
            var lookup = snapshot.GetProperty("doctrine_byid").EnumerateArray().Select(i => w.Table(Tables.Doctrine)[i.GetInt32()]).ToArray();
            List<string> host = [];
            var policy = new RulerPolicy(w, lookup, () => host.Add("0x1f0ad0"));
            byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
            var flags = snapshot.GetProperty("flags").EnumerateObject().ToDictionary(p => p.Name, p => w.Value(p.Name));
            switch (kind)
            {
                case "reset": policy.Apply(Object(Tables.Forces, "force"), Object(Tables.Officers, "successor"), Object(Tables.Officers, "old"), f.GetProperty("notify").GetBoolean()); break;
                case "advisor": RulerPolicy.ClearAdvisor(w, Object(Tables.Forces, "force"), f.GetProperty("refresh").GetBoolean()); break;
                case "field": RulerPolicy.ClearField(w, Object(Tables.Forces, "force"), f.GetProperty("field").GetInt32()); break;
                case "cost":
                    byte[]? cost = RulerPolicy.DoctrineCost(w, f.GetProperty("panel").GetInt32(), Object(Tables.Officers, "person"));
                    Assert.Equal(c.GetProperty("cost").ValueKind == JsonValueKind.Null ? null : Convert.FromHexString(c.GetProperty("cost").GetString()!), cost);
                    break;
                case "refund": policy.RefundDoctrine(Object(Tables.Forces, "force")!, Object(Tables.Officers, "person")); break;
                case "doctrine": policy.ResetDoctrine(Object(Tables.Forces, "force")!); break;
                case "gate": policy.ClampGateTroops(Object(Tables.Forces, "force")!); break;
                case "facility": policy.ClampFacilityDurability(Object(Tables.Forces, "force")!); break;
                case "change":
                    RulerChange.Apply(w, new CorpsLifecycle(w, cache, members, () => true), Object(Tables.Officers, "dying"), Object(Tables.Officers, "successor"),
                        output, new RulerChangeHost(RefreshPresentation: () => host.Add("0x1b4e60")), policy);
                    break;
                default: throw new InvalidDataException("Unknown policy fixture");
            }
            if (c.TryGetProperty("host_events", out JsonElement events)) Assert.Equal(events.EnumerateArray().Select(e => e.GetString()), host);
            foreach (int table in tables)
                for (int i = 0; i < expected[table].Length; i++)
                    if (!expected[table][i].SequenceEqual(w.Table(table)[i].Bytes))
                        Assert.Fail($"{f.GetProperty("name").GetString()}: {table:X}[{i}] differs at " + string.Join(",", expected[table][i].Select((b, at) => (b, at))
                            .Where(v => v.b != w.Table(table)[i].Bytes[v.at]).Select(v => $"{v.at:X}")));
            Assert.Equal(after.TryGetProperty("worldstate", out JsonElement ws) ? Convert.FromHexString(ws.GetString()!) : state, w.WorldState.Bytes);
            Assert.Equal(after.TryGetProperty("settings", out JsonElement opts) ? Convert.FromHexString(opts.GetString()!) : settings, w.Settings.Bytes);
            foreach (var (name, value) in flags) Assert.Equal(value, w.Value(name));
            var lists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("items"));
            if (after.TryGetProperty("lists", out JsonElement changedLists)) foreach (JsonProperty p in changedLists.EnumerateObject()) lists[p.Name] = p.Value;
            foreach (var (head, list) in lists) Assert.Equal(list.EnumerateArray().Select(n => n.EnumerateArray().Select(Ref).ToArray()), w.List(Convert.ToInt32(head, 16)));
            foreach (JsonProperty p in snapshot.GetProperty("nested").EnumerateObject())
            {
                JsonElement value = after.TryGetProperty("nested", out JsonElement nested) && nested.TryGetProperty(p.Name, out JsonElement changed) ? changed : p.Value;
                IEnumerable<string> actual = p.Name == wrapper ? output.Select(r => $"0x{r?.NativeAddress ?? 0:x}") :
                    p.Name.StartsWith("caution:") ? members[int.Parse(p.Name[8..])].Select(r => $"0x{r?.NativeAddress ?? 0:x}") : p.Value.EnumerateArray().Select(e => e.GetString()!);
                Assert.Equal(value.EnumerateArray().Select(e => e.GetString()!), actual);
            }
            JsonElement cacheExpected = after.TryGetProperty("corps_cache", out JsonElement changedCache) ? changedCache : snapshot.GetProperty("corps_cache");
            Assert.Equal(cacheExpected.EnumerateObject().Count(), cache.Count);
            foreach (JsonProperty p in cacheExpected.EnumerateObject()) Assert.Equal(Convert.ToUInt64(p.Value.GetString()![2..], 16), cache[Convert.ToUInt64(p.Name[2..], 16)]);
            foreach (JsonProperty p in after.EnumerateObject()) Assert.Contains(p.Name, new[] { "records", "lists", "nested", "corps_cache", "worldstate", "settings" });
            cases++;
        }
        Assert.True(cases > 0);
        Assert.Equal(root.GetProperty("groups").GetProperty(group).GetInt32(), cases);
    }
}

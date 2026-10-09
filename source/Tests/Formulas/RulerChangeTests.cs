using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class RulerChangeTests
{
    private sealed class PlayerPresentationBoundary : Exception { }

    [Fact]
    public void PlayerRecordsMatchObservedNativePrefixBeforeCorpsPresentation()
    {
        JsonElement root = Vectors.Read("player_ruler_prefix.json.gz");
        Assert.False(root.GetProperty("function_complete").GetBoolean());
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.False(root.GetProperty("presentation_stubs").GetBoolean());
        Assert.Equal("0x450d30", root.GetProperty("endpoint").GetString());
        string world = root.GetProperty("world").GetString()!;
        World w = Vectors.LoadWorld(world);
        JsonElement snapshot = Vectors.Read(world), c = root.GetProperty("case"), f = c.GetProperty("fixture"), after = c.GetProperty("after");
        int[] tables = snapshot.GetProperty("tables").EnumerateObject().Select(t => Convert.ToInt32(t.Name, 16)).ToArray();
        var pointers = tables.SelectMany(w.Table).ToDictionary(r => r.NativeAddress);
        var expected = tables.ToDictionary(t => t, t => w.Table(t).Select(r => r.Bytes.ToArray()).ToArray());
        foreach (JsonElement r in after.GetProperty("records").EnumerateArray())
            expected[Convert.ToInt32(r[0].GetString()!, 16)][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
        var cache = snapshot.GetProperty("corps_cache").EnumerateObject().ToDictionary(p => Convert.ToUInt64(p.Name[2..], 16),
            p => Convert.ToUInt64(p.Value.GetString()![2..], 16));
        var members = snapshot.GetProperty("nested").EnumerateObject().Where(p => p.Name.StartsWith("caution:"))
            .ToDictionary(p => int.Parse(p.Name[8..]), p => p.Value.EnumerateArray().Select(e => (Rec?)pointers[Convert.ToUInt64(e.GetString()![2..], 16)]).ToList());
        List<Rec?> output = [];
        byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
        bool reached = false;
        Assert.Throws<PlayerPresentationBoundary>(() => RulerChange.ApplyWithoutPolicyReset(w,
            new CorpsLifecycle(w, cache, members, () => true), w.Table(Tables.Officers)[f.GetProperty("dying").GetInt32()],
            w.Table(Tables.Officers)[f.GetProperty("successor").GetInt32()], output, new RulerChangeHost(RefreshPlayerCorps: corps =>
            {
                Assert.Equal(Convert.ToUInt64(c.GetProperty("refresh_corps").GetString()![2..], 16), corps.NativeAddress);
                reached = true;
                throw new PlayerPresentationBoundary();
            }, RefreshPlayerStrategy: () => Assert.Fail("Strategy refresh is beyond the observed native prefix"),
            RefreshPresentation: () => Assert.Fail("Final refresh is beyond the observed native prefix"))));
        Assert.True(reached);
        foreach (int table in tables)
            for (int i = 0; i < expected[table].Length; i++) Assert.Equal(expected[table][i], w.Table(table)[i].Bytes);
        Assert.Equal(state, w.WorldState.Bytes);
        Assert.Equal(settings, w.Settings.Bytes);
        Assert.Equal(c.GetProperty("officers").EnumerateArray().Select(e => $"0x{Convert.ToUInt64(e.GetString()![2..], 16):x}"),
            output.Select(r => $"0x{r!.NativeAddress:x}"));
        foreach (JsonProperty p in after.EnumerateObject()) Assert.Contains(p.Name, new[] { "records", "nested" });
        foreach (JsonProperty p in snapshot.GetProperty("nested").EnumerateObject())
        {
            JsonElement value = after.TryGetProperty("nested", out JsonElement nested) && nested.TryGetProperty(p.Name, out JsonElement changed) ? changed : p.Value;
            var actual = p.Name == root.GetProperty("output_wrapper").GetString() ? output.Select(r => $"0x{r!.NativeAddress:x}") :
                p.Name.StartsWith("caution:") ? members[int.Parse(p.Name[8..])].Select(r => $"0x{r!.NativeAddress:x}") : p.Value.EnumerateArray().Select(e => e.GetString()!);
            Assert.Equal(value.EnumerateArray().Select(e => e.GetString()!), actual);
        }
    }

    [Theory]
    [InlineData("change", 22)]
    [InlineData("loyalty", 49)]
    [InlineData("diplomatic", 36)]
    [InlineData("force_cache", 18)]
    public void CompleteNativeBodiesMatchEveryRecordListAndCache(string group, int count)
    {
        JsonElement root = Vectors.Read("ruler_change.json.gz");
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.False(root.GetProperty("presentation_stubs").GetBoolean());
        Assert.False(root.GetProperty("policy_reset").GetBoolean());
        string world = root.GetProperty("world").GetString()!;
        JsonElement snapshot = Vectors.Read(world);
        int cases = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture"), after = c.GetProperty("after");
            string kind = f.GetProperty("kind").GetString()!;
            bool diplomatic = kind is "slot" or "diplomatic_officer" or "clear";
            if (group == "diplomatic" ? !diplomatic : group != kind) continue;
            World w = Vectors.LoadWorld(world);
            Vectors.Poke(w, f.GetProperty("pokes"));
            int[] tables = snapshot.GetProperty("tables").EnumerateObject().Select(t => Convert.ToInt32(t.Name, 16)).ToArray();
            var pointers = tables.SelectMany(w.Table).ToDictionary(r => r.NativeAddress);
            Rec? Pointer(string value) => value == "0x0" ? null : pointers[Convert.ToUInt64(value[2..], 16)];
            Rec? Ref(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(e[0].GetString()!, 16))[e[1].GetInt32()];
            var expected = tables.ToDictionary(t => t, t => w.Table(t).Select(r => r.Bytes.ToArray()).ToArray());
            foreach (JsonElement r in after.GetProperty("records").EnumerateArray())
                expected[Convert.ToInt32(r[0].GetString()!, 16)][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
            byte[] expectedWorld = after.TryGetProperty("worldstate", out JsonElement ws) ? Convert.FromHexString(ws.GetString()!) : w.WorldState.Bytes.ToArray();
            byte[] expectedSettings = after.TryGetProperty("settings", out JsonElement settings) ? Convert.FromHexString(settings.GetString()!) : w.Settings.Bytes.ToArray();
            Dictionary<ulong, ulong> Cache(string name) => snapshot.GetProperty(name).EnumerateObject().ToDictionary(
                p => Convert.ToUInt64(p.Name[2..], 16), p => Convert.ToUInt64(p.Value.GetString()![2..], 16));
            var corpsCache = Cache("corps_cache");
            var forceCache = Cache("force_cache");
            var members = snapshot.GetProperty("nested").EnumerateObject().Where(p => p.Name.StartsWith("caution:"))
                .ToDictionary(p => int.Parse(p.Name[8..]), p => p.Value.EnumerateArray().Select(e => Pointer(e.GetString()!)).ToList());
            var output = snapshot.GetProperty("nested").GetProperty(root.GetProperty("output_wrapper").GetString()!)
                .EnumerateArray().Select(e => Pointer(e.GetString()!)).ToList();
            Rec? Object(int table, string key) => f.GetProperty(key).ValueKind == JsonValueKind.Null ? null : w.Table(table)[f.GetProperty(key).GetInt32()];
            List<string> calls = [];
            switch (kind)
            {
                case "change":
                    RulerChange.ApplyWithoutPolicyReset(w, new CorpsLifecycle(w, corpsCache, members, () => true),
                        Object(Tables.Officers, "dying"), Object(Tables.Officers, "successor"), output,
                        new RulerChangeHost(RefreshPresentation: () => calls.Add("0x1b4e60")));
                    Assert.Equal(c.GetProperty("officers").EnumerateArray().Select(e => Pointer(e.GetString()!)), output);
                    if (c.TryGetProperty("host_events", out JsonElement events))
                        Assert.Equal(events.EnumerateArray().Select(e => e[0].GetString()!), calls);
                    break;
                case "loyalty":
                    Assert.Equal(c.GetProperty("result").GetInt32(), RulerChange.Loyalty(w,
                        Object(Tables.Officers, "person")!, Object(Tables.Forces, "force")!, f.GetProperty("tenure").GetBoolean()));
                    break;
                case "slot":
                    Assert.Equal(c.GetProperty("result").GetInt32(), ForceRelations.DiplomaticSlot(Object(Tables.Forces, "force")!,
                        f.GetProperty("destination").GetUInt32(), f.GetProperty("allow_empty").GetBoolean()));
                    break;
                case "diplomatic_officer":
                    Assert.Same(Pointer($"0x{c.GetProperty("result").GetUInt64():x}"), ForceRelations.DiplomaticOfficer(w,
                        Object(Tables.Forces, "force")!, f.GetProperty("slot").GetUInt32()));
                    break;
                case "clear":
                    Rec? removed = ForceRelations.ClearDiplomaticAppointment(w, Object(Tables.Forces, "force"), Object(Tables.Forces, "destination"));
                    Assert.Equal(c.GetProperty("result").GetInt32(), removed is null ? 0 : 1);
                    break;
                case "force_cache": ForceFall.RemoveForceCache(forceCache, Convert.ToUInt64(f.GetProperty("key").GetString()![2..], 16)); break;
                default: throw new InvalidDataException("Unknown ruler fixture");
            }
            foreach (int table in tables)
                for (int i = 0; i < expected[table].Length; i++)
                {
                    byte[] actual = w.Table(table)[i].Bytes;
                    if (!expected[table][i].SequenceEqual(actual))
                        Assert.Fail($"{f.GetProperty("name").GetString()}: {table:X}[{i}] differs at " +
                            string.Join(",", expected[table][i].Select((b, at) => (b, at)).Where(v => v.b != actual[v.at]).Select(v => $"{v.at:X}")));
                }
            Assert.Equal(expectedWorld, w.WorldState.Bytes);
            Assert.Equal(expectedSettings, w.Settings.Bytes);
            var expectedLists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("items"));
            if (after.TryGetProperty("lists", out JsonElement lists)) foreach (JsonProperty p in lists.EnumerateObject()) expectedLists[p.Name] = p.Value;
            foreach (var (head, list) in expectedLists)
                Assert.Equal(list.EnumerateArray().Select(n => n.EnumerateArray().Select(Ref).ToArray()), w.List(Convert.ToInt32(head, 16)));
            foreach (JsonProperty p in snapshot.GetProperty("nested").EnumerateObject())
            {
                JsonElement value = after.TryGetProperty("nested", out JsonElement nested) ? nested.GetProperty(p.Name) : p.Value;
                string Address(Rec? r) => $"0x{r?.NativeAddress ?? 0:x}";
                IEnumerable<string> actual = p.Name == root.GetProperty("output_wrapper").GetString() ? output.Select(Address) :
                    p.Name.StartsWith("caution:") ? members[int.Parse(p.Name[8..])].Select(Address) : p.Value.EnumerateArray().Select(e => e.GetString()!);
                Assert.Equal(value.EnumerateArray().Select(e => e.GetString()!), actual);
            }
            foreach (string name in new[] { "corps_cache", "force_cache" })
            {
                JsonElement value = after.TryGetProperty(name, out JsonElement changed) ? changed : snapshot.GetProperty(name);
                var actual = name == "corps_cache" ? corpsCache : forceCache;
                Assert.Equal(value.EnumerateObject().Count(), actual.Count);
                foreach (JsonProperty p in value.EnumerateObject()) Assert.Equal(Convert.ToUInt64(p.Value.GetString()![2..], 16), actual[Convert.ToUInt64(p.Name[2..], 16)]);
            }
            foreach (JsonProperty p in after.EnumerateObject()) Assert.Contains(p.Name, new[] { "records", "lists", "nested", "corps_cache", "force_cache" });
            cases++;
        }
        Assert.Equal(count, cases);
    }
}

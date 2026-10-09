using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class UnitPersonalityLifecycleTests
{
    private static ulong Address(string s) => Convert.ToUInt64(s[2..], 16);

    [Fact]
    public void DeactivationAndReversePropagationMatchCompleteNativeBodies()
    {
        var root = Vectors.Read("unit_personality_lifecycle.json.gz");
        var snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        Assert.True(root.GetProperty("native_pool").GetBoolean());
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            var fixture = c.GetProperty("fixture");
            string name = fixture.GetProperty("name").GetString()!;
            var tables = snapshot.GetProperty("tables").EnumerateObject()
                .ToDictionary(t => t.Name, t => w.Table(Convert.ToInt32(t.Name[2..], 16)));
            var pointers = tables.Values.SelectMany(t => t).ToDictionary(r => r.NativeAddress);
            var nested = snapshot.GetProperty("nested").EnumerateObject().ToDictionary(p => p.Name,
                p => p.Value.EnumerateArray().Select(e => Address(e.GetString()!) is var a && a != 0 ? pointers[a] : (Rec?)null).ToList());
            foreach (var p in fixture.GetProperty("propagated").EnumerateObject())
                nested["unit:" + p.Name] = p.Value.EnumerateArray()
                    .Select(e => e.ValueKind == System.Text.Json.JsonValueKind.Null ? null : w.Table(Tables.Units)[e.GetInt32()]).ToList();
            Vectors.Poke(w, fixture.GetProperty("pokes"));
            var expected = tables.ToDictionary(t => t.Key, t => t.Value.Select(r => r.Bytes.ToArray()).ToArray());
            byte[] worldBefore = w.WorldState.Bytes.ToArray(), settingsBefore = w.Settings.Bytes.ToArray();
            var refreshes = new List<int>();
            var lifecycle = new UnitPersonalityLifecycle(w, nested, fixture.GetProperty("refresh_available").GetBoolean(),
                unit => refreshes.Add(unit.Index));
            Rec target = w.Table(Tables.Units)[fixture.GetProperty("unit").GetInt32()];
            int slot = fixture.GetProperty("slot").GetInt32();
            switch (fixture.GetProperty("kind").GetString())
            {
                case "off": lifecycle.Deactivate(target, slot); break;
                case "drop": lifecycle.DropReceived(target, slot, fixture.GetProperty("notify").GetBoolean()); break;
                case "all_off":
                    for (int i = 0; i < 9; i++) lifecycle.Deactivate(target, i);
                    break;
                default: throw new InvalidOperationException("Unknown native personality fixture.");
            }
            Assert.Equal(c.GetProperty("refreshes").EnumerateArray().Select(e => e.GetInt32()), refreshes);
            var after = c.GetProperty("after");
            foreach (var r in after.GetProperty("records").EnumerateArray())
                expected[r[0].GetString()!][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
            foreach (var table in expected)
                for (int i = 0; i < table.Value.Length; i++)
                    Assert.True(table.Value[i].SequenceEqual(tables[table.Key][i].Bytes), $"{name}: {table.Key}[{i}] differs");
            var expectedNested = after.TryGetProperty("nested", out var changed) ? changed : snapshot.GetProperty("nested");
            foreach (var p in expectedNested.EnumerateObject())
            {
                IEnumerable<Rec?> entries;
                if (!after.TryGetProperty("nested", out _) && p.Name.StartsWith("unit:") &&
                    fixture.GetProperty("propagated").TryGetProperty(p.Name[5..], out var input))
                    entries = input.EnumerateArray().Select(e => e.ValueKind == System.Text.Json.JsonValueKind.Null
                        ? null : w.Table(Tables.Units)[e.GetInt32()]);
                else entries = p.Value.EnumerateArray().Select(e => Address(e.GetString()!) is var a && a != 0 ? pointers[a] : null);
                Assert.Equal(entries, nested[p.Name]);
            }
            Assert.Equal(worldBefore, w.WorldState.Bytes);
            Assert.Equal(settingsBefore, w.Settings.Bytes);
            Assert.All(after.EnumerateObject(), p => Assert.Contains(p.Name, new[] { "records", "nested" }));
        }
    }
}

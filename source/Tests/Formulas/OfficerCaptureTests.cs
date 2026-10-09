using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class OfficerCaptureTests
{
    [Fact]
    public void RetainedCorpsCaptureMatchesEveryNativeRecordAndCountyArray()
    {
        JsonElement root = Vectors.Read("officer_capture.json.gz");
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        int cases = 0, countyArrays = 0, corpsChanges = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            JsonElement f = c.GetProperty("fixture"), after = c.GetProperty("after");
            Rec? Ref(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null :
                w.Table(Convert.ToInt32(e[0].GetString()![2..], 16))[e[1].GetInt32()];
            Vectors.Poke(w, f.GetProperty("pokes"));
            JsonElement snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
            var before = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(t => t.Name,
                t => w.Table(Convert.ToInt32(t.Name[2..], 16)).Select(r => r.Bytes.ToArray()).ToArray());
            byte[] worldBefore = w.WorldState.Bytes.ToArray();
            int result = OfficerCapture.ApplyRetainedCorps(w, Ref(f.GetProperty("person")), Ref(f.GetProperty("unit")),
                Ref(f.GetProperty("force")), Ref(f.GetProperty("officer")));
            Assert.Equal(c.GetProperty("result").GetInt32(), result);
            foreach (JsonElement r in after.GetProperty("records").EnumerateArray())
            {
                before[r[0].GetString()!][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
                if (r[0].GetString() == "0xe0d0") corpsChanges++;
            }
            foreach ((string table, byte[][] records) in before)
                for (int i = 0; i < records.Length; i++)
                {
                    byte[] actual = w.Table(Convert.ToInt32(table[2..], 16))[i].Bytes;
                    Assert.True(records[i].SequenceEqual(actual), $"{f.GetProperty("name").GetString()}: {table}[{i}] differs at " +
                        string.Join(",", records[i].Select((b, at) => (b, at)).Where(x => x.b != actual[x.at]).Select(x => $"{x.at:X}")));
                }
            Assert.Equal(worldBefore, w.WorldState.Bytes);
            foreach (JsonProperty section in after.EnumerateObject())
                Assert.Contains(section.Name, new[] { "records", "officer_counties" });
            var arrays = after.TryGetProperty("officer_counties", out JsonElement changed)
                ? changed.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!) : new Dictionary<string, string>();
            JsonElement extras = snapshot.GetProperty("tables").GetProperty("0x73a50").GetProperty("extra");
            foreach (JsonProperty p in extras.EnumerateObject())
                if (p.Value.TryGetProperty("vec1D0", out JsonElement input))
                {
                    string expected = arrays.TryGetValue(p.Name, out string? output) ? output : input.GetString()!;
                    Assert.Equal(Convert.FromHexString(expected), w.Table(Tables.Officers)[int.Parse(p.Name)].Vector1D0);
                }
            countyArrays += arrays.Count;
            cases++;
        }
        Assert.Equal(root.GetProperty("cases").GetArrayLength(), cases);
        Assert.True(countyArrays > 0);
        Assert.True(corpsChanges > 0);
    }
}

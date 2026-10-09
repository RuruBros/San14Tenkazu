using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ForceFallCleanupTests
{
    [Fact]
    public void StandingsMatchCompleteNativeCalls() => Verify("force_standings.json.gz", 21, 21, 0, 0);

    [Fact]
    public void ItemTransferAndForceUnregisterMatchCompleteNativeCalls() => Verify("force_fall_cleanup.json.gz", 20, 0, 14, 6);

    private static void Verify(string vector, int count, int expectedStandings, int expectedItems, int expectedUnregister)
    {
        JsonElement root = Vectors.Read(vector);
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.True(root.GetProperty("available_pools").GetBoolean());
        Assert.Equal(count, root.GetProperty("cases").GetArrayLength());
        string world = root.GetProperty("world").GetString()!;
        World w = Vectors.LoadWorld(world);
        int[] tables = Vectors.Read(world).GetProperty("tables").EnumerateObject()
            .Select(t => Convert.ToInt32(t.Name, 16)).Order().ToArray();
        byte[] OtherState()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (int table in tables.Where(t => t is not (Tables.Forces or Tables.Items)))
                foreach (Rec r in w.Table(table)) hash.AppendData(r.Bytes);
            hash.AppendData(w.WorldState.Bytes);
            hash.AppendData(w.Settings.Bytes);
            return hash.GetHashAndReset();
        }
        string[][] ForceListSnapshot() => w.List(Lists.ForceList).Select(n => n.Select(r => r is null
            ? "null" : $"{r.Table:x}:{r.Index}").ToArray()).ToArray();
        int standings = 0, items = 0, unregister = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture");
            var original = new[] { Tables.Forces, Tables.Items }.ToDictionary(t => t,
                t => w.Table(t).Select(r => r.Bytes.ToArray()).ToArray());
            Rec?[][] listOriginal = w.List(Lists.ForceList).ToArray();
            Action undo = Vectors.Poke(w, f.GetProperty("pokes"));
            try
            {
                if (f.GetProperty("kind").GetString() == "unregister")
                {
                    var list = w.List(Lists.ForceList);
                    list.Clear();
                    list.AddRange(f.GetProperty("members").EnumerateArray().Select(i => new Rec?[] {
                        i.ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Forces)[i.GetInt32()] }));
                }
                var expected = new[] { Tables.Forces, Tables.Items }.ToDictionary(t => t,
                    t => w.Table(t).Select(r => r.Bytes.ToArray()).ToArray());
                foreach (JsonElement r in c.GetProperty("after").GetProperty("records").EnumerateArray())
                    expected[Convert.ToInt32(r[0].GetString()!, 16)][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
                string[][] expectedList = ForceListSnapshot();
                if (c.GetProperty("after").TryGetProperty("lists", out JsonElement nativeLists))
                    expectedList = nativeLists.GetProperty("0xc0").EnumerateArray().Select(n => n.EnumerateArray()
                        .Select(r => r.ValueKind == JsonValueKind.Null ? "null" :
                            $"{Convert.ToInt32(r[0].GetString()!, 16):x}:{r[1].GetInt32()}").ToArray()).ToArray();
                byte[] otherState = OtherState();
                Rec? Ref(int table, string key) => f.GetProperty(key).ValueKind == JsonValueKind.Null
                    ? null : w.Table(table)[f.GetProperty(key).GetInt32()];
                switch (f.GetProperty("kind").GetString())
                {
                    case "standings":
                        Assert.Equal(c.GetProperty("result").GetInt32(), ForceFall.UpdateStandings(w));
                        standings++;
                        break;
                    case "items":
                        ForceFall.TransferItemsToRuler(w, Ref(Tables.Officers, "person"), Ref(Tables.Forces, "victor"));
                        items++;
                        break;
                    case "unregister":
                        Assert.Equal(c.GetProperty("result").GetInt32(), ForceFall.RemoveFromForceList(w, Ref(Tables.Forces, "target")));
                        unregister++;
                        break;
                    default: throw new InvalidDataException("Unknown native cleanup case");
                }
                foreach (int table in expected.Keys)
                    Assert.Equal(expected[table].Select(Convert.ToHexString), w.Table(table).Select(r => Convert.ToHexString(r.Bytes)));
                Assert.Equal(expectedList, ForceListSnapshot());
                Assert.Equal(otherState, OtherState());
            }
            finally
            {
                undo();
                foreach (int table in original.Keys)
                    for (int i = 0; i < original[table].Length; i++) original[table][i].CopyTo(w.Table(table)[i].Bytes, 0);
                var list = w.List(Lists.ForceList);
                list.Clear();
                list.AddRange(listOriginal);
            }
        }
        Assert.Equal(expectedStandings, standings);
        Assert.Equal(expectedItems, items);
        Assert.Equal(expectedUnregister, unregister);
    }
}

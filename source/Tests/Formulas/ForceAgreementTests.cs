using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ForceAgreementTests
{
    [Fact]
    public void PlayerFlagPolicyRefreshOrderAndEmptyVectorResetMatchNative()
    {
        JsonElement root = Vectors.Read("force_agreement.json.gz");
        Assert.False(root.GetProperty("gameplay_stubs").GetBoolean());
        Assert.True(root.GetProperty("ui_body_native").GetBoolean());
        Assert.True(root.GetProperty("ui_effects_external").GetBoolean());
        Assert.True(root.GetProperty("empty_vectors_only").GetBoolean());
        Assert.Equal(32, root.GetProperty("cases").GetArrayLength());
        JsonElement input = Vectors.Read(root.GetProperty("world").GetString()!);
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        int[] tables = input.GetProperty("tables").EnumerateObject().Select(t => Convert.ToInt32(t.Name, 16))
            .Where(t => t != Tables.Forces).Order().ToArray();
        byte[] OtherState()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (int table in tables)
                foreach (Rec r in w.Table(table)) hash.AppendData(r.Bytes);
            hash.AppendData(w.WorldState.Bytes);
            hash.AppendData(w.Settings.Bytes);
            return hash.GetHashAndReset();
        }
        int agreements = 0, resets = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement fixture = c.GetProperty("fixture");
            byte[][] original = w.Table(Tables.Forces).Select(r => r.Bytes.ToArray()).ToArray();
            Action undo = Vectors.Poke(w, fixture.GetProperty("pokes"));
            try
            {
                byte[] otherState = OtherState();
                byte[][] expected = w.Table(Tables.Forces).Select(r => r.Bytes.ToArray()).ToArray();
                foreach (JsonElement record in c.GetProperty("after").GetProperty("records").EnumerateArray())
                    expected[record[1].GetInt32()] = Convert.FromHexString(record[2].GetString()!);
                var nested = input.GetProperty("nested").EnumerateObject().ToDictionary(p => p.Name,
                    p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToList());
                var refreshes = new List<(int City, string[] Forces)>();
                var clears = new List<int[]>();
                int target = fixture.GetProperty("target").GetInt32();
                int[] forceIndices = fixture.TryGetProperty("other", out JsonElement other)
                    ? new[] { target, other.GetInt32() }.Distinct().Order().ToArray() : [target];
                Action<Rec>? refresh = fixture.GetProperty("refresh").GetBoolean() ? city =>
                    refreshes.Add((city.Index, forceIndices.Select(i => Convert.ToHexString(w.Table(Tables.Forces)[i].Bytes).ToLowerInvariant()).ToArray())) : null;
                Rec force = w.Table(Tables.Forces)[target];
                if (fixture.GetProperty("kind").GetString() == "reset")
                {
                    ForceReset.ApplyWithoutVector(w, force, true, at =>
                    {
                        clears.Add([target, at]);
                        nested[$"force:{target}:{at:x}"].Clear();
                    }, refresh);
                    resets++;
                }
                else
                {
                    ForceRelations.SetAgreement(w, force, w.Table(Tables.Forces)[other.GetInt32()],
                        fixture.GetProperty("value").GetInt32(), refresh);
                    agreements++;
                }
                Assert.Equal(c.GetProperty("refreshes").GetArrayLength(), refreshes.Count);
                int atRefresh = 0;
                foreach (JsonElement r in c.GetProperty("refreshes").EnumerateArray())
                {
                    Assert.Equal(r.GetProperty("city").GetInt32(), refreshes[atRefresh].City);
                    Assert.Equal(r.GetProperty("forces").EnumerateArray().Select(v => v.GetString()!), refreshes[atRefresh++].Forces);
                }
                Assert.Equal(c.GetProperty("clears").EnumerateArray().Select(v => v.EnumerateArray().Select(i => i.GetInt32()).ToArray()), clears);
                Assert.Equal(expected.Select(Convert.ToHexString), w.Table(Tables.Forces).Select(r => Convert.ToHexString(r.Bytes)));
                Assert.Equal(otherState, OtherState());
                JsonElement expectedNested = c.GetProperty("after").TryGetProperty("nested", out JsonElement changed)
                    ? changed : input.GetProperty("nested");
                foreach (JsonProperty p in expectedNested.EnumerateObject())
                    Assert.Equal(p.Value.EnumerateArray().Select(v => v.GetString()!), nested[p.Name]);
            }
            finally
            {
                undo();
                for (int i = 0; i < original.Length; i++) original[i].CopyTo(w.Table(Tables.Forces)[i].Bytes, 0);
            }
        }
        Assert.Equal(27, agreements);
        Assert.Equal(5, resets);
    }
}

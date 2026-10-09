using System.Buffers.Binary;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class ForceResetTests
{
    [Fact]
    public void CompleteResetAndBilateralAgreementMatchNative()
    {
        JsonElement root = Vectors.Read("force_reset.json.gz");
        Assert.True(root.GetProperty("no_stubs").GetBoolean());
        Assert.True(root.GetProperty("native_pool").GetBoolean());
        Assert.True(root.GetProperty("empty_vectors_only").GetBoolean());
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        JsonElement input = Vectors.Read(root.GetProperty("world").GetString()!);
        int[] tables = input.GetProperty("tables").EnumerateObject().Select(t => Convert.ToInt32(t.Name, 16)).ToArray();
        int resets = 0, agreements = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            JsonElement f = c.GetProperty("fixture");
            string name = f.GetProperty("name").GetString()!;
            var original = tables.ToDictionary(t => t, t => w.Table(t).Select(r => r.Bytes.ToArray()).ToArray());
            Action undo = Vectors.Poke(w, f.GetProperty("pokes"));
            try
            {
                var expected = tables.ToDictionary(t => t, t => w.Table(t).Select(r => r.Bytes.ToArray()).ToArray());
                foreach (JsonElement record in c.GetProperty("after").GetProperty("records").EnumerateArray())
                    expected[Convert.ToInt32(record[0].GetString()!, 16)][record[1].GetInt32()] =
                        Convert.FromHexString(record[2].GetString()!);
                byte[] state = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
                var nested = c.GetProperty("nested_before").EnumerateObject().ToDictionary(p => p.Name,
                    p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToList());
                var clears = new List<int>();
                Rec force = w.Table(Tables.Forces)[f.GetProperty("target").GetInt32()];
                if (f.GetProperty("kind").GetString() == "reset")
                {
                    ForceReset.ApplyWithoutPlayerOrVector(w, force, f.GetProperty("enabled").GetBoolean(), at =>
                    {
                        clears.Add(at);
                        nested[$"force:{force.Index}:{at:x}"].Clear();
                    });
                    resets++;
                }
                else
                {
                    ForceRelations.SetAgreementNpc(w, force, w.Table(Tables.Forces)[f.GetProperty("other").GetInt32()],
                        f.GetProperty("value").GetInt32());
                    agreements++;
                }
                Assert.Equal(c.GetProperty("clear_calls").EnumerateArray().Select(x => x.GetInt32()), clears);
                foreach (int table in tables)
                    for (int i = 0; i < expected[table].Length; i++)
                        Assert.True(expected[table][i].AsSpan().SequenceEqual(w.Table(table)[i].Bytes), $"{name}: {table:X}/{i}");
                Assert.Equal(state, w.WorldState.Bytes);
                Assert.Equal(settings, w.Settings.Bytes);
                if (c.GetProperty("after").TryGetProperty("nested", out JsonElement after))
                    foreach (JsonProperty p in after.EnumerateObject().Where(p => p.Name.StartsWith("force:")))
                        Assert.Equal(p.Value.EnumerateArray().Select(v => v.GetString()!), nested[p.Name]);
                else
                    foreach (JsonProperty p in c.GetProperty("nested_before").EnumerateObject())
                        Assert.Equal(p.Value.EnumerateArray().Select(v => v.GetString()!), nested[p.Name]);
            }
            finally
            {
                undo();
                foreach (int table in tables)
                    for (int i = 0; i < original[table].Length; i++) original[table][i].CopyTo(w.Table(table)[i].Bytes, 0);
            }
        }
        Assert.Equal(18, resets);
        Assert.Equal(23, agreements);
    }

    [Fact]
    public void UnsupportedPlayerAndHeapBodiesRejectBeforeAnyWrite()
    {
        JsonElement root = Vectors.Read("force_reset.json.gz");
        foreach (bool player in new[] { true, false })
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            Rec force = w.Table(Tables.Forces)[45];
            w.WorldState.Bytes[0x3A] = player ? (byte)51 : (byte)0;
            if (!player) BinaryPrimitives.WriteUInt64LittleEndian(force.Bytes.AsSpan(0x1A0), 0x12345678);
            byte[][] before = w.Table(Tables.Forces).Select(r => r.Bytes.ToArray()).ToArray();
            byte[] state = w.WorldState.Bytes.ToArray();
            var clears = new List<int>();
            Assert.Throws<NotSupportedException>(() => ForceReset.ApplyWithoutPlayerOrVector(w, force, true, clears.Add));
            Assert.Empty(clears);
            Assert.Equal(before.Select(Convert.ToHexString), w.Table(Tables.Forces).Select(r => Convert.ToHexString(r.Bytes)));
            Assert.Equal(state, w.WorldState.Bytes);
        }
        World agreement = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        agreement.WorldState.Bytes[0x3A] = 45;
        byte[][] old = agreement.Table(Tables.Forces).Select(r => r.Bytes.ToArray()).ToArray();
        Assert.Throws<NotSupportedException>(() => ForceRelations.SetAgreementNpc(agreement,
            agreement.Table(Tables.Forces)[45], agreement.Table(Tables.Forces)[46], 0));
        Assert.Equal(old.Select(Convert.ToHexString), agreement.Table(Tables.Forces).Select(r => Convert.ToHexString(r.Bytes)));
    }
}

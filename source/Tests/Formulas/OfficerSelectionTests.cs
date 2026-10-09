using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class OfficerSelectionTests
{
    [Fact]
    public void SelectionRemovalMatchesNativeSlotsAndHostRequestOrder()
    {
        var root = Vectors.Read("officer_selection.json.gz");
        var snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        int requests = 0;
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            var f = c.GetProperty("fixture");
            Vectors.Poke(w, f.GetProperty("pokes"));
            byte[] expected = w.WorldState.Bytes.ToArray(), settings = w.Settings.Bytes.ToArray();
            var records = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(t => t.Name,
                t => w.Table(Convert.ToInt32(t.Name[2..], 16)).Select(r => r.Bytes.ToArray()).ToArray());
            var observed = new List<string>();
            OfficerSelection.Remove(w, w.Table(Tables.Officers)[f.GetProperty("person").GetInt32()],
                () => observed.Add(Convert.ToHexString(w.WorldState.Bytes.AsSpan(0x165D, 31)).ToLowerInvariant()));
            var events = c.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "host_lookup");
            Assert.Equal(events.Select(e => e.GetProperty("selection").GetString()), observed);
            requests += observed.Count;
            var after = c.GetProperty("after");
            if (after.TryGetProperty("worldstate", out var state)) expected = Convert.FromHexString(state.GetString()!);
            Assert.Equal(expected, w.WorldState.Bytes);
            Assert.Equal(settings, w.Settings.Bytes);
            Assert.Empty(after.GetProperty("records").EnumerateArray());
            foreach (var table in records)
                for (int i = 0; i < table.Value.Length; i++)
                    Assert.Equal(table.Value[i], w.Table(Convert.ToInt32(table.Key[2..], 16))[i].Bytes);
            foreach (var section in after.EnumerateObject()) Assert.Contains(section.Name, new[] { "records", "worldstate" });
        }
        Assert.Equal(2, requests);
    }
}

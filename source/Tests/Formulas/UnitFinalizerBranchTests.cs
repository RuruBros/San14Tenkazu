using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class UnitFinalizerBranchTests
{
    private static readonly int[] Focus = [Tables.Units, Tables.Troops, Tables.CautionTroops, Tables.CautionUnits];

    private static Dictionary<int, byte[][]> Records(World w) => Focus.ToDictionary(t => t,
        t => w.Table(t).Select(r => r.Bytes.ToArray()).ToArray());

    private static void Apply(Dictionary<int, byte[][]> records, JsonElement changes)
    {
        foreach (JsonElement row in changes.EnumerateArray())
            records[Convert.ToInt32(row[0].GetString()!, 16)][row[1].GetInt32()] = Convert.FromHexString(row[2].GetString()!);
    }

    private static void CopyTo(World w, Dictionary<int, byte[][]> records)
    {
        foreach ((int table, byte[][] rows) in records)
            for (int i = 0; i < rows.Length; i++) rows[i].CopyTo(w.Table(table)[i].Bytes, 0);
    }

    private static void AssertRecords(World w, Dictionary<int, byte[][]> expected, string name)
    {
        foreach ((int table, byte[][] rows) in expected)
            for (int i = 0; i < rows.Length; i++)
                Assert.True(rows[i].SequenceEqual(w.Table(table)[i].Bytes), $"{name}: table {table:X}, record {i}");
    }

    private static List<Rec?>? Members(World w, int table, JsonElement members) =>
        members.ValueKind == JsonValueKind.Null ? null : members.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.Null ? null : w.Table(table)[e.GetInt32()]).ToList();

    private static void SetUnitList(World w, int head, JsonElement members)
    {
        List<Rec?[]> list = w.List(head);
        list.Clear();
        list.AddRange(Members(w, Tables.Units, members)!.Select(r => new Rec?[] { r }));
    }

    [Fact]
    public void CautionCleanupAndCommanderSequenceMatchActualFinalizerCheckpoints()
    {
        JsonElement root = Vectors.Read("unit_finalizer_branches.json.gz");
        Assert.True(root.GetProperty("native_finalizer").GetBoolean());
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Dictionary<int, byte[][]> baseline = Records(w);
        int n = 0, removed = 0, duplicateCase = 0, absentCase = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            Dictionary<int, byte[][]> expected = Records(w);
            Apply(expected, c.GetProperty("inputs"));
            CopyTo(w, expected);
            SetUnitList(w, Lists.UnitList, c.GetProperty("active"));
            SetUnitList(w, Lists.EngagedUnits, c.GetProperty("pending"));
            List<Rec?>? registered = Members(w, Tables.CautionUnits, c.GetProperty("caution_units"));
            var lists = c.GetProperty("caution_troops").EnumerateArray().Select((e, i) => (i, list: Members(w, Tables.Units, e)))
                .Where(p => p.list is not null).ToDictionary(p => p.i, p => p.list!);
            int before = (registered?.Count ?? 0) + lists.Values.Sum(l => l.Count);
            try
            {
                UnitLifecycle.CleanupCautions(w, w.Table(Tables.Units)[c.GetProperty("unit").GetInt32()], registered, lists);
                Apply(expected, c.GetProperty("cleanup_records"));
                AssertRecords(w, expected, name);
                Assert.Equal(Members(w, Tables.CautionUnits, c.GetProperty("cleanup_units")), registered);
                foreach ((int i, List<Rec?> list) in lists)
                    Assert.Equal(Members(w, Tables.Units, c.GetProperty("cleanup_troops")[i]), list);
                int difference = before - (registered?.Count ?? 0) - lists.Values.Sum(l => l.Count);
                Assert.Equal(c.GetProperty("removed_nodes").GetInt32(), difference);
                removed += difference;
                if (name == "caution_duplicates_and_unregistered")
                {
                    // Entry zero and an invalid, unregistered record are both reset by the native table scan.
                    Assert.Contains(0, c.GetProperty("cleared").EnumerateArray().Select(e => e.GetInt32()));
                    Assert.Equal(0U, w.Table(Tables.CautionUnits)[500].U32(0x10));
                    Assert.True(difference >= 8);
                    duplicateCase++;
                }
                if (registered is null) absentCase++;
                UnitLifecycle.ReplaceCommander(w, w.Table(Tables.Units)[c.GetProperty("unit").GetInt32()],
                    c.GetProperty("member_list_available").GetBoolean());
                Apply(expected, c.GetProperty("commander_records"));
                AssertRecords(w, expected, name + " after commander replacement");
                n++;
            }
            finally { CopyTo(w, baseline); }
        }
        Assert.Equal(root.GetProperty("stats").GetProperty("cases").GetInt32(), n);
        Assert.Equal(root.GetProperty("stats").GetProperty("caution_nodes_removed").GetInt32(), removed);
        Assert.Equal(1, duplicateCase);
        Assert.Equal(1, absentCase);
    }

    [Fact]
    public void CommanderValidityOrderAndCompleteRecordsMatchActualFinalizerCheckpoints()
    {
        JsonElement root = Vectors.Read("unit_finalizer_branches.json.gz");
        World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
        Dictionary<int, byte[][]> baseline = Records(w);
        int n = 0, changes = 0, validGroups = 0, exhausted = 0;
        foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            Dictionary<int, byte[][]> expected = Records(w);
            Apply(expected, c.GetProperty("inputs"));
            Apply(expected, c.GetProperty("cleanup_records"));
            CopyTo(w, expected);
            SetUnitList(w, Lists.UnitList, c.GetProperty("active"));
            SetUnitList(w, Lists.EngagedUnits, c.GetProperty("pending"));
            try
            {
                Rec unit = w.Table(Tables.Units)[c.GetProperty("unit").GetInt32()];
                Rec troop = w.Entry(Tables.Troops, unit.U16(0x58), 500);
                bool valid = UnitLifecycle.TroopsValid(w, troop);
                bool available = c.GetProperty("member_list_available").GetBoolean();
                Assert.Equal(c.GetProperty("troops_valid").GetBoolean(), valid);
                Rec[] candidates = valid && available && ReferenceEquals(w.OfficerById(troop.U16(0x2C)), w.OfficerById(unit.U16(0x12)))
                    ? UnitLifecycle.TroopMembers(w, troop).ToArray() : [];
                Assert.Equal(c.GetProperty("candidates").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                    candidates.Select(r => r.Index).ToArray());
                UnitLifecycle.ReplaceCommander(w, unit, available);
                changes += w.Table(Tables.Troops).Count(r => !r.Bytes.SequenceEqual(expected[Tables.Troops][r.Index]));
                Apply(expected, c.GetProperty("commander_records"));
                AssertRecords(w, expected, name);
                if (valid) validGroups++;
                if (!available)
                {
                    Assert.True(valid);
                    Assert.Empty(candidates);
                    exhausted++;
                }
                n++;
            }
            finally { CopyTo(w, baseline); }
        }
        Assert.Equal(root.GetProperty("stats").GetProperty("cases").GetInt32(), n);
        Assert.Equal(root.GetProperty("stats").GetProperty("commander_changes").GetInt32(), changes);
        Assert.Equal(root.GetProperty("stats").GetProperty("valid_groups").GetInt32(), validGroups);
        Assert.Equal(1, exhausted);
    }
}

using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class EngagementTests
{
    // engagement.json.gz: fight [a, b, [powerA, atkA, defA, powerB, atkB, defB] bits, damage, defender after,
    // pokes] and siege [a, b, durability after, fell, pokes]; a side is [index, kind, hex, force, split].
    private static Engagement.Side Side(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32(), e[4].GetInt32());

    [Fact]
    public void BaseCapacityMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("capacity.json.gz").GetProperty("cases").EnumerateArray())
        {
            int table = Convert.ToInt32(c[0].GetString()![2..], 16), index = c[1].GetInt32();
            Action undo = Vectors.Poke(w, c[3]);
            int got = Engagement.MaxTroops(w, w.Table(table)[index]);
            undo();
            n++;
            if (got != c[2].GetInt32() && wrong.Count < 20)
                wrong.Add($"{c[0]}[{index}]: {got}, game {c[2]}; pokes {c[3]}");
        }
        Assert.True(n > 1_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void PowersMatchTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("engagement.json.gz").GetProperty("fight").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[5]);
            Engagement.Powers got = Engagement.Compute(w, Side(c[0]), Side(c[1]));
            undo();
            n++;
            uint[] bits = c[2].EnumerateArray().Select(x => x.GetUInt32()).ToArray();
            uint[] mine = [B(got.PowerA), B(got.AttackA), B(got.DefenseA), B(got.PowerB), B(got.AttackB), B(got.DefenseB)];
            if ((!mine.SequenceEqual(bits) || got.Damage != c[3].GetInt32()) && wrong.Count < 20)
                wrong.Add($"{c[0]} vs {c[1]}: {string.Join(",", mine.Select(F))} dmg {got.Damage}, game {string.Join(",", bits.Select(F))} dmg {c[3]}; pokes {c[5]}");
        }
        Assert.True(n > 1_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void ApplyMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0;
        foreach (JsonElement c in Vectors.Read("engagement.json.gz").GetProperty("fight").EnumerateArray())
        {
            Engagement.Side a = Side(c[0]), b = Side(c[1]);
            int[] expected = c[4].EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Action undo = Vectors.Poke(w, c[5]);
            int[] got;
            if (b.Kind == 0x1B)
            {
                var r = Engagement.ApplyUnit(w, w.Table(Tables.Units)[b.Index], c[3].GetInt32(), a);
                got = [r.Troops, r.Wounded, r.Morale, r.Gold, r.Food, r.Lit];
            }
            else
            {
                Rec baseRec = b.Kind == 5 ? w.Table(Tables.Cities)[b.Index] : w.Table(Tables.Gates)[b.Index];
                var r = Engagement.ApplyBase(w, baseRec, c[3].GetInt32(), a);
                got = [r.Troops, r.Wounded, r.Morale];
            }
            undo();
            n++;
            if (!got.SequenceEqual(expected) && wrong.Count < 20)
                wrong.Add($"{c[0]} vs {c[1]} dmg {c[3]}: [{string.Join(",", got)}], game [{string.Join(",", expected)}]; pokes {c[5]}");
        }
        Assert.True(n > 1_000, $"only {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void SiegeMatchesTheGamesCode()
    {
        World w = Vectors.LoadWorld("world.json.gz");
        var wrong = new List<string>();
        int n = 0, fought = 0, changed = 0;
        foreach (JsonElement c in Vectors.Read("engagement.json.gz").GetProperty("siege").EnumerateArray())
        {
            Action undo = Vectors.Poke(w, c[4]);
            Engagement.Side a = Side(c[0]), b = Side(c[1]);
            bool fight = Reach.CanAttack(w, a.Hex, b.Hex);
            Rec building = b.Kind == 5 ? w.Table(Tables.Buildings)[w.Table(Tables.Cities)[b.Index].U16(0x4E)] : b.Kind == 6 ? w.Table(Tables.Buildings)[w.Table(Tables.Gates)[b.Index].U16(0x4E)] : w.Table(Tables.Buildings)[b.Index];
            Bombard.Result got = fight ? Engagement.Siege(w, a, b) : new Bombard.Result(building.U16(0x14), false);
            if (fight) fought++;
            if (got.Durability != building.U16(0x14)) changed++;
            n++;
            int expectedDurability = c[2].GetInt32();
            int code = c[3].GetInt32();
            // Step1 answers 8 for a base that fell and 2 otherwise. Buildings always end the step when valid.
            int expectedCode = b.Kind == 0xB && (!fight || Map.BuildingValid(building)) || got.Fell ? 8 : 2;
            undo();
            if ((got.Durability != expectedDurability || code != expectedCode) && wrong.Count < 20)
                wrong.Add($"{c[0]} vs {c[1]}: {got.Durability}/{got.Fell}, game {expectedDurability}/{code}; pokes {c[4]}");
        }
        Assert.True(n > 300, $"only {n} cases");
        Assert.True(fought > 100 && changed > 50, $"only {fought} reachable fights and {changed} durability changes among {n} cases");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    private static uint B(float f) => BitConverter.SingleToUInt32Bits(f);
    private static string F(uint b) => BitConverter.UInt32BitsToSingle(b).ToString("R");
}

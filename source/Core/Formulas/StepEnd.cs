namespace San14Tenkazu.Formulas;

// What an engagement step leaves behind besides the troops (Battle_StepEnd 0x174E30): the 군 (county) of the
// defender's hex loses development, order and population, and the two forces' relation drops.
internal static class StepEnd
{
    [Rva(0x1A4899C)] internal const int OrderDivisor = 40;
    [Rva(0x1A49F98)] internal const int OrderFactor = 3;
    [Rva(0x1A48990)] internal const int OrderScale = 40;
    [Rva(0x1A49FA4)] internal const int OrderOffset = 30;
    [Rva(0x1A489A8)] internal const int PopulationLoss = 50;
    [Rva(0x1A489B4)] internal const int PopulationRandom = 50;
    [Rva(0x1A489D4)] internal const int RelationCap = 15;
    [Rva(0x1A489E0)] internal const int RelationLoss = -1;
    [Rva(0x1A48984)] internal const int DevelopmentLoss = 1;

    // A side of the engagement as the step record holds it: the object's index, its kind (0x1B unit, 5 city,
    // 6 gate, 0xB building), its hex and its force number.
    internal readonly record struct Side(int Index, int Kind, int Hex, int Force);

    // The county's values after the step, the relation bytes of the two forces and the development owner's +0x1BE.
    internal readonly record struct Result(int Agriculture, int Commerce, int Barracks, int Population, int Order,
        int RelationAB, int RelationBA, int OwnerPanel);

    // 0x174E30. The defender's hex names the county; the attacker (a) rolls the losses with its force's 주의
    // panel 0x30 as the chance to spare them (each development value loses 1). Returns the fields as they stand afterwards (or as they were).
    internal static Result Apply(World w, Side a, Side b)
    {
        Rec tile = w.Entry(Tables.Tiles, b.Hex, 0xBD0F);
        Rec county = w.Entry(Tables.Counties, tile.U16(0x12), 0x1F4);
        int agri = county.U16(0x3C), comm = county.U16(0x3E), barr = county.U16(0x40), pop = county.U16(0x52), order = county.U8(0x54);
        Rec fa = w.Entry(Tables.Forces, a.Force, 0x33), fb = w.Entry(Tables.Forces, b.Force, 0x33);
        int relAB = fb.Index < 0x34 ? fa.U8(0x13 + fb.Index) : 0, relBA = fa.Index < 0x34 ? fb.U8(0x13 + fa.Index) : 0;
        Rec owner = CountyOwner(w, county);
        int panel = owner.I16(0x1BE);

        if (Objects.CountyValid(county))
        {
            bool roll = true;
            if (county.U16(0x36) != 0)
            {
                Rec governor = w.OfficerById(county.U16(0x36)) ?? w.Table(Tables.Officers)[0];
                if (Objects.PersonValid(governor) && Personality.EffectSum(w, governor, 0x7D) > 0) roll = false;
            }
            int seedA = (a.Index + 5) * b.Hex, seedB = a.Hex * 2;
            if (roll)
            {
                int spare = Objects.ForceValid(fa) ? Objects.Doctrine(w, fa, 0x30) : 0;
                float chance = Math.Min(100f, (float)spare);
                if (chance <= 0f || Wounded.Seeded(w, 10000, 1, seedA, seedB, 0, 0) >= (int)(chance * 10000f / 100f))
                {
                    int loss = DevelopmentLoss;
                    Develop(ref agri, Math.Clamp(agri - loss, 0, 1000), owner, ref panel);
                    Develop(ref comm, Math.Clamp(comm - loss, 0, 1000), owner, ref panel);
                    Develop(ref barr, Math.Clamp(barr - loss, 0, 1000), owner, ref panel);
                }
            }
            int div = Math.Max(1, OrderDivisor);
            float c2 = (float)OrderFactor * (float)OrderScale / ((float)order + (float)OrderOffset) / (float)div * 100f;
            c2 = Math.Min(100f, c2);
            if (c2 > 0f && Wounded.Seeded(w, 10000, 1, seedA, seedB, 0, 0) < (int)(c2 * 10000f / 100f))
            {
                // 0x17A0D0: only while the county's owner is not a tribe
                if ((uint)(owner.Index - 0x2E) > 4) order = Math.Clamp(Math.Max(0, order - 1), 0, 100);
            }
            int r = Wounded.Seeded(w, PopulationRandom, 1, seedA, seedB, 0, 0);
            int v = pop - r - PopulationLoss;
            v = Math.Max(5000, v);
            pop = v > 60000 ? 60000 : v;
        }

        if (Objects.ForceValid(fa) && Objects.ForceValid(fb) && Counts(w, a) && Counts(w, b))
        {
            int rel = fb.Index < 0x34 ? fa.U8(0x13 + fb.Index) : 0;
            (int ab, int ba) = (relAB, relBA);
            if (rel > RelationCap) (ab, ba) = Relation.Change(w, fa, fb, RelationCap - rel, ab, ba);
            (relAB, relBA) = Relation.Change(w, fa, fb, RelationLoss, ab, ba);
        }
        return new Result(agri, comm, barr, pop, order, relAB, relBA, panel);
    }

    // A side counts for the relation drop: a base always; a unit when its commander is real, has a force (not 51)
    // and is not a tribe officer outside the bases.
    private static bool Counts(World w, Side s)
    {
        if (s.Kind != 0x1B) return true;
        Rec unit = w.Entry(Tables.Units, s.Index, 0x1F4);
        if (!Objects.UnitValid(unit)) return false;
        Rec cmd = w.OfficerById(unit.U16(0x12)) ?? w.Table(Tables.Officers)[0];
        return Objects.PersonValid(cmd) && Objects.ForceOf(w, cmd).Index != 0x33 && !Objects.TribeOutside(w, cmd);
    }

    // 0x22C810: the force owning the county's centre hex (+0x3A, the hex's +0x14).
    internal static Rec CountyOwner(World w, Rec county) =>
        w.Entry(Tables.Forces, w.Entry(Tables.Tiles, county.U16(0x3A), 0xBD0F).U8(0x14), 0x33);

    // The development level 1..5 of a value by the thresholds 100, 300, 600, 1000 (rdata 0x13A6D9C).
    internal static int Level(int value)
    {
        int level = 1;
        foreach (int t in (ReadOnlySpan<int>)[100, 300, 600, 1000])
        {
            if (value < t) return level;
            level++;
        }
        return 5;
    }

    // 0x240B20 / 0x2410C0 / 0x241AA0 with the third argument set: the value takes `next` (0..1000) and, when its
    // level changes, the owning force's +0x1BE moves by the level difference (×1, exe 0x1A48864), within ±1000,
    // for an owner that is a real force and neither 51 nor a tribe; `panel` is that +0x1BE as it stands.
    private static void Develop(ref int value, int next, Rec owner, ref int panel)
    {
        int before = Level(value);
        value = next;
        int after = Level(value);
        if (after == before) return;
        if (!Objects.ForceValid(owner) || owner.Index == 0 || owner.Index == 0x33 || (uint)(owner.Index - 0x2E) < 5) return;
        panel = Math.Clamp((after - before) * 1 + panel, -1000, 1000);
    }
}

// The relation bytes two forces keep of each other (+0x13 + the other's number, 0..200) and their grade.
internal static class Relation
{
    [Rva(0x1A481F8)] internal const int Grade1 = 1;
    [Rva(0x1A481D4)] internal const int Grade2 = 30;
    [Rva(0x1A481DC)] internal const int Grade3 = 50;
    [Rva(0x1A481E4)] internal const int Grade4 = 70;
    [Rva(0x1A481F0)] internal const int Grade5 = 100;

    // 0x2AD610: the grade 0..5 of a relation byte.
    internal static int Grade(int rel) =>
        rel < Grade1 ? 0 : rel < Grade2 ? 1 : rel < Grade3 ? 2 : rel < Grade4 ? 3 : rel >= Grade5 ? 5 : 4;

    // 0x2700A0 through 0x2586E0: both bytes move by `delta` and stay within 0..200; nothing between a force and
    // itself. The game then tells the player when the grade changed, and hands a changed grade with a tribe to
    // 0x27ACA0 (neither modelled here). `ab`/`ba` are the bytes as they stand (the same step may change them
    // twice).
    internal static (int AB, int BA) Change(World w, Rec a, Rec b, int delta, int ab, int ba)
    {
        if (!Objects.ForceValid(a) || !Objects.ForceValid(b) || a.Index == b.Index) return (ab, ba);
        return (Math.Clamp(ab + delta, 0, 200), Math.Clamp(ba + delta, 0, 200));
    }
}

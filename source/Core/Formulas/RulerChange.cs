using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

internal sealed record RulerChangeHost(Action<Rec>? RefreshPlayerCorps = null,
    Action? RefreshPlayerStrategy = null, Action? RefreshPresentation = null);

internal static class RulerChange
{
    [Rva(0x1A49CAC)] internal const int LoyaltyTenureDivisor = 2;
    internal const string LoyaltyJoinLimit = "loyalty_join_limit";

    // 0x2688C0: relationship shortcuts precede the compatibility, charm and signed tenure terms.
    internal static int Loyalty(World w, Rec person, Rec force, bool includeTenure)
        => LoyaltyForRuler(w, person, w.OfficerById(force.U16(0x10))!, includeTenure);

    private static int LoyaltyForRuler(World w, Rec person, Rec ruler, bool includeTenure)
    {
        if (person.U8(0xE6) == 1) return 250;
        if (OfficerRelations.Sworn(w, person, ruler) || OfficerRelations.Spouse(w, person, ruler)) return 150;
        if (OfficerRelations.Likes(w, person, ruler) || (person.U16(0x15E) & 0x4000) != 0) return 120;
        int compatibility = Math.Min(Link.CompatibilityDiff(person, ruler), 50);
        int charm = OfficerAbility.Get(w, ruler, 4, OfficerAbility.All);
        int bonus = person.I16(0xB2) == ruler.I16(0xB2) ? 5 : 0;
        if (person.U8(0x14C) != 0 && person.U8(0x14C) == ruler.U8(0x14C)) bonus += 3;
        if (person.I16(0x17A) - ruler.I16(0x17A) <= w.Value(LoyaltyJoinLimit)) bonus += 3;
        int tenure = includeTenure && person.I16(0x17C) > person.I16(0x16C)
            ? person.I16(0x17C) / LoyaltyTenureDivisor + 1 : 0;
        return Math.Clamp(100 + bonus + charm / 14 - compatibility / 2 - tenure -
            (OfficerRelations.Dislikes(w, person, ruler) ? 15 : 0), 0, 100);
    }

    // Preview the same loyalty calculation using copied officer bytes before any caller mutation.
    internal static int PreviewLoyalty(World w, Rec person, Rec dying, Rec successor)
    {
        Rec ruler = new(successor.Index, successor.Bytes.ToArray(), successor.Table, successor.NativeAddress);
        ruler.Bytes[0xE6] = 1; ruler.Bytes[0xE7] = 0x49; ruler.Bytes[0x1BC] = 0;
        Rec member = person;
        if (person == successor) member = ruler;
        else if (person == dying)
        {
            member = new(person.Index, person.Bytes.ToArray(), person.Table, person.NativeAddress);
            member.Bytes[0xE6] = 4; member.Bytes[0x1BC] = 2;
        }
        return (member.U16(0x15E) & 0x2000) != 0 ? 255 : LoyaltyForRuler(w, member, ruler, true);
    }

    private static void Word(Rec r, int at, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(r.Bytes.AsSpan(at), unchecked((ushort)value));

    // 0x28DB70, param4 == 0: install the ruler, merge corps, rebuild loyalty and the caller's officer list.
    // NPC calls complete; player records are verified only through the first 0x450D30 presentation entry.
    // The later 0x26CCC0 policy reinitialization is a separate body. Physical presentation belongs to the host.
    internal static void ApplyWithoutPolicyReset(World w, CorpsLifecycle corps, Rec? dying, Rec? successor,
        List<Rec?> officers, RulerChangeHost host)
        => Apply(w, corps, dying, successor, officers, host, null);

    // 0x28DB70: policy reset runs after rebuilding the officer list and before player presentation.
    internal static void Apply(World w, CorpsLifecycle corps, Rec? dying, Rec? successor,
        List<Rec?> officers, RulerChangeHost host, RulerPolicy? policy)
    {
        if (!Objects.Valid(w, dying) || !Objects.Valid(w, successor)) return;
        Rec force = Objects.ForceOf(w, dying!);
        Rec oldCorps = w.Entry(Tables.Corps, dying!.U8(0xE0), 51);
        Rec nextCorps = w.Entry(Tables.Corps, successor!.U8(0xE0), 51);
        if (dying.U8(0xE0) != successor.U8(0xE0)) corps.RequireMergeSupported(nextCorps, oldCorps);
        if (Capture.CityCount(w, force) == 0)
            throw new NotSupportedException("Ruler change with no city requires the force teardown body.");
        Rec[] rules = w.Table(OfficerLifecycle.LoyaltyRules);
        int upper = rules[6].I16(0x2C), lower = rules[Objects.OptionAt4C4(w) == 1 ? 2 : 1].I16(0x2C);
        int Bound(int value) => lower > value ? lower : Math.Min(value, upper);

        Word(force, 0x10, Math.Min(successor.U16(0x10), (ushort)6000));
        successor.Bytes[0xE6] = 1;
        successor.Bytes[0xE7] = 0x49;
        successor.Bytes[0xE8] = (byte)((successor.U16(0x15E) & 0x2000) != 0 ? 255 : Loyalty(w, successor, force, false));
        successor.Bytes[0x1BC] = 0;
        successor.Bytes[0x1C4] = 0;
        Word(successor, 0x1BE, Bound(rules[0].I16(0x2C)));
        successor.Bytes.AsSpan(0x1C0, 4).Clear();
        dying.Bytes[0x1BC] = 2;
        dying.Bytes[0x1C4] = 0;
        Word(dying, 0x1BE, Bound(rules[2].I16(0x2C)));
        dying.Bytes.AsSpan(0x1C0, 4).Clear();
        if (force.U16(0x52) == successor.U16(0x10)) Word(force, 0x52, 0);
        if (dying.U8(0xE0) != successor.U8(0xE0)) corps.Merge(nextCorps, oldCorps, 0);
        dying.Bytes[0xE6] = 4;
        int retainedCorps = dying.U8(0xE0);
        CorpsLifecycle.SetOfficerCorps(w, dying, 0);
        corps.CheckSurvivingForce(w.Entry(Tables.Corps, successor.U8(0xE0), 51));
        CorpsLifecycle.SetOfficerCorps(w, dying, retainedCorps);
        officers.Clear();
        foreach (Rec?[] node in w.List(Lists.OfficerList))
        {
            Rec person = node[0] ?? throw new NotSupportedException("The native force iterator dereferences a null officer node.");
            if (force.Index is < 1 or > 51 || OfficerRelations.RawForce(w, person) != force.Index) continue;
            person.Bytes[0xE8] = (byte)((person.U16(0x15E) & 0x2000) != 0 ? 255 : Loyalty(w, person, force, true));
            officers.Add(person);
        }
        policy?.Apply(force, successor, dying, true);
        if (ReferenceEquals(force, w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 51)))
        {
            host.RefreshPlayerCorps?.Invoke(w.Entry(Tables.Corps, successor.U8(0xE0), 51));
            host.RefreshPlayerStrategy?.Invoke();
        }
        host.RefreshPresentation?.Invoke();
    }
}

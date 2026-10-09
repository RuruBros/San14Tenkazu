using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// The ordinary officer-capture body with no notification/dialog request and a surviving force.
// CorpsLifecycle composes mergers; force dissolution, ruler/marked-officer handling and player UI remain separate.
internal static class OfficerCapture
{
    [Rva(0x1A481AC)] internal const int PolicyTierTwo = 5;
    [Rva(0x1A481A0)] internal const int PolicyTierThree = 10;
    [Rva(0x1A49CA8)] internal const int TribeDurability = 9999;
    [Rva(0x1A4A238)] internal const int CityDurability = 2000;
    [Rva(0x1A4A244)] internal const int CityDurabilityPerSize = 1000;
    [Rva(0x1A4A250)] internal const int CityDurabilityPerPolicy = 1;
    [Rva(0x1A4A634)] internal const int DamageTierOne = 75;
    [Rva(0x1A4A640)] internal const int DamageTierTwo = 50;
    [Rva(0x1A4A64C)] internal const int DamageTierThree = 25;

    private static void Word(Rec r, int at, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(r.Bytes.AsSpan(at), unchecked((ushort)value));
    private static void Dword(Rec r, int at, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(r.Bytes.AsSpan(at), value);
    private static bool Serving(Rec p) => p.U8(0xE6) is >= 1 and <= 4;
    private static bool Marked(Rec p) => (p.U16(0x15E) & 0x4000) != 0;

    private static IEnumerable<Rec> CorpsOfficers(World w, Rec corps) =>
        w.List(Lists.OfficerList).Select(n => n[0]).OfType<Rec>()
            .Where(p => corps.Index is >= 1 and <= 51 && p.U8(0xE0) == corps.Index && Serving(p));
    private static IEnumerable<Rec> CorpsCities(World w, Rec corps) =>
        w.Table(Tables.Cities).Where(b => corps.Index is >= 1 and <= 51 && b.U8(0x30) == corps.Index);
    internal static bool CountedOfficer(World w, Rec p) =>
        !Objects.SpecialOfficer(w, p, false) && !Objects.TribeOutside(w, p) && !Marked(p);

    // Validate the supported native branch before mutating records. Excluding the target also covers the second
    // corps reevaluation after a district leader changes to prisoner status.
    private static void RequireRetainedCorps(World w, Rec person, Rec corps, Rec force)
    {
        if (Marked(person) || person.U8(0xE6) == 1 || person.U16(0x10) == 0)
            throw new NotSupportedException("Ruler, marked-officer and zero-ID capture are outside this branch.");
        if (Objects.IsPlayerForce(w, force))
            throw new NotSupportedException("The player's officer selection and capture presentation are not ported.");
        if (!Objects.CorpsValid(corps) || !Objects.ForceValid(force) || Capture.CityCount(w, force) == 0 ||
            !w.List(Lists.OfficerList).Select(n => n[0]).OfType<Rec>().Any(p =>
                p != person && Serving(p) && Objects.ForceOf(w, p) == force && CountedOfficer(w, p)) ||
            !CorpsCities(w, corps).Any() || !CorpsOfficers(w, corps).Any(p => p != person && CountedOfficer(w, p)))
            throw new NotSupportedException("Force dissolution or corps merging is outside retained-corps capture.");
    }

    // 0x2E0CF0, arguments 3 and 4 equal zero: perform every gameplay write in the retained-corps branch.
    internal static int ApplyRetainedCorps(World w, Rec? person, Rec? attackerUnit, Rec? captorForce = null,
        Rec? captorOfficer = null)
    {
        if (!Objects.Valid(w, person)) return 0;
        Rec p = person!, corps = w.Entry(Tables.Corps, p.U8(0xE0), 51), force = Objects.ForceOf(w, p);
        RequireRetainedCorps(w, p, corps, force);
        return Apply(w, p, corps, force, attackerUnit, captorForce, captorOfficer,
            (c, _) => ReevaluateRetainedCorps(w, c));
    }

    // 0x2E0CF0, arguments 3/4 zero, ordinary officer, surviving original force. The native second check
    // uses the current corps after capture and allowStructural=1, even if the first check merged the corps.
    internal static int ApplySurvivingForce(World w, CorpsLifecycle lifecycle, Rec? person, Rec? attackerUnit,
        Rec? captorForce = null, Rec? captorOfficer = null)
    {
        if (!Objects.Valid(w, person)) return 0;
        Rec p = person!, corps = w.Entry(Tables.Corps, p.U8(0xE0), 51), force = Objects.ForceOf(w, p);
        if (Marked(p) || p.U8(0xE6) == 1 || p.U16(0x10) == 0 || Objects.IsPlayerForce(w, force))
            throw new NotSupportedException("Ruler, marked-officer and player capture require separate engine bodies.");
        if (!Objects.CorpsValid(corps) || !Objects.ForceValid(force) || Capture.CityCount(w, force) == 0 ||
            !w.List(Lists.OfficerList).Select(n => n[0]).OfType<Rec>().Any(other =>
                other != p && Serving(other) && Objects.ForceOf(w, other) == force && CountedOfficer(w, other)))
            throw new NotSupportedException("The force teardown body is outside surviving-force capture.");
        return Apply(w, p, corps, force, attackerUnit, captorForce, captorOfficer,
            (c, mode) => lifecycle.CheckSurvivingForce(c, mode));
    }

    // 0x2E0CF0 with notification/dialog zero and null captors. Its active marked-officer branch precedes
    // ruler/player handling and returns immediately after 0x264DD0, without ordinary prisoner writes.
    internal static int ApplyMarkedCapture(World w, OfficerLifecycle lifecycle, Rec? person)
    {
        if (!Objects.Valid(w, person)) return 0;
        if ((person!.U16(0x15E) & 0x4000) == 0 || person.U8(0xE6) == 5)
            throw new NotSupportedException("This capture branch requires an active, non-free marked officer.");
        return lifecycle.ApplyMarked(person);
    }

    // 0x2E0CF0: an activated marked slot already at status5 falls through to prisoner placement.
    // Its corps0 check is a no-op, and marked slots skip force-post removal.
    internal static int ApplyFreeMarkedCapture(World w, CorpsLifecycle lifecycle, Rec? person, Rec? attackerUnit,
        Rec? captorForce = null, Rec? captorOfficer = null)
    {
        if (!Objects.Valid(w, person)) return 0;
        Rec p = person!, district = w.Entry(Tables.Corps, p.U8(0xE0), 51), force = Objects.ForceOf(w, p);
        if (!Marked(p) || p.U8(0xE6) != 5 || p.U8(0xE0) != 0 || force.Index != 0)
            throw new NotSupportedException("This branch requires an activated free marked slot without a corps.");
        return Apply(w, p, district, force, attackerUnit, captorForce, captorOfficer,
            (c, mode) => lifecycle.CheckSurvivingForce(c, mode), removeForcePosts: false);
    }

    private static int Apply(World w, Rec p, Rec corps, Rec force, Rec? attackerUnit, Rec? captorForce,
        Rec? captorOfficer, Action<Rec, int> reevaluate, bool removeForcePosts = true)
    {
        RemoveAppointments(w, p, force);
        if (removeForcePosts) RemoveForcePosts(w, p, force);
        reevaluate(corps, 0);
        p.Bytes[0xE7] = 0x49;
        bool wasLeader = p.U8(0xE6) == 2;
        p.Bytes[0xE6] = 6;
        p.Bytes[0x168] = 0;
        if (wasLeader) reevaluate(w.Entry(Tables.Corps, p.U8(0xE0), 51), 1);

        if (Objects.Valid(w, attackerUnit))
        {
            Rec? commander = w.OfficerById(attackerUnit!.U16(0x12));
            Rec b = Map.Base(w, Objects.Valid(w, commander) ? commander!.U16(0xE2) : 0)!;
            Word(p, 0xE2, Math.Min(b.U16(0x4E), (ushort)61));
            SetPlace(p, 27, attackerUnit.Index);
            if (Objects.Valid(w, commander) && Objects.Place(commander!).Kind != 27)
                Word(p, 0xE4, Math.Min(p.U16(0xE2), (ushort)1061));
        }
        else if (Objects.Valid(w, captorForce))
        {
            Rec ruler = w.OfficerById(captorForce!.U16(0x10))!;
            SetCaptorBase(w, p, Map.Base(w, ruler.U16(0xE2))!);
        }
        else if (Objects.Valid(w, captorOfficer))
            SetCaptorBase(w, p, Map.Base(w, captorOfficer!.U16(0xE2))!);
        else Word(p, 0xE4, Math.Min(p.U16(0xE2), (ushort)1061));
        if (Objects.Place(p).Kind == 27 || Capture.CityCount(w, force) > 1)
            p.Bytes[0x165] = (byte)Math.Clamp(force.Index, 0, 51);
        return 1;
    }

    private static void SetCaptorBase(World w, Rec p, Rec b)
    {
        Word(p, 0xE2, Math.Min(b.U16(0x4E), (ushort)61));
        SetPlace(p, 5, ParentCity(w, b).Index);
    }

    // 0x241E20: kinds other than city, gate and unit use the corps encoding.
    internal static void SetPlace(Rec p, int kind, int number)
    {
        int encoded = kind switch
        {
            5 => number is >= 0 and <= 51 ? number : 0,
            6 => (uint)(number - 1) < 10 ? number + 51 : 0,
            27 => (uint)(number - 1) < 500 ? number + 61 : 0,
            _ => (uint)(number - 1) < 500 ? number + 561 : 0,
        };
        Word(p, 0xE4, Math.Clamp(encoded, 0, 1061));
    }

    // 0x22A7B0: even a city is resolved through its building's county and that county's parent city.
    internal static Rec ParentCity(World w, Rec b)
    {
        Rec tile = Map.BuildingTile(w, b);
        Rec county = w.Entry(Tables.Counties, Objects.Valid(w, tile) ? tile.U16(0x12) : 0, 500);
        return w.Entry(Tables.Cities, Objects.CountyValid(county) ? county.U8(0x35) : 0, 51);
    }

    // 0x29BFF0 with no destination corps/base: remove advisor, city and county assignments, then envoy slots.
    internal static void RemoveAppointments(World w, Rec p, Rec force)
    {
        if (!Objects.ForceValid(force)) return;
        int id = p.U16(0x10);
        if (force.U16(0x52) == id)
        {
            Word(force, 0x52, 0);
            RefreshMorale(w, force);
            RefreshPolicyTier(w, force);
            RefreshDurability(w, force);
        }
        foreach (Rec city in w.List(Lists.CityList).Select(n => n[0]).OfType<Rec>())
        {
            if (city.U16(0xCA) == id) Word(city, 0xCA, 0);
            if (city.U16(0xCC) == id) Word(city, 0xCC, 0);
            foreach (Rec county in w.List(Lists.CountyList).Select(n => n[0]).OfType<Rec>()
                .Where(c => city.Index is >= 1 and <= 51 && c.U8(0x35) == city.Index))
            {
                if (county.U16(0x36) != id) continue;
                if (id != 0)
                {
                    Word(county, 0x36, 0);
                    Rec? owner = w.OfficerById(id);
                    if (Objects.Valid(w, owner)) RemoveGovernedCounty(owner!, county.Index);
                }
                county.Bytes[0x72] = 0;
                county.Bytes[0x6D] = 0;
            }
        }
        ClearEnvoy(w, p, force);
    }

    // 0x277F60: erase the first matching envoy slot only when its host force is valid.
    internal static void ClearEnvoy(World w, Rec p, Rec force)
    {
        int id = p.U16(0x10);
        if (!Objects.Valid(w, p) || !Objects.ForceValid(force)) return;
        for (int i = 0; i < 3; i++)
            if (force.U16(0x196 + i * 2) == id)
            {
                Rec host = w.Entry(Tables.Forces, force.U8(0x19C + i), 51);
                if (Objects.ForceValid(host))
                {
                    force.Bytes[0x19C + i] = 0;
                    Word(force, 0x196 + i * 2, 0);
                }
                break; // 0x2297B0 returns the first matching slot, including an invalid host.
            }
    }

    // 0x2E7D00 -> 0x224BC0: erase in the active prefix, decrement count, sort the entire vector with zeros last.
    private static void RemoveGovernedCounty(Rec p, int county)
    {
        int count = p.I32(0x1E8), found = -1;
        if (count == 0) return;
        byte[] vector = p.Vector1D0 ?? throw new InvalidDataException("Missing officer county vector.");
        for (int i = 0; i < count; i++)
            if (BitConverter.ToInt32(vector, i * 4) == county) { found = i; break; }
        if (found < 0) return;
        BinaryPrimitives.WriteInt32LittleEndian(vector.AsSpan(found * 4), 0);
        Dword(p, 0x1E8, count - 1);
        int[] entries = Enumerable.Range(0, vector.Length / 4).Select(i => BitConverter.ToInt32(vector, i * 4)).ToArray();
        Array.Sort(entries, (a, b) => a == 0 && b != 0 ? 1 : b == 0 && a != 0 ? -1 : unchecked(a - b));
        for (int i = 0; i < entries.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(vector.AsSpan(i * 4), entries[i]);
    }

    // 0x2E7E90. The advisor has already been cleared by 0x29BFF0 on this capture path.
    internal static void RemoveForcePosts(World w, Rec p, Rec force)
    {
        for (int field = 0; field < 5; field++)
            for (int slot = 0; slot < 7; slot++)
            {
                int at = 0x54 + (field * 15 + slot) * 2;
                if (force.U16(at) != p.U16(0x10)) continue;
                Word(force, at, 0);
                Word(force, at + 14, 0);
                if (field == 1) RefreshMorale(w, force);
                else if (field == 4) RefreshPolicyTier(w, force);
                else if (field == 3) RefreshDurability(w, force);
            }
    }

    internal static void RefreshPolicyTier(World w, Rec force)
    {
        if (!Objects.ForceValid(force)) return;
        int level = Policy.Level(w, force, 14);
        force.Bytes[0x15E] = (byte)(level >= PolicyTierThree ? 13 : level >= PolicyTierTwo ? 12 : 11);
    }

    // 0x27B9F0: bases in the base list, then units in the active unit list.
    internal static void RefreshMorale(World w, Rec force)
    {
        if (!Objects.ForceValid(force)) return;
        int max = BattlePower.MaxMorale(w, force);
        foreach (Rec b in w.List(Lists.Bases).Select(n => n[0]).OfType<Rec>())
            if (Map.BaseForce(w, b) == force.Index && Objects.Valid(w, b) && (uint)(force.Index - 46) > 4)
                b.Bytes[0x44] = unchecked((byte)Math.Max(Math.Min(b.U8(0x44), max), 0));
        foreach (Rec unit in w.List(Lists.UnitList).Select(n => n[0]).OfType<Rec>())
            if (Objects.UnitForce(w, unit) == force.Index && Objects.Valid(w, unit) && !Map.Engaged(w, unit))
                unit.Bytes[0x1A] = unchecked((byte)Math.Max(Math.Min(unit.U8(0x1A), max), 0));
    }

    // 0x2FED20 only visits cities in the force's city array iterator.
    internal static void RefreshDurability(World w, Rec force)
    {
        if (!Objects.ForceValid(force)) return;
        foreach (Rec city in w.Table(Tables.Cities))
        {
            if (Map.BaseForce(w, city) != force.Index || !Objects.Valid(w, city) || city.U16(0x4E) > 51) continue;
            Rec building = w.Entry(Tables.Buildings, city.U16(0x4E), 3000);
            int current = Map.BuildingValid(building) ? building.U16(0x14) : 6000;
            int max = MaximumDurability(w, city);
            if (max <= current) Word(building, 0x14, Math.Clamp(max, 0, 9999));
            int percent = Objects.Trunc(MathF.Ceiling((float)(Map.BuildingValid(building) ? building.U16(0x14) : 6000) / max * 100f));
            city.Bytes[0x66] = (byte)(percent > DamageTierOne && percent <= 100 ? 0 :
                percent > DamageTierTwo && percent <= DamageTierOne ? 1 :
                percent > DamageTierThree && percent <= DamageTierTwo ? 2 : percent is >= 0 and <= DamageTierThree ? 3 : 0);
        }
    }

    private static int MaximumDurability(World w, Rec city)
    {
        Rec force = w.Entry(Tables.Forces, Map.BaseForce(w, city), 51);
        bool tribeHome = (uint)(force.Index - 46) < 5 && ParentCity(w, city).Index == force.Index + 1;
        if (tribeHome) return TribeDurability;
        int value = CityDurability + (city.U8(0xB8) - 1) * CityDurabilityPerSize + Policy.Effect(w, force, 32) * CityDurabilityPerPolicy;
        if (w.Settings.I32(0xE58) == 0) value = unchecked(BattlePower.Slider(w, 39) * value) / 100;
        return Math.Min(value, 9999);
    }

    // 0x2E8E70 after the force/corps survival guards. Preserve its leader-retention rule, then refresh governors.
    internal static void ReevaluateRetainedCorps(World w, Rec corps)
    {
        Rec leader = w.OfficerById(corps.U16(0x12))!;
        bool retained = leader.U8(0xE0) == corps.Index && (corps.U8(0x11) == 1 || leader.U8(0xE6) is 1 or 2);
        if (!retained)
        {
            if (leader.U8(0xE6) == 2) leader.Bytes[0xE6] = 4;
            Rec replacement = Best(CorpsOfficers(w, corps).Where(p => !Objects.InForces46To50(w, p) && !Objects.SpecialOfficer(w, p, false)),
                (a, b) => PreferCorpsLeader(w, a, b));
            Word(corps, 0x12, Math.Min(replacement.U16(0x10), (ushort)6000));
            if (replacement.U8(0xE6) != 1) replacement.Bytes[0xE6] = 2;
        }
        foreach (Rec city in CorpsCities(w, corps))
        {
            if (Objects.Valid(w, city)) AssignGovernor(w, city, SelectGovernor(w, city));
            foreach (Rec gate in w.List(Lists.GateList).Select(n => n[0]).OfType<Rec>())
                if (ParentCity(w, gate) == city && Objects.Valid(w, gate)) AssignGovernor(w, gate, SelectGovernor(w, gate));
        }
    }

    private static Rec Best(IEnumerable<Rec> people, Func<Rec, Rec, bool> preferred)
    {
        Rec? best = null;
        foreach (Rec p in people) if (best is null || preferred(p, best)) best = p;
        return best ?? throw new InvalidDataException("Native officer selection requires a nonempty list.");
    }

    // 0x254DE0: ruler, troop capacity, nonzero office, office number, leadership, officer ID.
    private static bool PreferCorpsLeader(World w, Rec a, Rec b)
    {
        if ((a.U8(0xE6) == 1) != (b.U8(0xE6) == 1)) return a.U8(0xE6) == 1;
        int ac = TroopCapacity.Get(w, a), bc = TroopCapacity.Get(w, b);
        if (ac != bc) return ac > bc;
        int ao = a.U8(0xE7), bo = b.U8(0xE7);
        if ((ao == 0) != (bo == 0)) return ao != 0;
        if (ao != bo) return ao < bo;
        int al = OfficerAbility.Get(w, a, 0, OfficerAbility.All), bl = OfficerAbility.Get(w, b, 0, OfficerAbility.All);
        return al != bl ? al > bl : a.U16(0x10) < b.U16(0x10);
    }

    // 0x2827C0 -> 0x253FC0: use the native base iterator and exclude away/special/marked candidates.
    internal static Rec? SelectGovernor(World w, Rec b)
    {
        Rec[] candidates = Map.OfficersOf(w, b, 0).Where(p => !Objects.Away(w, p) &&
            !Objects.SpecialOfficer(w, p, false) && !Marked(p)).ToArray();
        return candidates.Length == 0 ? w.OfficerById(0)! : Best(candidates, (a, c) => PreferGovernor(w, a, c));
    }

    private static bool PreferGovernor(World w, Rec a, Rec b)
    {
        foreach (int status in new[] { 1, 2 })
            if ((a.U8(0xE6) == status) != (b.U8(0xE6) == status)) return a.U8(0xE6) == status;
        if ((a.U8(0x160) & 1) != (b.U8(0x160) & 1)) return (a.U8(0x160) & 1) != 0;
        Rec ao = w.Table(Tables.Offices)[a.U8(0xE7)], bo = w.Table(Tables.Offices)[b.U8(0xE7)];
        int ar = (ao.U8(0x3D) & 7) == 0 ? ao.Index : ao.U8(0x37), br = (bo.U8(0x3D) & 7) == 0 ? bo.Index : bo.U8(0x37);
        if (ar != br) return ar < br;
        if (a.U16(0x166) != b.U16(0x166)) return a.U16(0x166) < b.U16(0x166);
        if (a.U8(0xE8) != b.U8(0xE8)) return a.U8(0xE8) > b.U8(0xE8);
        if (a.U8(0xEC) != b.U8(0xEC)) return a.U8(0xEC) > b.U8(0xEC);
        return a.U16(0x10) < b.U16(0x10);
    }

    // 0x26CC10: demote the previous serving governor, write the new ID, promote ordinary officers to status 3.
    internal static void AssignGovernor(World w, Rec b, Rec? replacement)
    {
        Rec? previous = w.OfficerById(b.U16(0x2E));
        if (Objects.Valid(w, previous) && (byte)(previous!.U8(0xE6) - 1) > 1 && Serving(previous)) previous.Bytes[0xE6] = 4;
        if (!Objects.Valid(w, replacement)) { Word(b, 0x2E, 0); return; }
        Word(b, 0x2E, Math.Min(replacement!.U16(0x10), (ushort)6000));
        if ((byte)(replacement.U8(0xE6) - 1) > 1) replacement.Bytes[0xE6] = 3;
    }
}

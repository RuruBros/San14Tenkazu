using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// Officer gameplay removal, with logical native list pools. Unit destruction and player UI are separate.
internal sealed class OfficerLifecycle(World w, CorpsLifecycle corps, CommandStorage commands,
    IReadOnlyDictionary<int, List<Rec?>> forceDeferred, List<Rec?> freeOfficers, OfficerNameStorage? names = null)
{
    [Rva(0x1A488C4)] internal const int FreePopularityLoss = 3;
    internal const int LoyaltyRules = 0x85DB8;
    private static void Word(Rec p, int at, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(p.Bytes.AsSpan(at), unchecked((ushort)value));

    // 0x32B5A0: pending membership is tested first. Remove the first force-list alias, then detach and append.
    internal void QueueFree(Rec p)
    {
        if (freeOfficers.Any(other => ReferenceEquals(other, p))) return;
        Rec force = Objects.ForceOf(w, p);
        if (!Objects.ForceValid(force)) return;
        if (forceDeferred.TryGetValue(force.Index, out var members))
        {
            int index = members.FindIndex(other => ReferenceEquals(other, p));
            if (index >= 0) members.RemoveAt(index);
        }
        p.Bytes[0x165] = 0;
        p.Bytes[0xE6] = 5;
        CorpsLifecycle.SetOfficerCorps(w, p, 0);
        Word(p, 0xE2, 0); // 0x32B669 passes kind5, number0 to 0x240930.
        freeOfficers.Add(p);
    }

    private OfficerNameStorage.Name? PrepareName(Rec p)
    {
        if (BitConverter.ToUInt64(p.Bytes, 0x1B0) <= 7) return null;
        return names?.Prepare(p) ?? throw new NotSupportedException("Heap name reset requires its copied buffer and allocation header.");
    }

    internal void RequireResetName(Rec p) => PrepareName(p);

    // 0x226300: reset an officer slot, retaining the unused string union tail in both storage modes.
    internal void ResetSlot(Rec p)
    {
        OfficerNameStorage.Name? name = PrepareName(p);
        Word(p, 0xE6, 0);
        CorpsLifecycle.SetOfficerCorps(w, p, 0);
        p.Bytes[0xE8] = (byte)((p.U16(0x15E) & 0x2000) != 0 ? 0xFF : 0);
        foreach (var (at, length) in new (int, int)[] {
            (0xE2,4), (0xEA,2), (0xBA,4), (0xC0,32), (0x104,20), (0x13A,18),
            (0x118,18), (0x12C,14), (0x14D,1), (0x151,1), (0x152,12), (0x15E,11),
            (0xF2,5), (0xF8,10), (0x182,1), (0x16A,24), (0x184,20), (0x1A8,8),
            (0x1BC,1), (0x1BE,10), (0x1C8,1), (0x1CC,4), (0x1E8,4), (0x1EE,2) })
            p.Bytes.AsSpan(at, length).Clear();
        if (name is { } heap) names!.ClearAndShrink(p, heap);
        else Word(p, 0x198, 0);
        p.Bytes.AsSpan(0x1D0, 8).CopyTo(p.Bytes.AsSpan(0x1D8));
    }

    // 0x33BC00: reset every queued slot, erase every registered alias, increment per queued node, then clear.
    internal int ConsumeFree(int count)
    {
        foreach (Rec? p in freeOfficers)
        {
            if (p is null) throw new NotSupportedException("Free queue consumption requires non-null slots.");
            PrepareName(p);
        }
        foreach (Rec? person in freeOfficers)
        {
            Rec p = person!;
            ResetSlot(p);
            w.List(Lists.OfficerList).RemoveAll(n => ReferenceEquals(n[0], p));
            count = unchecked(count + 1);
        }
        freeOfficers.Clear();
        return count;
    }

    // 0x264DD0 with bit0x4000: clear appointments and command membership before the deferred free queue.
    internal int ApplyMarked(Rec p)
    {
        if ((p.U16(0x15E) & 0x4000) == 0)
            throw new NotSupportedException("This body requires a valid marked officer.");
        DetachAppointments(p);
        if (commands.FindOfficer(p.U16(0x10)) is { } command) commands.RemoveOfficer(command, p.U16(0x10));
        if (p.U8(0xE6) != 5) QueueFree(p);
        return 1;
    }

    private (Rec Force, Rec Home, bool ClearedGovernor) DetachAppointments(Rec p)
    {
        if (!Objects.Valid(w, p)) throw new NotSupportedException("This body requires a valid officer.");
        Rec force = Objects.ForceOf(w, p);
        if (Objects.IsPlayerForce(w, force))
            throw new NotSupportedException("Player capture composition requires its UI host requests.");
        var place = Objects.Place(p);
        if (place.Kind == 27 && (p.U16(0x15E) & 0x4000) != 0)
        {
            Rec unit = w.Entry(Tables.Units, place.Number, 500);
            if (Objects.Valid(w, unit) && unit.U16(0x12) == p.U16(0x10))
                throw new NotSupportedException("The commander requires the unit-destruction body.");
        }
        Rec home = Map.Base(w, p.U16(0xE2))!;
        if (p.U8(0xE6) == 2 && Objects.ForceValid(force) &&
            (Capture.CityCount(w, force) == 0 || !w.List(Lists.OfficerList).Select(n => n[0]).OfType<Rec>().Any(other =>
                other.U8(0xE0) != p.U8(0xE0) && other.U8(0xE6) is >= 1 and <= 4 &&
                Objects.ForceOf(w, other) == force && OfficerCapture.CountedOfficer(w, other))))
            throw new NotSupportedException("Clearing this leader requires the force teardown body.");
        bool cleared = false;
        if (p.U8(0xE6) != 1)
        {
            if (p.U8(0xE6) == 2)
            {
                Rec district = w.Entry(Tables.Corps, p.U8(0xE0), 51);
                Word(district, 0x12, 0);
                p.Bytes[0xE6] = 5;
                corps.CheckSurvivingForce(district, 1);
                p.Bytes[0xE6] = 4;
            }
            if (p.U8(0xE6) == 3 || home.U16(0x2E) == p.U16(0x10)) { Word(home, 0x2E, 0); cleared = true; }
        }
        if (Objects.ForceValid(force))
        {
            if (Objects.Valid(w, home)) OfficerCapture.RemoveAppointments(w, p, force);
            OfficerCapture.RemoveForcePosts(w, p, force);
        }
        return (force, home, cleared);
    }

    // 0x264DD0, unmarked branch. An explicit base bypasses city selection; no candidate returns after detach.
    internal int ApplyFree(Rec p, Rec? releaseOfficer = null, Rec? relocation = null)
    {
        if ((p.U16(0x15E) & 0x4000) != 0)
            throw new NotSupportedException("Marked removal uses the deferred free queue.");
        if (relocation is not null && relocation.Table is not (Tables.Cities or Tables.Gates))
            throw new NotSupportedException("The relocation argument must be a base.");
        Rec[] loyalty = w.Table(LoyaltyRules);
        // Resolve the old county before detaching, as the native caller does.
        Rec oldCounty = CountyOf(Map.Base(w, p.U16(0xE2))!);
        var (force, home, cleared) = DetachAppointments(p);
        if (Objects.ForceValid(force) && force.Index != 51 && (uint)(force.Index - 46) > 4)
            Word(force, 0x1BE, Math.Clamp(force.I16(0x1BE) - FreePopularityLoss, -1000, 1000));
        p.Bytes[0x165] = 0;
        p.Bytes[0xE6] = 5;
        CorpsLifecycle.SetOfficerCorps(w, p, 0);
        Word(p, 0xE2, 0); // 0x264DD0 passes kind5, number0 before destination selection.
        Rec? county = Objects.Valid(w, relocation) ? CountyOf(relocation!) : SelectFreeCounty(p, oldCounty);
        if (county is null) return 0;

        var removed = new HashSet<Rec>();
        foreach (Rec item in Objects.ItemsOf(w, p))
        {
            if ((item.U8(0x4F) & 1) != 0) continue;
            item.Bytes[0x4F] &= 0xF7;
            if ((uint)(county.Index - 1) >= 500) continue;
            Word(item, 0x4A, 0);
            Word(item, 0x4C, county.Index);
            item.Bytes[0x50] = 2;
            removed.Add(item);
        }
        w.List(Lists.OwnedItems).RemoveAll(n => n[0] is { } item && removed.Contains(item));
        if (commands.FindOfficer(p.U16(0x10)) is { } command) commands.RemoveOfficer(command, p.U16(0x10));
        OfficerCapture.SetPlace(p, 10, county.Index);
        Rec nextHome = w.Entry(Tables.Cities, county.U8(0x35), 51);
        if (!Objects.Valid(w, nextHome)) nextHome = w.Entry(Tables.Cities, county.U8(0x34), 51);
        Word(p, 0xE2, nextHome.Index);
        Word(p, 0xE7, 0x49);
        if ((p.U16(0x15E) & 0x2000) != 0) p.Bytes[0xE8] = 0xFF;
        p.Bytes[0x1BC] = 0;
        p.Bytes[0x1C4] = 0;
        int initial = loyalty[0].I16(0x2C), upper = loyalty[6].I16(0x2C);
        int lower = loyalty[Objects.OptionAt4C4(w) == 1 ? 2 : 1].I16(0x2C);
        Word(p, 0x1BE, lower > initial ? lower : Math.Min(initial, upper));
        p.Bytes.AsSpan(0x1C0, 4).Clear();
        Word(p, 0x15E, p.U16(0x15E) & 0xFFDF);
        Word(p, 0x166, 0);
        p.Bytes[0x168] = 0;
        p.Bytes.AsSpan(0x152, 12).Clear();
        if (Objects.Valid(w, releaseOfficer))
        {
            Word(p, 0x162, Math.Min(releaseOfficer!.U16(0x10), (ushort)6000));
            p.Bytes[0x164] = 12;
        }
        if (cleared && home.U16(0x2E) == 0 && Objects.Valid(w, home))
            OfficerCapture.AssignGovernor(w, home, OfficerCapture.SelectGovernor(w, home));
        return 1;
    }

    private Rec CountyOf(Rec b) => w.Entry(Tables.Counties, Map.BuildingTile(w, b).U16(0x12), 500);

    // 0x22F2D0: unique adjacent cities, crossing at most two gate layers, in native traversal order.
    internal IReadOnlyList<Rec> AdjacentCities(Rec source)
    {
        var result = new List<Rec>();
        IEnumerable<Rec> Neighbors(Rec b, int excluded)
        {
            for (int at = 0x58; at < 0x62; at++)
            {
                int id = b.U8(at);
                if (id == 0 || id == source.U16(0x4E) || id == excluded) continue;
                Rec? neighbor = Map.Base(w, id);
                if (Objects.Valid(w, neighbor)) yield return neighbor!;
            }
        }
        void Add(Rec b) { if (!result.Contains(b)) result.Add(b); }
        foreach (Rec first in Neighbors(source, -1))
            if (first.U16(0x4E) < 52) Add(first);
            else foreach (Rec second in Neighbors(first, -1))
                if (second.U16(0x4E) < 52) Add(second);
                else foreach (Rec third in Neighbors(second, first.U16(0x4E)))
                    if (third.U16(0x4E) < 52) Add(third);
        return result;
    }

    // 0x24A9C0: swap payloads using seeded mode1; the global RNG remains unchanged.
    private void Shuffle(List<Rec> records)
    {
        int seed = 0, count = records.Count;
        if (count < 2) return;
        for (int left = (count / 2) * count; left > 0; left--, seed += 2)
        {
            int a = Wounded.Seeded(w, count, 1, seed, 0, 0, 0);
            int b = count - 1 - Wounded.Seeded(w, count, 1, seed + 1, 0, 0, 0);
            (records[a], records[b]) = (records[b], records[a]);
        }
    }

    // 0x254AF0: pairwise ruler validity is intentional; this comparator need not define a total ordering.
    private bool PreferCity(Rec p, Rec a, Rec b, IReadOnlyDictionary<Rec, int> positions,
        Rec? changedForce = null, Rec? futureRuler = null)
    {
        Rec fa = w.Entry(Tables.Forces, Map.BaseForce(w, a), 51), fb = w.Entry(Tables.Forces, Map.BaseForce(w, b), 51);
        bool tribeA = (uint)(fa.Index - 46) < 5, tribeB = (uint)(fb.Index - 46) < 5;
        if (tribeA != tribeB) return !tribeA;
        Rec? ra = fa == changedForce ? futureRuler : w.OfficerById(fa.U16(0x10));
        Rec? rb = fb == changedForce ? futureRuler : w.OfficerById(fb.U16(0x10));
        Rec? excluded = w.OfficerById(p.U16(0x162));
        if (Objects.Valid(w, excluded) && ReferenceEquals(ra, excluded) != ReferenceEquals(rb, excluded))
            return !ReferenceEquals(ra, excluded);
        if (Objects.Valid(w, ra) && Objects.Valid(w, rb) && Link.Hates(p, ra!) != Link.Hates(p, rb!))
            return !Link.Hates(p, ra!);
        int maskA = 0, maskB = 0;
        foreach (Rec county in w.List(Lists.CountyList).Select(n => n[0]).OfType<Rec>())
            if (county.U8(0x34) == a.Index) maskA |= county.U8(0x38);
            else if (county.U8(0x34) == b.Index) maskB |= county.U8(0x38);
        foreach (int mask in new[] { 1, 4 })
            if ((maskA & mask) != (maskB & mask)) return (maskA & mask) == 0;
        return positions[a] < positions[b];
    }

    private Rec? SelectFreeCounty(Rec p, Rec oldCounty, Rec? changedForce = null, Rec? futureRuler = null)
    {
        var cities = AdjacentCities(w.Entry(Tables.Cities, oldCounty.U8(0x35), 51)).ToList();
        if (cities.Count == 0) return null;
        Shuffle(cities);
        var positions = cities.Select((city, i) => (city, i)).ToDictionary(e => e.city, e => e.i);
        // 0x24D7B0 insertion sort: compare to the preceding node, then walk backwards until insertion.
        for (int i = 1; i < cities.Count; i++)
        {
            Rec candidate = cities[i];
            if (!PreferCity(p, candidate, cities[i - 1], positions, changedForce, futureRuler)) continue;
            cities.RemoveAt(i);
            int at = i - 1;
            while (at > 0 && PreferCity(p, candidate, cities[at - 1], positions, changedForce, futureRuler)) at--;
            cities.Insert(at, candidate);
        }
        int target = cities[0].Index;
        var counties = w.List(Lists.CountyList).Select(n => n[0]).OfType<Rec>().Where(c => c.U8(0x35) == target ||
            (c.U8(0x34) == target && w.Entry(Tables.Tiles, c.U16(0x3A), 48399).U8(0x14) == 0)).ToList();
        Shuffle(counties);
        return counties.Count != 0 ? counties[0] : throw new InvalidDataException("The native county selection reads an empty list head.");
    }

    // Preflight ordinary succession departure using a copied detached officer and the future force ruler.
    internal void RequireSuccessionDeparture(Rec p, Rec force, Rec successor)
    {
        Rec? home = Map.Base(w, p.U16(0xE2));
        if (!Objects.PersonValid(p) || p.U8(0xE6) != 4 || (p.U16(0x15E) & 0x4000) != 0 ||
            !Objects.Valid(w, home) || Objects.Place(p).Kind is not (5 or 6) ||
            Objects.ItemsOf(w, p).Any() || commands.FindOfficer(p.U16(0x10)) is not null)
            throw new NotSupportedException("This departure requires an ordinary NPC officer in a base without items or commands.");
        Rec detached = new(p.Index, p.Bytes.ToArray(), p.Table, p.NativeAddress);
        detached.Bytes[0xE6] = 5; detached.Bytes[0xE0] = 0; detached.Bytes[0x165] = 0;
        Word(detached, 0xE2, 0);
        if (SelectFreeCounty(detached, CountyOf(home!), force, successor) is null)
            throw new NotSupportedException("This succession departure requires an available destination.");
    }
}

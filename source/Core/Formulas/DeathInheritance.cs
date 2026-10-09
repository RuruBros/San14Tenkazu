using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// 0x2BDD50: death item inheritance, including reserved-command exceptions and native candidate ordering.
internal sealed class DeathInheritance(World w, Rng rng)
{
    private static void Word(Rec p, int at, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(p.Bytes.AsSpan(at), unchecked((ushort)value));
    private static bool Serving(Rec p) => p.U8(0xE6) is >= 1 and <= 4;

    // 0x22CE20 returns the corps' raw force byte, even when it exceeds the force-table limit.
    private int ForceNumber(Rec p)
    {
        Rec corps = w.Entry(Tables.Corps, p.U8(0xE0), 51);
        return Objects.CorpsValid(corps) ? corps.U8(0x10) : 0;
    }
    private static bool Related(Rec a, Rec b, int at) => Objects.PersonValid(a) && Objects.PersonValid(b) &&
        a.U16(0x10) != b.U16(0x10) && a.U16(at) != 0 && a.U16(at) == (at == 0xBC ? b.U16(at) : b.U16(0x10));
    private static bool Child(Rec a, Rec b) => Related(a, b, 0xB4) || Related(a, b, 0xB6);

    // 0x254160 is not a strict ordering: two sworn candidates both compare before each other.
    private bool Before(Rec dying, Rec a, Rec b)
    {
        bool ac = Child(a, dying), bc = Child(b, dying);
        if (ac != bc) return ac;
        bool af = a.U16(0xB2) == dying.U16(0xB2), bf = b.U16(0xB2) == dying.U16(0xB2);
        if (af != bf) return af;
        if (Related(a, dying, 0xBC)) return true;
        if (Related(b, dying, 0xBC)) return false;
        bool al = OfficerRelations.Likes(w, dying, a), bl = OfficerRelations.Likes(w, dying, b);
        if (al != bl) return al;
        int aa = Attack.Age(w, a), ba = Attack.Age(w, b);
        return aa != ba ? aa > ba : a.U16(0x10) < b.U16(0x10);
    }

    internal Rec? SelectReceiver(Rec dying, Rec? explicitReceiver)
    {
        if (Objects.Valid(w, explicitReceiver)) return explicitReceiver;
        return OrderedCandidates(dying).FirstOrDefault();
    }

    internal IReadOnlyList<Rec> OrderedCandidates(Rec dying)
    {
        var candidates = w.List(Lists.OfficerList).Select(n => n[0]).OfType<Rec>().Where(p =>
            ForceNumber(p) == ForceNumber(dying) && (Child(p, dying) || Related(p, dying, 0xBA) || Related(p, dying, 0xBC)) &&
            !OfficerRelations.Dislikes(w, dying, p) && p.U8(0xE6) != 9 && (byte)(p.U8(0xE6) - 6) > 1 && p != dying).ToList();
        // 0x24C590 moves each node backwards using the boolean predicate; List.Sort would change sworn ties.
        for (int i = 1; i < candidates.Count; i++)
        {
            Rec current = candidates[i];
            int j = i;
            while (j > 0 && Before(dying, current, candidates[j - 1]))
            {
                candidates[j] = candidates[j - 1];
                j--;
            }
            candidates[j] = current;
        }
        return candidates;
    }

    private Rec RulerOf(Rec p)
    {
        Rec force = Objects.ForceOf(w, p);
        return w.OfficerById(Objects.ForceValid(force) ? force.U16(0x10) : 0)
            ?? throw new InvalidDataException("The native ruler lookup resolved a null officer pointer.");
    }

    // 0x323ED0 with ownership kind1; invalid owner IDs leave the record unchanged.
    private void GiveTo(Rec item, int owner)
    {
        if ((uint)(owner - 1) >= 6000) return;
        Word(item, 0x4A, owner);
        if (item.U8(0x50) == 1) return;
        item.Bytes[0x50] = 1;
        if (Objects.ItemValid(item)) w.List(Lists.OwnedItems).Add([item]);
    }

    internal void Apply(Rec dying, Rec? explicitReceiver, CommandCancellation.Command? command)
    {
        Rec home = OfficerCapture.ParentCity(w, Map.Base(w, dying.U16(0xE2))!);
        Rec? receiver = SelectReceiver(dying, explicitReceiver);
        Rec force = Objects.ForceOf(w, dying);
        if (Objects.IsPlayerForce(w, force) || receiver is not null && Objects.IsPlayerForce(w, Objects.ForceOf(w, receiver)))
            throw new NotSupportedException("Player inheritance requires the separate history and presentation body.");
        bool tribe = Objects.Valid(w, explicitReceiver) && Objects.InForces46To50(w, explicitReceiver!);
        bool commandValid = command is not null && command.Data.I32(0x58) != -1;
        var removed = new HashSet<Rec>();
        var owned = w.List(Lists.OwnedItems);
        int id = dying.U16(0x10);
        if ((uint)(id - 1) >= 6000) return;
        // Native iteration observes current owners and can see nodes appended by the ownership helper.
        for (int index = 0; index < owned.Count; index++)
        {
            if (owned[index][0] is not { } item || item.U16(0x4A) != id) continue;
            if (commandValid)
            {
                int kind = command!.Kind;
                if ((uint)(kind - 4) <= 5 || kind == 19)
                {
                    if (item.Index == command.Data.U16(0x76)) continue;
                }
                else if (kind is 17 or 18 && item.Index == command.Data.U16(0x72))
                {
                    Rec? member = command.Officers.Where(n => n != id).Select(w.OfficerById)
                        .FirstOrDefault(p => p is not null && Objects.PersonValid(p) && Serving(p));
                    Rec? next = member ?? w.OfficerById(force.U16(0x10));
                    if (Objects.Valid(w, next))
                    {
                        item.Bytes[0x4F] &= 0xF7;
                        GiveTo(item, member is not null ? command.Officers.First(n => n != id && w.OfficerById(n) == member) : next!.U16(0x10));
                        continue;
                    }
                }
            }
            if (!Objects.Valid(w, receiver) || tribe)
            {
                if (!Serving(dying) || dying.U8(0xE6) == 1 || tribe)
                {
                    removed.Add(item);
                    // 0x332FB0 checks the parent-city byte only, without county validity or ownership checks.
                    var counties = w.List(Lists.CountyList).Select(n => n[0]).OfType<Rec>()
                        .Where(c => home.Index is >= 1 and <= 51 && c.U8(0x35) == home.Index).ToList();
                    int choice = rng.Next(counties.Count);
                    if (counties.Count != 0)
                    {
                        Rec county = counties[choice];
                        item.Bytes[0x4F] &= 0xF7;
                        if ((uint)(county.Index - 1) < 500)
                        {
                            Word(item, 0x4A, 0);
                            Word(item, 0x4C, county.Index);
                            item.Bytes[0x50] = 2;
                        }
                    }
                }
                else
                {
                    item.Bytes[0x4F] &= 0xF7;
                    // 0x22E970 returns the force's ruler ID, or literal5014 when its virtual index is zero.
                    Word(item, 0x4A, Math.Clamp(force.Index == 0 ? 5014 : force.U16(0x10), 0, 6000));
                    item.Bytes[0x50] = 1;
                }
            }
            else
            {
                item.Bytes[0x4F] &= 0xF7;
                Word(item, 0x4A, Math.Min(RulerOf(receiver!).U16(0x10), (ushort)6000));
                item.Bytes[0x50] = 1;
            }
        }
        owned.RemoveAll(n => n[0] is { } item && removed.Contains(item));
    }
}

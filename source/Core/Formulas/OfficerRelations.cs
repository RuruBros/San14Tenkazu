using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// Relationship helpers used by death, capture and item inheritance. Native temporary lists are logical lists.
internal static class OfficerRelations
{
    private static bool DistinctValid(World w, Rec? a, Rec? b) =>
        Objects.Valid(w, a) && Objects.Valid(w, b) && a!.U16(0x10) != b!.U16(0x10);

    // 0x2C3B50: a nonzero shared father or mother, with different officer IDs.
    internal static bool Siblings(World w, Rec? a, Rec? b) => DistinctValid(w, a, b) &&
        (a!.U16(0xB4) != 0 && a.U16(0xB4) == b!.U16(0xB4) ||
         a!.U16(0xB6) != 0 && a.U16(0xB6) == b!.U16(0xB6));

    // 0x2C9BD0: the two officers name the same nonzero sworn-group ID.
    internal static bool Sworn(World w, Rec? a, Rec? b) => DistinctValid(w, a, b) &&
        a!.U16(0xBC) != 0 && a.U16(0xBC) == b!.U16(0xBC);

    // 0x2C8AC0: only the first officer's spouse slot is tested.
    internal static bool Spouse(World w, Rec? a, Rec? b) => DistinctValid(w, a, b) &&
        a!.U16(0xBA) != 0 && a.U16(0xBA) == b!.U16(0x10);

    // 0x22CE20: an invalid/out-of-range corps gives zero; its force byte is not bounded again.
    internal static int RawForce(World w, Rec person)
    {
        Rec corps = w.Entry(Tables.Corps, person.U8(0xE0), 51);
        return Objects.CorpsValid(corps) ? corps.U8(0x10) : 0;
    }

    // 0x2767E0: serving officers must first have equal raw force bytes (0x22CE20).
    internal static bool Related(World w, Rec? a, Rec? b)
    {
        if (!Objects.Valid(w, a) || !Objects.Valid(w, b)) return false;
        if (a!.U8(0xE6) is >= 1 and <= 4 && b!.U8(0xE6) is >= 1 and <= 4 && RawForce(w, a) != RawForce(w, b))
            return false;
        return RulerSuccession.HasParent(w, a, b) || RulerSuccession.HasParent(w, b, a) ||
            Spouse(w, a, b) || Siblings(w, a, b) || Sworn(w, a, b) ||
            a.U16(0xB2) != 0 && a.U16(0xB2) == b!.U16(0xB2);
    }

    // 0x232EE0 / 0x232A00: only the target's validity is checked before scanning all eight ID slots.
    internal static bool Likes(World w, Rec person, Rec? target) => Contains(w, person, target, 0xC0);
    internal static bool Dislikes(World w, Rec person, Rec? target) => Contains(w, person, target, 0xD0);

    private static bool Contains(World w, Rec person, Rec? target, int start) =>
        Objects.Valid(w, target) && Enumerable.Range(0, 8).Any(i => person.U16(start + i * 2) == target!.U16(0x10));

    // 0x2283F0 / 0x2280D0: rebuild from resolvable IDs, erase every pointer alias, and compact in original order.
    // The native helper does not check validity of the resolved officers or of the removal target.
    internal static void RemoveLiked(World w, Rec person, Rec? target) => Remove(w, person, target, 0xC0);
    internal static void RemoveDisliked(World w, Rec person, Rec? target) => Remove(w, person, target, 0xD0);

    private static void Remove(World w, Rec person, Rec? target, int start)
    {
        var retained = new List<Rec>();
        for (int i = 0; i < 8; i++)
        {
            int id = person.U16(start + i * 2);
            if ((uint)(id - 1) < 6000 && w.OfficerById(id) is { } other && !ReferenceEquals(other, target))
                retained.Add(other);
        }
        person.Bytes.AsSpan(start, 16).Clear();
        for (int i = 0; i < retained.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(person.Bytes.AsSpan(start + i * 2), Math.Min(retained[i].U16(0x10), (ushort)6000));
    }

    // 0x24FD70: stable insertion sort of native list nodes by birth year, then unsigned officer ID.
    internal static void SortSworn(List<Rec> people)
    {
        for (int i = 1; i < people.Count; i++)
        {
            Rec item = people[i];
            int j = i;
            while (j > 0 && (item.U16(0xAC) < people[j - 1].U16(0xAC) ||
                (item.U16(0xAC) == people[j - 1].U16(0xAC) && item.U16(0x10) < people[j - 1].U16(0x10))))
            {
                people[j] = people[j - 1];
                j--;
            }
            people[j] = item;
        }
    }
}

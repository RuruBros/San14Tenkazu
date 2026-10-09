using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

internal static class ForceFall
{
    // 0xA21C0: erase the pointer-keyed force cache entry when present.
    internal static void RemoveForceCache<T>(IDictionary<ulong, T> cache, ulong forcePointer) => cache.Remove(forcePointer);

    // 0x292070 with available temporary pools: distinct unsigned force scores are sorted descending.
    // Only a sole force in the lowest score group receives grade4, and only with at least four scores.
    internal static int UpdateStandings(World w)
    {
        Rec[] forces = w.Table(Tables.Forces).Where(f => Objects.ForceValid(f) && f.Index < 46).ToArray();
        int[] scores = forces.Select(f => (int)f.U16(0x1BC)).Distinct().OrderDescending().ToArray();
        foreach (Rec f in forces) f.Bytes[0x1C4] = 0;
        Rec[] last = scores.Length >= 4 ? forces.Where(f => f.U16(0x1BC) == scores[^1]).ToArray() : [];
        foreach (Rec f in forces)
        {
            int rank = Array.IndexOf(scores, f.U16(0x1BC)) + 1;
            f.Bytes[0x1C4] = rank switch
            {
                1 => (byte)(f.U16(0x1BC) == 0 ? 3 : 1),
                2 => 2,
                _ => (byte)(last.Length == 1 && ReferenceEquals(last[0], f) ? 4 : 3),
            };
        }
        return 1;
    }

    // 0x2EA9E0: transfer unbound items to the victor's ruler without changing ownership kind or list nodes.
    internal static void TransferItemsToRuler(World w, Rec? person, Rec? victor)
    {
        if (!Objects.Valid(w, person) || !Objects.Valid(w, victor)) return;
        int ruler = victor!.U16(0x10);
        if ((uint)(ruler - 1) >= 6000) return;
        foreach (Rec item in Objects.ItemsOf(w, person!))
        {
            if ((item.U8(0x4F) & 1) != 0) continue;
            item.Bytes[0x4F] &= 0xF7;
            BinaryPrimitives.WriteUInt16LittleEndian(item.Bytes.AsSpan(0x4A), (ushort)ruler);
        }
    }

    // 0x32B060 -> 0x335060: membership is checked first, then every pointer alias is erased.
    internal static int RemoveFromForceList(World w, Rec? force)
    {
        var list = w.List(Lists.ForceList);
        if (!list.Any(n => ReferenceEquals(n[0], force))) return 0;
        list.RemoveAll(n => ReferenceEquals(n[0], force));
        return 1;
    }

    // 0x279A50, called by 0x293790: four ruler pairs set scenario bits when the victor is the player.
    // This helper does not dissolve the force, choose a successor or complete unification.
    internal static void UpdateScenarioFlags(World w, Rec? defeated, Rec? victor)
    {
        ReadOnlySpan<(int Defeated, int Victor)> pairs = [(521, 952), (521, 35), (521, 769), (35, 266)];
        for (int bit = 0; bit < pairs.Length; bit++)
        {
            uint flags = w.WorldState.U32(0x169C), mask = 1u << bit;
            if ((flags & mask) != 0) continue;
            if (!Objects.Valid(w, defeated) || defeated!.U16(0x10) != pairs[bit].Defeated) continue;
            if (!Objects.Valid(w, victor) || victor!.U16(0x10) != pairs[bit].Victor) continue;
            if (Objects.IsPlayerForce(w, victor))
                BinaryPrimitives.WriteUInt32LittleEndian(w.WorldState.Bytes.AsSpan(0x169C), flags | mask);
        }
    }
}

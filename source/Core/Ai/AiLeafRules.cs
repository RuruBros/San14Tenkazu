namespace San14Tenkazu;

// Verified numeric/list effects of leaves whose original descriptions were uncertain.
internal static class AiLeafRules
{
    private static readonly int[] specificLordIds = [0x209, 0x3B8, 0x229, 0x3BA, 0x303, 0x3AA, 0x108, 0x29A, 0x9E, 0xDF, 0x3B5, 0x28E, 0x155];
    internal static int SpecificLord(int choice) => (uint)choice < specificLordIds.Length ? specificLordIds[choice] : 0;

    internal static ulong SpecificForce(int choice, IReadOnlyList<ulong> forces, IReadOnlyList<int> lords)
    {
        if (forces.Count != lords.Count) throw new ArgumentException("Unequal list lengths.");
        ulong selected = 0;
        for (int i = 0; i < forces.Count; i++)
            if (lords[i] == SpecificLord(choice)) selected = forces[i];
        return selected;
    }

    internal static ulong[] RemoveAll(IReadOnlyList<ulong> source, ulong value) => source.Where(p => p != value).ToArray();
    internal static ulong[] AppendDistrict(IReadOnlyList<ulong> source, ulong district, bool valid) =>
        valid ? [.. source, district] : [.. source];
    internal static ulong[] UniqueInOrder(IEnumerable<ulong> source) => source.Distinct().ToArray();

    internal static int ResolutionStatus(bool helperSucceeded) => helperSucceeded ? 1 : 2;
    internal static bool InspectNeedsCandidates(int reserve) => reserve >= 500;
    internal static bool RejectBuildingPurpose(ushort purpose) => purpose is 0 or 7 or 17 or 18;

    internal static (int Advisor, int[][] Groups) PolicyAssignment(int slot, int person, int oldAdvisor, IReadOnlyList<int[]> oldGroups)
    {
        if ((uint)slot > 5 || oldGroups.Count != 5) throw new ArgumentOutOfRangeException(nameof(slot));
        int advisor = slot == 0 ? person : oldAdvisor == person ? 0 : oldAdvisor;
        int[][] groups = oldGroups.Select(g => g.Where(p => p != 0 && p != person && p != advisor).Distinct().ToArray()).ToArray();
        if (slot != 0) groups[slot - 1] = [.. groups[slot - 1], person];
        return (advisor, groups);
    }
}

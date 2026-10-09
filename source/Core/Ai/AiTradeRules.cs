namespace San14Tenkazu;

internal static class AiTradeRules
{
    // Caller excludes the candidate itself and invalid other countries before supplying kinds.
    internal static bool CandidateAllowed(IReadOnlyList<byte> relations, IEnumerable<int> otherKinds)
    {
        if (relations.Count != 5) throw new ArgumentException("Five relationship bytes are required.", nameof(relations));
        return otherKinds.All(kind => relations[Math.Clamp(kind, 0, 4)] == 1);
    }

    // Native comparator 0x978E0: below 1000 first, then larger values, then smaller original kind.
    internal static bool Precedes(int firstKind, ushort firstValue, int secondKind, ushort secondValue) =>
        (firstValue < 1000) != (secondValue < 1000) ? firstValue < 1000 :
        firstValue != secondValue ? firstValue > secondValue : firstKind < secondKind;
}

namespace San14Tenkazu;

internal static class AiRemainingPredicates
{
    // P30 reads the army commander's home via 0x22A610, not the first object's own home field.
    internal static bool CommanderHomeIs(ushort home, ushort foothold, bool validArmy, bool validBase) =>
        validArmy && validBase && home == foothold;
    internal static bool PersonAt(ushort location, ushort foothold, bool validPerson, bool validBase) =>
        validPerson && validBase && location == foothold;
    internal static bool FormerLordIs(ushort formerLord, ushort lord, bool validDistrict, bool validPerson) =>
        validDistrict && validPerson && formerLord == lord;

    // Date storage: signed year short and signed month/day/hour bytes; native comparison uses 30-day months.
    internal static int DateValue(short year, sbyte month, sbyte day, sbyte hour) =>
        unchecked(hour + (day - 31 + (month + year * 12) * 30) * 24);
    internal static bool DateReached(int stored, int now, bool validForce) => validForce && stored <= now;
    internal static bool InChallenge(bool managerExists, bool worldStateExists, sbyte challenge) =>
        managerExists && worldStateExists && challenge != -1;

    // P137 keeps the relational area helper as an explicit input.
    internal static bool FakeNewsTarget(byte ailment23, byte ailment24, byte ailment25, byte byte140, byte kind,
        int areaCount, bool validFirst, bool validSecond) => validFirst && validSecond &&
        (ailment23 == 0 || ailment24 == 0 || ailment25 == 0) && byte140 == 0 && kind != 2 && areaCount > 0;

    // Native outcomes, after lookups and the predicate's gate. P99 has no command gate.
    internal static bool SchemeOutcome(int predicate, int outcome, bool playerTarget, bool prerequisites) =>
        prerequisites && (predicate switch
        {
            82 or 91 or 95 or 99 => AiCommandPredicates.SchemeLikely(outcome, playerTarget),
            103 => outcome == 1,
            145 => outcome is 1 or 2,
            _ => throw new ArgumentOutOfRangeException(nameof(predicate)),
        });

    internal static bool StateAdvantage(int own, int hostile, int required) =>
        own < required && required <= unchecked(own + hostile);
}

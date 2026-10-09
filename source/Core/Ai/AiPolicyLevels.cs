namespace San14Tenkazu;

internal readonly record struct AiPolicyLevelContribution(bool ValidPolicy, byte BaseLevel, int CreditBonus, IReadOnlyList<byte> Effects);

// 0x41C440 returns an aggregate level. The effect's benefit values at 0x41C200 are separate.
internal static class AiPolicyLevels
{
    internal static bool SelectsMember(bool validOfficer, bool ruler, ushort remainingDays) => validOfficer && (ruler || remainingDays == 0);
    internal static int ReadLevel(int accumulated) => Math.Min(accumulated, 10);
    internal static int SetLevel(int value) => Math.Max(0, value);

    // 0x258D90 processes all eight effect bytes, including duplicate matches.
    // CreditBonus is the observed native 0x2A9D30 result, not a replacement formula.
    internal static int AddOfficer(int accumulated, int requestedEffect, AiPolicyLevelContribution officer)
    {
        if (!officer.ValidPolicy) return accumulated;
        int ownLevel = Math.Min(unchecked(officer.BaseLevel + officer.CreditBonus), 10);
        foreach (byte effect in officer.Effects)
            if (effect is >= 1 and <= 100 && effect == requestedEffect)
                accumulated = SetLevel(unchecked(ReadLevel(accumulated) + ownLevel));
        return accumulated;
    }

    internal static int Accumulate(int requestedEffect, IEnumerable<AiPolicyLevelContribution> officers)
    {
        int accumulated = 0;
        foreach (AiPolicyLevelContribution officer in officers) accumulated = AddOfficer(accumulated, requestedEffect, officer);
        return accumulated;
    }
}

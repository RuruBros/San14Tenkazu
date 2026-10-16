namespace San14Tenkazu;

internal enum AbilityRuleMode { Original, Cap200, Growth200 }

internal sealed record RuntimeRuleSettings(bool Traits9 = false, AbilityRuleMode Abilities = AbilityRuleMode.Original, bool TraitList9 = false)
{
    internal static readonly RuntimeRuleSettings Original = new();
    internal int Slots => Traits9 ? 9 : 5;
    internal bool Growth => Abilities == AbilityRuleMode.Growth200;
    internal bool Expanded => Abilities != AbilityRuleMode.Original;
    internal void Validate()
    {
        if (!Enum.IsDefined(Abilities)) throw new ArgumentOutOfRangeException(nameof(Abilities));
        if (TraitList9 && !Traits9) throw new InvalidOperationException(L.RuntimeErrors.NineTraitsRequired);
    }
}

internal static class AbilityProgress
{
    internal static int ReapplyExperience(int level, int experience, bool charm, bool growth)
    {
        if (level is < 0 or > 200 || experience < 0) throw new InvalidDataException(L.RuntimeErrors.ExperienceOutOfRange);
        // A save may already contain the desired curve while a restarted process has original code.
        // Preserve an already valid value, including overlapping intervals; do not convert it twice.
        long start = Threshold(level, charm, growth), end = Threshold(level + 1, charm, growth);
        if (experience >= start && (level == 200 || experience < end)) return experience;
        return ConvertExperience(level, experience, charm, !growth, growth);
    }
    internal static long Threshold(long level, bool charm, bool growth)
    {
        if (growth && level > 100) return Threshold(100, charm, false) * level * level / 10000;
        return (charm ? level * level : level * level * level) + level * 5000 + Math.Max(level - 70, 0) * 5000;
    }

    // Preserve the fraction of the current level, never grant or remove an ability point.
    // A record fitting neither curve needs an explicit edit rather than an invented baseline.
    internal static int ConvertExperience(int level, int experience, bool charm, bool fromGrowth, bool toGrowth)
    {
        if (level is < 0 or > 200 || experience < 0) throw new InvalidDataException(L.RuntimeErrors.ExperienceOutOfRange);
        long target = Threshold(level, charm, toGrowth), end = Threshold(level + 1, charm, toGrowth);
        long start = Threshold(level, charm, fromGrowth), next = Threshold(level + 1, charm, fromGrowth);
        if (fromGrowth == toGrowth && experience >= target && (level == 200 || experience < end)) return experience;
        if (level == 200 && experience >= start) return checked((int)(target + experience - start));
        if (experience < start || experience >= next)
        {
            if (experience >= target && (level == 200 || experience < end)) return experience;
            throw new InvalidDataException(L.RuntimeErrors.ExperienceCurveMismatch($"{level}", $"{experience:N0}"));
        }
        return checked((int)(target + (experience - start) * (end - target) / (next - start)));
    }
}

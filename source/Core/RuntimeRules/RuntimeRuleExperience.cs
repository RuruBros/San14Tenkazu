namespace San14Tenkazu;

// Experience values rescaled to the wanted curve, ability levels raised back (from experience or the located
// save), officers whose nine traits came back from that save, and the save's file name when one was used.
internal sealed record RuntimeRuleApplyCounts(int Converted, int Restored, int TraitsRestored = 0, string? SaveName = null);

internal static class RuntimeRuleExperience
{
    internal static RuntimeRuleWrite[] Plan(byte[] record, ulong address, RuntimeRuleSettings previous,
        RuntimeRuleSettings wanted, bool reapply, bool convertExperience)
    {
        if (!wanted.Growth && !previous.Growth) return [];
        var writes = new List<RuntimeRuleWrite>();
        for (int ability = 0; ability < OfficerFields.Abilities.Length; ability++)
        {
            FieldDef abilityField = OfficerFields.Abilities[ability], experienceField = OfficerFields.ExperienceOf(abilityField);
            int level = (int)abilityField.Read(record), offset = experienceField.Offset;
            if (level < 100) continue;
            int experience = (int)experienceField.Read(record);
            bool fromGrowth = previous.Growth && OfficerFields.FitsExperience(record, abilityField, true);
            int adjusted;
            try
            {
                adjusted = reapply ? AbilityProgress.ReapplyExperience(level, experience, ability == 4, wanted.Growth)
                    : AbilityProgress.ConvertExperience(level, experience, ability == 4, fromGrowth, wanted.Growth);
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException)
            {
                throw new InvalidDataException(L.RuntimeErrors.OfficerConversionError($"{BitConverter.ToUInt16(record, OfficerFields.IdOffset)}",
                    $"{OfficerFields.Abilities[ability].Label}", $"{error.Message}"), error);
            }
            if (adjusted != experience)
            {
                if (!convertExperience) throw new InvalidOperationException(L.RuntimeErrors.ConversionRequired);
                writes.Add(new(address + (uint)offset, BitConverter.GetBytes(experience), BitConverter.GetBytes(adjusted), false));
            }
            // Growth mode: a level below what its experience reached was clamped by the original loader
            // (0x2258A7 keeps the experience). Restore the level the game itself would reach on the next gain.
            if (wanted.Growth && AbilityProgress.LevelFor(adjusted, ability == 4, true, level) is int reached && reached > level)
                writes.Add(new(address + (uint)abilityField.Offset, [(byte)level], [(byte)reached], false));
        }
        return writes.ToArray();
    }

    internal static bool IsLevelRestore(RuntimeRuleWrite write) => write.Before.Length == 1;

    internal static bool NeedsReapply(byte[] record, RuntimeRuleSettings settings)
    {
        if (!settings.Growth) return false;
        for (int ability = 0; ability < OfficerFields.Abilities.Length; ability++)
        {
            FieldDef abilityField = OfficerFields.Abilities[ability];
            int level = (int)abilityField.Read(record);
            if (level < 100) continue;
            int experience = (int)OfficerFields.ExperienceOf(abilityField).Read(record);
            int adjusted = AbilityProgress.ReapplyExperience(level, experience, ability == 4, true);
            if (adjusted != experience || AbilityProgress.LevelFor(adjusted, ability == 4, true, level) > level) return true;
        }
        return false;
    }
}

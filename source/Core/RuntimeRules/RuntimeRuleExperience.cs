namespace San14Tenkazu;

internal static class RuntimeRuleExperience
{
    internal static RuntimeRuleWrite[] Plan(byte[] record, ulong address, RuntimeRuleSettings previous,
        RuntimeRuleSettings wanted, bool reapply, bool convertExperience)
    {
        if (!wanted.Growth && !previous.Growth) return [];
        var writes = new List<RuntimeRuleWrite>();
        for (int ability = 0; ability < 5; ability++)
        {
            int level = record[0xEC + ability], offset = 0x104 + ability * 4;
            if (level < 100) continue;
            int experience = BitConverter.ToInt32(record, offset);
            bool fromGrowth = previous.Growth && OfficerFields.FitsExperience(record, OfficerFields.Abilities[ability], true);
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
            if (adjusted == experience) continue;
            if (!convertExperience) throw new InvalidOperationException(L.RuntimeErrors.ConversionRequired);
            writes.Add(new(address + (uint)offset, BitConverter.GetBytes(experience), BitConverter.GetBytes(adjusted), false));
        }
        return writes.ToArray();
    }

    internal static bool NeedsReapply(byte[] record, RuntimeRuleSettings settings)
    {
        if (!settings.Growth) return false;
        for (int ability = 0; ability < 5; ability++)
        {
            int level = record[0xEC + ability];
            if (level < 100) continue;
            int experience = BitConverter.ToInt32(record, 0x104 + ability * 4);
            if (AbilityProgress.ReapplyExperience(level, experience, ability == 4, true) != experience) return true;
        }
        return false;
    }
}

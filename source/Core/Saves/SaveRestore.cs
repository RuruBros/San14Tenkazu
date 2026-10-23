namespace San14Tenkazu;

// What the original loader took from a record that a located save still holds. Loading under original
// code limits abilities to 100 (0x2258A7) and clears all nine trait slots of an officer holding six or
// more (0x225711); experience and everything else stay, so the save record is matched on those first.
internal static class SaveRestore
{
    internal const int TraitOffset = 0x118, TraitBytes = 18, AbilityOffset = 0xEC;

    internal static RuntimeRuleWrite[] Plan(byte[] record, ulong address, SaveOfficer saved, RuntimeRuleSettings wanted, bool preserveTraits = false)
    {
        if (!GameSaveFile.SameOfficer(record, saved)) return [];
        var writes = new List<RuntimeRuleWrite>();
        if (!preserveTraits && wanted.Traits9 && saved.TraitCount >= 6 && record.AsSpan(TraitOffset, TraitBytes).IndexOfAnyExcept((byte)0) < 0)
        {
            byte[] after = new byte[TraitBytes];
            for (int slot = 0; slot < 9; slot++) BitConverter.GetBytes(saved.Traits[slot]).CopyTo(after, slot * 2);
            writes.Add(new(address + TraitOffset, new byte[TraitBytes], after, false));
        }
        if (wanted.Expanded)
            for (int ability = 0; ability < 5; ability++)
                if (record[AbilityOffset + ability] == 100 && saved.Abilities[ability] > 100 && saved.Abilities[ability] <= 200)
                    writes.Add(new(address + (uint)(AbilityOffset + ability), [100], [saved.Abilities[ability]], false));
        return writes.ToArray();
    }

    internal static bool IsTraitRestore(RuntimeRuleWrite write) => write.Before.Length == TraitBytes;
}

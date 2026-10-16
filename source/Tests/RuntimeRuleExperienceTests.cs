namespace San14Tenkazu.Tests;

public sealed class RuntimeRuleExperienceTests
{
    private static readonly RuntimeRuleSettings Growth = new(true, AbilityRuleMode.Growth200);
    [Theory] [InlineData(100)] [InlineData(101)] [InlineData(150)] [InlineData(199)] [InlineData(200)]
    public void SaveLoadAndRestartPreserveAllFiveAbilitiesAndMatchingExperience(int level)
    {
        var record = new byte[0x1F0];
        for (int ability = 0; ability < 5; ability++)
        {
            record[0xEC + ability] = (byte)level;
            int experience = (int)AbilityProgress.Threshold(level, ability == 4, true) + 17;
            BitConverter.GetBytes(experience).CopyTo(record, 0x104 + ability * 4);
        }
        Assert.False(RuntimeRuleExperience.NeedsReapply(record, Growth));
        foreach (var previous in new[] { RuntimeRuleSettings.Original, Growth })
            Assert.Empty(RuntimeRuleExperience.Plan(record, 0x1000, previous, Growth, true, true));
    }
    [Theory] [InlineData(100)] [InlineData(150)]
    public void SameProcessOldCurveLoadConvertsEveryAbilityIncludingBoundaryAndIsIdempotent(int level)
    {
        var record = new byte[0x1F0];
        for (int ability = 0; ability < 5; ability++)
        {
            record[0xEC + ability] = (byte)level;
            long start = AbilityProgress.Threshold(level, ability == 4, false);
            long end = AbilityProgress.Threshold(level + 1, ability == 4, false);
            BitConverter.GetBytes((int)(start + (end - start) * 9 / 10)).CopyTo(record, 0x104 + ability * 4);
        }
        Assert.True(RuntimeRuleExperience.NeedsReapply(record, Growth));
        Assert.Throws<InvalidOperationException>(() => RuntimeRuleExperience.Plan(record, 0x1000, Growth, Growth, true, false));
        var writes = RuntimeRuleExperience.Plan(record, 0x1000, Growth, Growth, true, true);
        Assert.NotEmpty(writes);
        foreach (var write in writes)
        {
            Assert.False(write.Executable);
            write.After.CopyTo(record, checked((int)(write.Address - 0x1000)));
        }
        for (int ability = 0; ability < 5; ability++)
        {
            Assert.Equal(level, record[0xEC + ability]);
            int experience = BitConverter.ToInt32(record, 0x104 + ability * 4);
            Assert.InRange(experience, AbilityProgress.Threshold(level, ability == 4, true), AbilityProgress.Threshold(level + 1, ability == 4, true) - 1);
        }
        Assert.False(RuntimeRuleExperience.NeedsReapply(record, Growth));
        Assert.Empty(RuntimeRuleExperience.Plan(record, 0x1000, Growth, Growth, true, true));
    }
    [Fact]
    public void InvalidExperienceIsRejectedWithoutInventingAValue()
    {
        var record = new byte[0x1F0]; record[0xEC] = 150;
        Assert.Throws<InvalidDataException>(() => RuntimeRuleExperience.NeedsReapply(record, Growth));
        Assert.Throws<InvalidDataException>(() => RuntimeRuleExperience.Plan(record, 0, Growth, Growth, true, true));
    }
}

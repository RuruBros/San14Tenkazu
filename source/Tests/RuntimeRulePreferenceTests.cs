namespace San14Tenkazu.Tests;

public sealed class RuntimeRulePreferenceTests
{
    private static GameIdentity Identity => new(123, new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc), 0x140000000, "fixture", true, TestGame.Classes);
    [Fact]
    public void SettingsSurviveNewInstancesAndOriginalRestoreIsAlsoPersisted()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var file = new RuntimeRulePreferenceFile(dir); Assert.Null(file.Read());
            var saved = new RuntimeRulePreference(1, Identity.ExeHash, new(true, AbilityRuleMode.Growth200, true), true);
            file.Save(saved); Assert.Equal(saved, new RuntimeRulePreferenceFile(dir).Read());
            file.Save(saved with { Settings = RuntimeRuleSettings.Original, ConvertExperience = false });
            var restored = new RuntimeRulePreferenceFile(dir).Read()!;
            Assert.Equal(RuntimeRuleSettings.Original, restored.Settings); Assert.False(restored.ConvertExperience);
            Assert.Single(Directory.GetFiles(dir));
            Assert.Throws<InvalidDataException>(() => restored.CheckBuild("other-build"));
            File.WriteAllText(file.PathName, "{}"); Assert.Throws<InvalidDataException>(() => file.Read());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public void LegacyMigrationRequiresOwnedCommittedReceiptAndExactCurrentState()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var file = new RuntimeRulePreferenceFile(dir); var id = Identity;
            var target = new RuntimeRuleSettings(true, AbilityRuleMode.Growth200, true);
            var receipt = new RuntimeRuleReceipt(id.ProcessId, id.StartTimeUtc, id.ModuleBase, id.ExeHash, RuntimeRuleSettings.Original, target, "prepared", []);
            var journal = new RuntimeRuleJournal(file.ReceiptPath(id)); journal.Save(receipt);
            Assert.Null(file.AdoptCommitted(id, target));
            journal.Save(receipt with { Phase = "committed", ProcessId = 456 }); Assert.Null(file.AdoptCommitted(id, target));
            journal.Save(receipt with { Phase = "committed" });
            Assert.Null(file.AdoptCommitted(id, null)); Assert.Null(file.AdoptCommitted(id, RuntimeRuleSettings.Original));
            Assert.False(File.Exists(file.PathName));
            Assert.Equal(target, file.AdoptCommitted(id, target)!.Settings);
            journal.Save(receipt with { Phase = "committed", After = RuntimeRuleSettings.Original });
            Assert.Equal(target, file.AdoptCommitted(id, RuntimeRuleSettings.Original)!.Settings);
            Assert.NotEqual(file.ReceiptPath(id), file.ReceiptPath(id with { StartTimeUtc = id.StartTimeUtc.AddMinutes(1) }));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ReconnectPreservesMatchingExperienceAndConversionIsIdempotent(bool charm)
    {
        foreach (bool growth in new[] { false, true })
            for (int level = 100; level <= 200; level++)
            {
                int start = (int)AbilityProgress.Threshold(level, charm, growth), end = (int)AbilityProgress.Threshold(level + 1, charm, growth);
                int experience = start + (end - start) / 2;
                Assert.Equal(experience, AbilityProgress.ReapplyExperience(level, experience, charm, growth));
                int other = (int)AbilityProgress.Threshold(level, charm, !growth);
                int converted = AbilityProgress.ReapplyExperience(level, other, charm, growth);
                Assert.Equal(converted, AbilityProgress.ReapplyExperience(level, converted, charm, growth));
            }
        Assert.Throws<InvalidDataException>(() => AbilityProgress.ReapplyExperience(150, -1, charm, true));
    }
}

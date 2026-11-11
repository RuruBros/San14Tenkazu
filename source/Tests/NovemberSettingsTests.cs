namespace San14Tenkazu.Tests;

public sealed class NovemberSettingsTests
{
    [Fact]
    public void BothReviewedBuildsTransferOnlySettingsAndUseTargetDefaults()
    {
        string[] builds = [GameBuildFacts.ExeSha256, GameNativeFactsNovember2026.ExeSha256];
        foreach (string from in builds)
        foreach (string to in builds)
        {
            foreach (var ability in Enum.GetValues<AbilityRuleMode>())
            foreach (var (traits, list) in new[] { (false, false), (true, false), (true, true) })
            {
                var settings = new RuntimeRuleSettings(traits, ability, list);
                var imported = SettingsMigration.Runtime(new(1, from, settings, true), to);
                Assert.Equal(to, imported.Build); Assert.Equal(settings, imported.Settings);
            }
            var snapshot = new FormationFixture().Snapshot();
            var source = snapshot.Baseline(from); var target = snapshot.Baseline(to);
            var key = source.Defaults[0].Key;
            var result = SettingsMigration.Rules(source.WithChanges([new(key, source.Default(key), source.Default(key) + 1)]), target);
            Assert.Equal(to, result.Build); Assert.Equal(source.Default(key) + 1, result.Desired(key));
            Assert.Equal(target.Defaults, result.Defaults); Assert.False(result.AutoApply);
        }
        Assert.Throws<InvalidDataException>(() => SettingsMigration.Runtime(new(1, builds[0], RuntimeRuleSettings.Original, true), new string('A', 64)));
    }

    [Fact]
    public void StartupPreferencesAndImportTargetsStaySeparateByBuild()
    {
        string directory = Path.Combine(Path.GetTempPath(), "november-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = new RuntimeRulePreferenceFile(directory);
            var legacy = new RuntimeRulePreference(1, GameBuildFacts.ExeSha256, RuntimeRuleSettings.Original, true);
            var november = new RuntimeRulePreference(1, GameNativeFactsNovember2026.ExeSha256, new(Traits9: true), true);
            file.ForBuild(legacy.Build).Save(legacy);
            file.ForBuild(november.Build).Save(november);
            Assert.Equal(legacy, file.Read()); Assert.Equal(november, file.ForBuild(november.Build).Read());
            Assert.NotEqual(file.PathName, file.ForBuild(november.Build).PathName);
            Assert.Throws<InvalidDataException>(() => file.ForBuild(new string('A', 64)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

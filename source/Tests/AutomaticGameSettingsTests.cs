namespace San14Tenkazu.Tests;

public sealed class AutomaticGameSettingsTests
{
    private sealed class Fixture
    {
        internal GameIdentity? Current;
        internal bool Ready, IntentCurrent = true, PrepareAllowed = true, CorruptReadback;
        internal int Finds, Writes;
        internal RuntimeRuleSettings? Rules = RuntimeRuleSettings.Original;
        internal Action? OnRead;
        internal GameSessionObservation Session = new(GameSessionPhase.Playing, 0, 0, []);
        internal readonly RuntimeRulePreference Preference;
        internal readonly AutomaticGameSettings Monitor;
        internal Fixture(RuntimeRuleSettings? settings = null)
        {
            Preference = new(1, GameBuildProfiles.Current.ExeSha256, settings ?? new(true, AbilityRuleMode.Growth200, true), true);
            Monitor = new(new(() => { Finds++; return Current; }, id => Current == id,
                _ => { OnRead?.Invoke(); return Rules; }, _ => Ready,
                (id, settings, path) =>
                {
                    Assert.Equal(Current, id); Assert.Equal($"receipt-{id.ProcessId}", path);
                    if (!PrepareAllowed) return false;
                    Writes++; Rules = CorruptReadback ? RuntimeRuleSettings.Original : settings; return true;
                }, _ => Session));
        }
        internal void Start(int pid = 10) => Current = new(pid, DateTime.UtcNow, 0x140000000, GameBuildProfiles.Current.ExeSha256, true, TestGame.Classes);
        internal AutomaticGameObservation Poll(CancellationToken token = default) => Monitor.Poll(Preference,
            id => $"receipt-{id.ProcessId}", () => IntentCurrent, token);
    }

    public static IEnumerable<object[]> SavedModes()
    {
        foreach (bool traits in new[] { false, true })
        foreach (AbilityRuleMode ability in Enum.GetValues<AbilityRuleMode>())
        foreach (bool list in new[] { false, true })
            if (!list || traits) yield return [traits, (int)ability, list];
    }

    [Theory] [MemberData(nameof(SavedModes))]
    public void LaunchAfterEditorStartPreparesOnceThenConnectsAndPreparesAgainOnRestart(bool traits, int ability, bool list)
    {
        var settings = new RuntimeRuleSettings(traits, (AbilityRuleMode)ability, list);
        int expectedWrites = settings == RuntimeRuleSettings.Original ? 0 : 1;
        var f = new Fixture(settings); Assert.Equal(AutomaticGamePhase.Absent, f.Poll().Phase);
        f.Start(); f.Rules = null; Assert.Equal(AutomaticGamePhase.Starting, f.Poll().Phase);
        f.Rules = RuntimeRuleSettings.Original; Assert.Equal(AutomaticGamePhase.RulesApplied, f.Poll().Phase);
        for (int i = 0; i < 5; i++) Assert.Equal(AutomaticGamePhase.RulesApplied, f.Poll().Phase);
        Assert.Equal(expectedWrites, f.Writes); Assert.Equal(settings, f.Rules); Assert.Equal(2, f.Finds);
        f.Ready = true; Assert.Equal(AutomaticGamePhase.Connect, f.Poll().Phase);
        f.Current = null; Assert.Equal(AutomaticGamePhase.Absent, f.Poll().Phase);
        f.Start(11); f.Ready = false; f.Rules = RuntimeRuleSettings.Original;
        Assert.Equal(AutomaticGamePhase.RulesApplied, f.Poll().Phase); Assert.Equal(2 * expectedWrites, f.Writes);
        Assert.Equal(settings, f.Rules);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void LoadedWorldOrLoadWinningTheFreezeRaceUsesConnectedConversion(bool race)
    {
        var f = new Fixture(); f.Start(); f.Ready = !race; f.PrepareAllowed = !race;
        Assert.Equal(AutomaticGamePhase.Connect, f.Poll().Phase); Assert.Equal(0, f.Writes);
    }

    [Fact]
    public void UnverifiedBuildNeverReadsOrWritesRules()
    {
        var f = new Fixture(); f.Start(); f.Current = f.Current!.Value with { Verified = false };
        f.OnRead = () => throw new Exception("must not read");
        Assert.Equal(AutomaticGamePhase.Unsupported, f.Poll().Phase); Assert.Equal(0, f.Writes);
    }

    [Theory] [InlineData("cancel")] [InlineData("intent")] [InlineData("process")] [InlineData("build")] [InlineData("foreign")]
    public void ChangedInputsNeverWrite(string kind)
    {
        var f = new Fixture(); f.Start(); using var cancellation = new CancellationTokenSource();
        if (kind == "build") f.Current = f.Current!.Value with { ExeHash = "other" };
        if (kind == "foreign") f.Rules = new(true);
        f.OnRead = () =>
        {
            if (kind == "cancel") cancellation.Cancel();
            if (kind == "intent") f.IntentCurrent = false;
            if (kind == "process") f.Start(12);
        };
        Assert.ThrowsAny<Exception>(() => f.Poll(cancellation.Token)); Assert.Equal(0, f.Writes);
    }

    [Fact]
    public void FailedReadbackCannotReportApplied()
    {
        var f = new Fixture { CorruptReadback = true }; f.Start();
        Assert.Throws<InvalidDataException>(() => f.Poll()); Assert.Equal(1, f.Writes);
    }

    [Fact]
    public void NoSavedSettingsNeverInventsOrWritesPreferences()
    {
        var f = new Fixture(); f.Start();
        Assert.Equal(AutomaticGamePhase.Starting, f.Monitor.Poll(null, _ => "", () => true, default).Phase);
        f.Ready = true;
        Assert.Equal(AutomaticGamePhase.Connect, f.Monitor.Poll(null, _ => "", () => true, default).Phase);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public void PopulatedTitleTablesWaitAndSameProcessConnectsAfterLoad()
    {
        var f = new Fixture(); f.Start(); f.Ready = true;
        f.Session = new(GameSessionPhase.Title, 0, 0, []);
        Assert.Equal(AutomaticGamePhase.SessionWaiting, f.Poll().Phase);
        Assert.Equal(0, f.Writes);
        f.Session = new(GameSessionPhase.Transition, 0, 1, []);
        Assert.Equal(AutomaticGamePhase.SessionWaiting, f.Poll().Phase);
        f.Session = new(GameSessionPhase.Playing, 0, 0, []);
        Assert.Equal(AutomaticGamePhase.Connect, f.Poll().Phase);
        f.Session = new(GameSessionPhase.Title, 0, 0, []);
        Assert.Equal(AutomaticGamePhase.SessionWaiting, f.Poll().Phase);
        Assert.Equal(0, f.Writes);
    }
}

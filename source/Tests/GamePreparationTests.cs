namespace San14Tenkazu.Tests;

public sealed class GamePreparationTests
{
    private sealed class Fixture
    {
        internal bool Running, Unchanged = true, FailApply, BadReadback;
        internal int Launches, Writes, Reads, NotReady;
        internal GameIdentity Identity = new(101, DateTime.UtcNow, 0x140000000,
            GameBuildProfiles.Current.ExeSha256, true, TestGame.Classes);
        internal RuntimeRuleSettings? State = RuntimeRuleSettings.Original;
        internal Action? OnLaunch, OnRead;
        internal readonly RuntimeRulePreference Preference = new(1, GameBuildProfiles.Current.ExeSha256, new(true, AbilityRuleMode.Growth200, true), true);
        internal Task<GameIdentity> Run(CancellationToken token = default, int attempts = 4) => new GamePreparation(new(
            () => Running,
            () => { Launches++; Identity = Identity with { StartTimeUtc = DateTime.UtcNow }; OnLaunch?.Invoke(); },
            () => Identity,
            _ => { Reads++; OnRead?.Invoke(); return Reads <= NotReady ? null : State; },
            (id, settings, receipt) =>
            {
                Assert.Equal(Identity, id); Assert.Equal("fixture-receipt", receipt);
                Writes++; if (FailApply) throw new IOException("fixture apply failure");
                State = BadReadback ? RuntimeRuleSettings.Original : settings;
            })).RunAsync(Preference, _ => "fixture-receipt", () => Unchanged, token, _ => Task.CompletedTask, attempts);
    }

    [Fact]
    public async Task FreshStartWaitsForOriginalGuardsAndVerifiesBeforeReady()
    {
        var f = new Fixture { NotReady = 2 };
        Assert.Equal(await f.Run(), f.Identity);
        Assert.Equal(1, f.Launches); Assert.Equal(1, f.Writes); Assert.Equal(4, f.Reads);
        Assert.Equal(f.Preference.Settings, f.State);
    }
    [Fact]
    public async Task ExistingGameIsNeverLaunchedOrModified()
    {
        var f = new Fixture { Running = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Run());
        Assert.Equal(0, f.Launches); Assert.Equal(0, f.Writes); Assert.Equal(0, f.Reads);
    }
    [Theory]
    [InlineData("old")] [InlineData("unknown")] [InlineData("hash")] [InlineData("changed")]
    public async Task DifferentProcessBuildOrRulesCannotBePrepared(string kind)
    {
        var f = new Fixture();
        f.OnLaunch = () =>
        {
            if (kind == "old") f.Identity = f.Identity with { StartTimeUtc = DateTime.UnixEpoch };
            if (kind == "unknown") f.Identity = f.Identity with { Verified = false };
            if (kind == "hash") f.Identity = f.Identity with { ExeHash = "other" };
            if (kind == "changed") f.State = f.Preference.Settings;
        };
        await Assert.ThrowsAnyAsync<Exception>(() => f.Run()); Assert.Equal(0, f.Writes);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FailedTransactionOrReadbackNeverReportsReady(bool badReadback)
    {
        var f = new Fixture { FailApply = !badReadback, BadReadback = badReadback };
        await Assert.ThrowsAnyAsync<Exception>(() => f.Run()); Assert.Equal(1, f.Writes);
    }
    [Fact]
    public async Task CancelAndChangedIntentBeforeApplyNeverWrite()
    {
        using var stop = new CancellationTokenSource();
        var f = new Fixture { OnRead = () => stop.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run(stop.Token)); Assert.Equal(0, f.Writes);
        f = new Fixture(); f.OnRead = () => f.Unchanged = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Run()); Assert.Equal(0, f.Writes);
    }
    [Fact]
    public async Task UnknownStartupBytesTimeOutWithoutWriting()
    {
        var f = new Fixture { State = null };
        await Assert.ThrowsAsync<TimeoutException>(() => f.Run()); Assert.Equal(0, f.Writes);
    }
    [Fact]
    public async Task ProcessReplacementDuringStartupIsRejected()
    {
        var f = new Fixture { NotReady = 1 };
        f.OnRead = () => f.Identity = f.Identity with { ProcessId = f.Identity.ProcessId + 1 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Run()); Assert.Equal(0, f.Writes);
    }
}

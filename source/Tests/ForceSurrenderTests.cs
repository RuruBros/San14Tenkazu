namespace San14Tenkazu.Tests;

public class ForceSurrenderTests
{
    private static GameSnapshot Game(int secondRuler = 2, int firstRuler = 1, int player = 1) => new TestGame()
        .Force(1, firstRuler, 1).Force(2, secondRuler, 2).Force(3, 3, 3).Force(4, 0, 4).Force(46, 46, 46)
        .Officer(firstRuler, "조조", 1, 1, 1).Officer(secondRuler, "유비", 2, 2, 1)
        .Officer(3, "손권", 3, 3, 1).Officer(46, "이민족", 46, 4, 1).Build(playerForce: player);

    [Fact]
    public void BatchPinsBothRulersAndDeduplicatesSourcesInSelectionOrder()
    {
        var requests = ForceSurrenders.Prepare(Game(), [3, 2, 3], 1);
        Assert.Equal([new ForceSurrenders.Request(3, 3, 1, 1), new(2, 2, 1, 1)], requests);
    }

    [Theory]
    [InlineData(1, 2)] // player force
    [InlineData(2, 2)] // recipient cannot surrender to itself
    [InlineData(4, 1)] // no ruler
    [InlineData(46, 1)] // tribe
    [InlineData(99, 1)] // missing source
    [InlineData(2, 4)] // recipient has no ruler
    [InlineData(2, 46)]
    [InlineData(2, 99)]
    public void InvalidMemberRefusesTheWholeSelection(int source, int target) =>
        Assert.Throws<InvalidOperationException>(() => ForceSurrenders.Prepare(Game(), [3, source], target));

    [Fact]
    public void EmptySelectionIsRefused() =>
        Assert.Throws<InvalidOperationException>(() => ForceSurrenders.Prepare(Game(), [], 1));

    [Fact]
    public void ChangedSourceRulerRefusesDispatch()
    {
        var request = ForceSurrenders.Prepare(Game(), [2], 1).Single();
        Assert.Throws<InvalidOperationException>(() => ForceSurrenders.Validate(Game(secondRuler: 22), request));
    }

    [Fact]
    public void ChangedRecipientRulerRefusesDispatch()
    {
        var request = ForceSurrenders.Prepare(Game(), [2], 1).Single();
        Assert.Throws<InvalidOperationException>(() => ForceSurrenders.Validate(Game(firstRuler: 11), request));
    }

    [Fact]
    public void SourceBecomingPlayerForceRefusesDispatch()
    {
        var request = ForceSurrenders.Prepare(Game(), [2], 1).Single();
        Assert.Throws<InvalidOperationException>(() => ForceSurrenders.Validate(Game(player: 2), request));
    }
}

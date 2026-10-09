namespace San14Tenkazu.Tests;

public sealed class KnownBuildsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "San14Tenkazu.Tests." + Guid.NewGuid().ToString("N"));
    private const string Build = "87ED4E46590E0939C9EFCAEF14FA0F892917604DFDA86E0A4BE319FCCFFA2D6A", Other = "0123456789ABCDEF";

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void NothingIsKeptAtFirst()
    {
        var builds = new KnownBuilds(folder);

        Assert.False(builds.IsAllowed(Build));
        Assert.Empty(builds.Pointers(Build));
    }

    [Fact]
    public void WritingAllowedIsKeptForThatBuildOnly()
    {
        new KnownBuilds(folder).Allow(Build);
        var builds = new KnownBuilds(folder);

        Assert.True(builds.IsAllowed(Build));
        Assert.True(builds.IsAllowed(Build.ToLowerInvariant()));
        Assert.False(builds.IsAllowed(Other));
    }

    [Fact]
    public void ThePointersFoundLastReplaceThoseKeptBefore()
    {
        var builds = new KnownBuilds(folder);
        builds.Remember(Build, [new BasePointer(0x1A67088, 0xE198)]);
        builds.Remember(Other, [new BasePointer(0x10, 0x20)]);
        builds.Remember(Build, [new BasePointer(0x1B626E8, 0xDFE8), new BasePointer(0x277DF18, 0xDD38)]);

        Assert.Equal([new BasePointer(0x1B626E8, 0xDFE8), new BasePointer(0x277DF18, 0xDD38)], new KnownBuilds(folder).Pointers(Build));
        Assert.Equal([new BasePointer(0x10, 0x20)], new KnownBuilds(folder).Pointers(Other));
    }

    [Fact]
    public void ALineThatDoesNotReadCountsAsNothingKept()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "build_pointers.txt"), $"{Build} 277DF18-DD38\n");

        Assert.Empty(new KnownBuilds(folder).Pointers(Build));
    }
}

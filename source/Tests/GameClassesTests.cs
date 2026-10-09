namespace San14Tenkazu.Tests;

public class GameClassesTests
{
    // The vtables found by hand on the verified build (Steam 1.0.1.0) before the lookup by class name existed.
    [GameExeFact]
    public void FindsEveryRecordTypeWhereItWasFoundByHand()
    {
        GameClasses classes = GameClasses.Find(File.ReadAllBytes(GameFiles.Exe), moduleBase: 0);

        Assert.Equal(new GameClasses(
            City: 0x13A6690, County: 0x13A6980, Officer: 0x13A6B88, Tile: 0x13A5FB0, Building: 0x13A6770, Force: 0x13A6840,
            Corps: 0x13A68B0, Unit: 0x1342458, World: 0x13B2658, Gate: 0x13A6BF0, Kingdom: 0x13A6F58, Item: 0x13A7120,
            Personality: 0x13A71F0, Tactic: 0x13A6DB8, Policy: 0x13A7258, Doctrine: 0x13A6E88, Post: 0x13A72C0, Rank: 0x13A6FC0, Formation: 0x13A6E20, PolicyEffect: 0x13A7328, DoctrinePanel: 0x13A67D8), classes);
    }

    [GameExeFact]
    public void PlacesTheVtablesAtTheModuleTheGameLoaded()
    {
        GameClasses classes = GameClasses.Find(File.ReadAllBytes(GameFiles.Exe), moduleBase: 0x7FF7_0000_0000);

        Assert.Equal(0x7FF7_0000_0000UL + 0x13A6690, classes.City);
    }

    [Fact]
    public void RefusesAFileThatIsNotAnExe()
    {
        var error = Assert.Throws<InvalidOperationException>(() => GameClasses.Find(new byte[0x400], moduleBase: 0));

        Assert.Contains("형식", error.Message);
    }

    [Fact]
    public void TheCityTableIs47PointersOneCityRecordApart()
    {
        byte[] table = new byte[47 * 8];
        for (int k = 0; k < 47; k++) BitConverter.GetBytes(0x1_0000_0000UL + (ulong)(k * GameMemory.CityStride)).CopyTo(table, k * 8);
        Assert.True(GameMemory.IsCityTableAt(table));

        BitConverter.GetBytes(0x1_0000_0000UL + 46 * GameMemory.CityStride + 8).CopyTo(table, 46 * 8);   // the last one off
        Assert.False(GameMemory.IsCityTableAt(table));
        Assert.False(GameMemory.IsCityTableAt(new byte[47 * 8]));   // empty slots
    }
}

using System.Security.Cryptography;

namespace San14Tenkazu.Tests;

public class GameClassesTests
{
    private static GameClasses Expected(byte[] exe) => Convert.ToHexString(SHA256.HashData(exe)) switch
    {
        GameBuildFacts.ExeSha256 => new(
            City: 0x13A6690, County: 0x13A6980, Officer: 0x13A6B88, Tile: 0x13A5FB0, Building: 0x13A6770, Force: 0x13A6840,
            Corps: 0x13A68B0, Unit: 0x1342458, World: 0x13B2658, Gate: 0x13A6BF0, Kingdom: 0x13A6F58, Item: 0x13A7120,
            Personality: 0x13A71F0, Tactic: 0x13A6DB8, Policy: 0x13A7258, Doctrine: 0x13A6E88, Post: 0x13A72C0, Rank: 0x13A6FC0,
            Formation: 0x13A6E20, PolicyEffect: 0x13A7328, DoctrinePanel: 0x13A67D8),
        GameSessionTests.UpdateHash => new(
            City: 0x13A96D0, County: 0x13A99C0, Officer: 0x13A9BC8, Tile: 0x13A8FF0, Building: 0x13A97B0, Force: 0x13A9880,
            Corps: 0x13A98F0, Unit: 0x1345458, World: 0x13B56C8, Gate: 0x13A9C30, Kingdom: 0x13A9F98, Item: 0x13AA160,
            Personality: 0x13AA230, Tactic: 0x13A9DF8, Policy: 0x13AA298, Doctrine: 0x13A9EC8, Post: 0x13AA300, Rank: 0x13AA000,
            Formation: 0x13A9E60, PolicyEffect: 0x13AA368, DoctrinePanel: 0x13A9818),
        var hash => throw new InvalidDataException($"Missing recorded RTTI reference for SHA-256 {hash}")
    };

    // The independently recorded vtables are tied to the installed EXE's exact hash.
    [GameExeFact]
    public void FindsEveryRecordTypeWhereItWasFoundByHand()
    {
        byte[] exe = File.ReadAllBytes(GameFiles.Exe);
        Assert.Equal(Expected(exe), GameClasses.Find(exe, moduleBase: 0));
    }

    [GameExeFact]
    public void PlacesTheVtablesAtTheModuleTheGameLoaded()
    {
        byte[] exe = File.ReadAllBytes(GameFiles.Exe);
        GameClasses classes = GameClasses.Find(exe, moduleBase: 0x7FF7_0000_0000);
        Assert.Equal(0x7FF7_0000_0000UL + Expected(exe).City, classes.City);
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

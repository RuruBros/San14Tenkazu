namespace San14Tenkazu.Tests;

public sealed class AiClassUsageTests
{
    [AiFileFact]
    public void RegisteredClassesAreDistinguishedFromCurrentTreeCallSites()
    {
        AiTree[] trees = AiTreeFile.Read(AiFileFactAttribute.Folder);
        var used = trees.SelectMany(t => t.Nodes).Select(n => n.Class).ToHashSet();
        Assert.Equal(232, used.Count);
        Assert.Equal(66, AiCatalog.Classes.Count(c => !used.Contains(c.Hash)));
        Assert.All(used, hash => Assert.NotNull(AiCatalog.Class(hash)));

        // These native executors are registered, but their synthetic audit inputs do not
        // establish a call path from the current CE trees.
        foreach (string name in new[] { "CExecuteAreaOccupyNode", "CExecuteAttackAreaBuildingNode", "CDecideAreaOccupyRouteNode" })
        {
            AiClass cls = Assert.Single(AiCatalog.Classes, c => c.Name == name);
            Assert.DoesNotContain(cls.Hash, used);
        }
        AiClass departure = Assert.Single(AiCatalog.Classes, c => c.Name == "CCommandAreaOccupyDepatureNode");
        Assert.Equal(9, trees[(int)AiTreeKind.Corps].Nodes.Count(n => n.Class == departure.Hash));
    }
}

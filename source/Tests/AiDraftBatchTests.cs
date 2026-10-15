namespace San14Tenkazu.Tests;

public partial class AiTreeTests
{
    [Fact]
    public void MultiPageDraftsMergeIdenticalSlotsAndRefuseConflictsBeforeWriting()
    {
        AiTree[] file = AiTreeFile.Parse(MadeUp.File());
        var heap = new MadeUp.Heap(file);
        var live = AiMemory.Read(heap, MadeUp.Heap.ModuleBase, file);
        var first = new AiGuideValue("First page", [new(AiTreeKind.Troops, 3, -1, 1)]);
        var second = new AiGuideValue("Second page", [new(AiTreeKind.Troops, 3, -1, 1)]);
        var other = new AiGuideValue("Other field", [new(AiTreeKind.Troops, 2, 0, 1)]);
        var conflict = AiEdits.Plan(heap, file, live, "Batch", [(first, 9), (other, 1500), (second, 10)]);
        Assert.True(conflict.Refused); Assert.Empty(conflict.Edits);
        Assert.Empty(AiMemory.Differences(heap, live[2]));
        var plan = AiEdits.Plan(heap, file, live, "Batch", [(first, 9), (other, 1500), (second, 9)]);
        Assert.False(plan.Refused, plan.Refusal);
        Assert.Equal(2, plan.Edits.Sum(e => e.Changes.Count));
        GameMemory.Apply(heap, TestGame.Classes, plan.Edits);
        Assert.Equal(9, AiEdits.GameRaw(heap, file, live, first));
        Assert.Equal(1500, AiEdits.GameRaw(heap, file, live, other));
        GameMemory.Apply(heap, TestGame.Classes, Undo.Of(plan.Edits));
        Assert.Empty(AiMemory.Differences(heap, live[2]));
    }
}

using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiTraitTests
{
    [Fact]
    public void EveryExposedTraitHasLoggedSaveLoadAndRestoreEvidence()
    {
        var covered = new HashSet<int>();
        foreach (string file in new[] { "AiSavedTraitVectors.json", "AiHanSavedTraitVectors.json" })
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)));
            JsonElement root = document.RootElement;
            int[] changed = root.GetProperty("changed_offsets").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            covered.UnionWith(changed);
            Assert.Equal("user reported overwriting the trial slot", root.GetProperty("cleanup").GetString());
            Assert.Equal(new[] { "before", "written", "saved", "undone", "loaded", "restored", "final" },
                root.GetProperty("phases").EnumerateArray().Select(x => x.GetString()));
            foreach (JsonElement row in root.GetProperty("cases").EnumerateArray())
            {
                int[] a = row.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (!changed.Contains(a[0]))
                {
                    Assert.All(a.Skip(1), v => Assert.Equal(a[1], v));
                    continue;
                }
                FieldDef field = Assert.Single(AiTraits.Fields, f => f.Offset == a[0]);
                Assert.NotEqual(a[1], a[2]);
                Assert.Equal(a[2], a[3]); // written -> saved
                Assert.Equal(a[1], a[4]); // undo before load
                Assert.Equal(a[2], a[5]); // saved trial value returned after load
                Assert.Equal(a[1], a[6]); // restored
                Assert.Equal(a[1], a[7]); // after user cleaned up the trial slot
                Assert.InRange((long)a[2], field.Min, field.Max);
            }
        }
        Assert.Equal(AiTraits.Fields.Select(f => f.Offset), covered.Order());
    }

    [Fact]
    public void PersonalityFieldsMatchTheNativeSignedReads()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "AiTraitVectors.json")));
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.Equal(84, cases.GetArrayLength());
        foreach (FieldDef field in AiTraits.Fields)
        {
            JsonElement[] answers = cases.EnumerateArray().Where(x => x[0].GetInt32() == field.Offset).ToArray();
            Assert.Equal(7, answers.Length);
            Assert.All(answers, row => Assert.Equal(row[2].GetInt32(), row[3].GetInt32()));
        }
        Assert.Equal(12, AiTraits.Fields.Length);
    }

    [AiFileFact]
    public void FameNameMatchesTheTreeHashAndPersonInfoKind()
    {
        Assert.Equal(0x9300703Fu, AiCatalog.NameHash("Personalityfame"));
        AiNode[] nodes = AiTreeFile.Read(AiFileFactAttribute.Folder)[1].Nodes;
        foreach (int index in new[] { 778, 4351, 7643 })
        {
            Assert.Equal(15, nodes[index].Params[1]);
            Assert.Equal(unchecked((int)0x9300703F), nodes[index].Params[2]);
        }
        FieldDef fame = Assert.Single(AiTraits.Fields, f => f.Offset == 0x16E);
        Assert.Equal(L.Ai.TraitFame, fame.Label);
        Assert.Equal((FieldKind.Int16, -20L, 20L), (fame.Kind, fame.Min, fame.Max));
    }
}

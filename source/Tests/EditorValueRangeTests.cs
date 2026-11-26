namespace San14Tenkazu.Tests;

public class EditorValueRangeTests
{
    [Fact]
    public void OnlyInstallationPathsAndUnambiguousSettingsAreReported()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            string Write(string relative, string text)
            {
                string path = Path.Combine(dir.FullName, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                return path;
            }
            string exe = Write("SAN14CE_KO.exe", "");
            Write("support/extra/SAN14_FormationCap100.dll", "package only");
            Assert.False(EditorRangeEvidence.AtConnection(exe).Formation.HasClues);
            Write("DLC/UserPatch/SAN14_CityCap.dll", "path only");
            Write("SAN14Mods/modules/CityCaps.ini", "[Mod]\nEnabled=1\n[CityCaps]\nMode=2");
            Write("SAN14Mods/modules/FormationCap100.ini", "[mod]\nenabled=1\n[Limits]\nAttack100=1\nDefense100=0\ndefense100=1");
            Write("SAN14_Effect128.dll", "");
            var result = EditorRangeEvidence.AtConnection(exe);
            Assert.Contains("DLC/UserPatch/SAN14_CityCap.dll", result.City.Files);
            Assert.Contains("CityCaps.ini [CityCaps] Mode=2", result.City.Settings);
            Assert.True(result.City.HasClues);
            Assert.True(result.Formation.Incomplete);
            Assert.Contains("FormationCap100.ini [Limits] Attack100=1", result.Formation.Settings);
            Assert.DoesNotContain(result.Formation.Settings, s => s.Contains("Defense100"));
            Assert.Single(result.Personality.Files);
            Assert.Empty(result.Personality.Settings);
            Write("SAN14Mods/modules/CityCaps.ini", "[Mod]\nEnabled=0\n[Other]\nMode=4");
            Assert.Equal(["CityCaps.ini [Mod] Enabled=0"], EditorRangeEvidence.AtConnection(exe).City.Settings);
            Assert.True(EditorRangeEvidence.AtConnection(null).City.Incomplete);
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData(1, 50000, 100000, 50000)]
    [InlineData(2, 100000, 200000, 70000)]
    [InlineData(3, 150000, 300000, 100000)]
    [InlineData(4, 200000, 400000, 150000)]
    public void CityFillUsesTheThreeOfficialResourcesOnly(int scale, int gold, int food, int troops)
    {
        Assert.Equal(gold, EditorValueRanges.OfficialCityFill(scale, CityFields.Gold));
        Assert.Equal(food, EditorValueRanges.OfficialCityFill(scale, CityFields.Food));
        Assert.Equal(troops, EditorValueRanges.OfficialCityFill(scale, CityFields.Troops));
        Assert.Null(EditorValueRanges.OfficialCityFill(scale, CityFields.Durability));
        Assert.Null(EditorValueRanges.OfficialCityFill(0, CityFields.Gold));
        Assert.Equal(int.MaxValue, CityFields.Gold.Max);
    }

    [Theory]
    [InlineData(127, 40)]
    [InlineData(128, 39)]
    [InlineData(255, 9)]
    public void EditingTheOtherSlotPreservesTheUnknownEffectAndConditions(int kind, int condition)
    {
        var data = new TestGame().Personality(1, "A", "", (1, -21), (condition == 9 ? 19 : 79, 8), condition: condition, filter: 0).Build();
        var p = data.Personalities.Single();
        p.Data[PersonalityFields.Kinds[0].Offset] = (byte)kind;
        EditPlan Plan(params FieldChange[] changes) => PersonalityEdits.Plan(data, 1, changes);
        Assert.Empty(Plan().Edits);
        Assert.True(Plan(new FieldChange(PersonalityFields.Kinds[0], 1)).Refused);
        Assert.True(Plan(new FieldChange(PersonalityFields.Values[0], 7)).Refused);
        Assert.True(Plan(new FieldChange(PersonalityFields.Filter, 1)).Refused);
        Assert.True(Plan(new FieldChange(PersonalityFields.Condition, 0)).Refused);
        Assert.True(Plan(new FieldChange(PersonalityFields.ConditionValue, 10)).Refused);
        var plan = Plan(new FieldChange(PersonalityFields.Values[1], 15));
        var change = Assert.Single(Assert.Single(plan.Edits).Changes);
        Assert.Equal(PersonalityFields.Values[1], change.Field);
        var memory = new FakeMemory(data);
        GameMemory.Apply(memory, TestGame.Classes, plan.Edits);
        Assert.Equal(kind, PersonalityFields.Kinds[0].Read(memory[p.Address]));
        Assert.Equal(-21, PersonalityFields.Values[0].Read(memory[p.Address]));
        var undo = Undo.Compare(Undo.Of(plan.Edits), e => memory[e.Address]);
        GameMemory.Apply(memory, TestGame.Classes, undo.Edits);
        Assert.Equal(p.Data, memory[p.Address]);
    }

    [Fact]
    public void OfficialEffect126IsEditableAndNew127And128AreRefusedEvenInEvents()
    {
        var data = new TestGame().Personality(1, "A", "", (1, 7)).Build();
        EditPlan Plan(int kind) => PersonalityEdits.Plan(data, 1, [new(PersonalityFields.Kinds[0], kind)]);
        Assert.False(Plan(126).Refused);
        Assert.True(Plan(127).Refused);
        Assert.True(Plan(128).Refused);
        Assert.True(PersonalityEdits.Plan(data, 1, [new(PersonalityFields.Condition, 40), new(PersonalityFields.Kinds[0], 128)]).Refused);
    }

    [Fact]
    public void AnExtendedEventKeepsItsCompanionWhenItIsNotAnOfficialMoraleSlot()
    {
        var data = new TestGame().Personality(1, "A", "", (1, 10), (19, 8), condition: 40).Build();
        var p = data.Personalities.Single();
        p.Data[PersonalityFields.Kinds[0].Offset] = 128;
        Assert.Empty(PersonalityEdits.Plan(data, 1, []).Edits);
        Assert.True(PersonalityEdits.Plan(data, 1, [new(PersonalityFields.Values[1], 9)]).Refused);
    }

    [Fact]
    public void SavingAnIndependentEditKeepsAStoredExtensionAcrossReopen()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var stock = new FormationFixture().Snapshot().Baseline(GameBuildFacts.ExeSha256);
            var key = new RuleKey("formation", 1, "attack");
            var held = stock with { Overrides = [new(key, 100)] };
            var file = new RuleProfileFile(Path.Combine(dir.FullName, "profile.json"));
            var saved = file.Save(held, file.Read().Stamp);
            var other = key with { Field = "mobility" };
            file.Save(saved.Profile!.WithChanges([new(other, 10, 20)]), saved.Stamp);
            var result = file.Read().Profile!;
            Assert.Equal(100, result.Desired(key));
            Assert.Equal(20, result.Desired(other));
            Assert.Throws<InvalidDataException>(() => (stock with { Overrides = [new(key, 256)] }).Validate());
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData("attack", 45)]
    [InlineData("defense", 40)]
    public void OutOfRangeFormationValuesCanBeKeptButNotAppliedOrImported(string field, int maximum)
    {
        var memory = new FormationFixture();
        var snapshot = memory.Snapshot();
        var clean = snapshot.Baseline(GameBuildFacts.ExeSha256);
        var key = new RuleKey("formation", 1, field);
        var other = key with { Field = "mobility" };
        var stored = clean with { Overrides = [new(key, 100), new(other, 20)] };
        stored.Validate();
        var automatic = stored.Reapply(snapshot.Values);
        Assert.Equal(other, Assert.Single(automatic.Apply).Key);
        Assert.Contains(key, automatic.Conflicts);
        var migration = SettingsMigration.PlanRules(stored, clean);
        Assert.Contains(key, migration.Unavailable);
        Assert.Equal(other, Assert.Single(migration.Changes).Key);
        Assert.Throws<InvalidDataException>(() => clean.WithChanges([new(key, 10, maximum + 1)]));
        Assert.Throws<InvalidDataException>(() => stored.WithChanges([new(key, 10, maximum)]));
        var restorable = EditorValueRanges.FormationRestore(snapshot.Record(1), stored);
        Assert.DoesNotContain(restorable, c => c.Key == key);
        Assert.Equal(100, stored.WithChanges(restorable, true).Desired(key));
        var stock = clean.WithChanges([new(key, 10, maximum)]);
        Assert.Equal(maximum, stock.Desired(key));
        Assert.Equal(10, stock.WithChanges(EditorValueRanges.FormationRestore(snapshot.Record(1), stock), true).Desired(key));
        var f = FormationRules.Field(field);
        var r = snapshot.Record(1);
        r.Data[f.Plain] = 99;
        r.Vector[f.Vector] = FormationRules.Encode(99, f.Rotate, 1);
        Assert.DoesNotContain(EditorValueRanges.FormationRestore(r, clean), c => c.Key == key);
        Assert.DoesNotContain(EditorValueRanges.FormationRestore(r, snapshot.Baseline(GameBuildFacts.ExeSha256)), c => c.Key == key);
    }
}

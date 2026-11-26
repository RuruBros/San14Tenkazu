using San14Tenkazu;
using San14Tenkazu.Tests;

internal static partial class RenderChecks
{
    private static void CheckEditorValueRanges()
    {
        static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
        var city = new CityEditorCard { RangeEvidence = new([], [], false) };
        var resources = CityFields.All.ToDictionary(f => f, f => f == CityFields.Scale ? 1L : 100L);
        var view = new CityEditorCard.View(1, "A", 0, "", System.Drawing.Color.White, "", 0, "", resources, [], [], [], false);
        city.Open(view);
        var fill = Field<PlateButton>(city, "fillCaps");
        var click = Field<Action>(fill, "Click");
        Require(fill.IsEnabled, "The official fill is unavailable");
        click();
        Require(city.Changes(out _)!.City.Count == 3, "Official fill should stage exactly three resources");
        var cityInputs = Field<Dictionary<FieldDef, NumberField>>(city, "fields");
        resources[CityFields.Gold] = 60_000;
        city.Open(view);
        cityInputs[CityFields.Food].Box.Text = "invalid draft";
        click();
        Require(cityInputs[CityFields.Gold].Value == 60_000 && cityInputs[CityFields.Food].Box.Text == "invalid draft"
            && cityInputs[CityFields.Troops].Value == 50_000, "Fill replaced an existing value or draft");
        city.Open(view);
        cityInputs[CityFields.Scale].Box.Text = "invalid scale";
        click();
        Require(city.Pending == 1, "An invalid scale staged an official fill");
        city.Open(view);
        city.RangeEvidence = new(["SAN14_CityCaps.dll"], ["CityCaps.ini [CityCaps] Mode=2"], false);
        click();
        Require(!fill.IsEnabled && city.Pending == 0, "An installed file or selected option enabled a cap fill");
        cityInputs[CityFields.Gold].Set(250_000);
        Require(city.Changes(out _)!.City.Single().Value == 250_000, "Manual city input was capped");
        Console.WriteLine("PASS city: official fill, higher value and invalid draft preserved, invalid scale, unverified option, manual input");

        var fixture = new FormationFixture();
        var snapshot = fixture.Snapshot();
        var profile = snapshot.Baseline(fixture.Identity.ExeHash);
        var formation = new FormationEditorCard();
        var record = snapshot.Record(1);
        var attack = FormationRules.Field("attack");
        var defense = FormationRules.Field("defense");
        var numbers = Field<Dictionary<FormationField, NumberField>>(formation, "fields");
        formation.Open(new(record, profile));
        numbers[attack].Box.Text = "45";
        numbers[defense].Box.Text = "40";
        Require(formation.Changes(out _)?.Length == 2, "Official formation maxima rejected");
        numbers[attack].Box.Text = "46";
        Require(formation.Changes(out _) is null, "Attack 46 accepted");
        numbers[attack].Box.Text = "45";
        numbers[defense].Box.Text = "41";
        Require(formation.Changes(out _) is null, "Defense 41 accepted");
        record.Data[attack.Plain] = 100;
        record.Vector[attack.Vector] = FormationRules.Encode(100, attack.Rotate, record.Id);
        formation.Open(new(record, profile));
        Require(numbers[attack].Box.IsReadOnly && numbers[attack].Value == 100 && !numbers[defense].Box.IsReadOnly,
            "Unknown attack affected the defense input or its original value");
        Console.WriteLine("PASS formation: attack 45/46, defense 40/41, observed 100 retained");

        var card = new PersonalityEditorCard();
        var personality = new PersonalityEditorCard.View(1, "A", "", 40, 0, 1, PersonalityRules.Timing.Always, 0,
            [new(127, -21), new(79, 7)], [new(79, "79", ""), new(127, "127", "")], new HashSet<int>(), []);
        card.Open(personality);
        var kinds = Field<NameChoice[]>(card, "kinds");
        var values = Field<NumberField[]>(card, "values");
        Require(card.Pending == 0 && kinds[0].Picked == 127 && !values[0].Box.IsEnabled, "Opening normalized effect 127");
        values[1].Set(8);
        card.Follow(personality with { Effects = [new(128, -9), new(79, 7)] });
        Require(kinds[0].Picked == 128 && values[0].Value == -9 && card.Changes(out _) is { Count: 1 } changes
            && changes[0] == new FieldChange(PersonalityFields.Values[1], 8), "Refreshing an unknown effect lost an independent change");
        card.Open(personality with { Effects = [new(128, 10), new(19, 8)] });
        Require(card.Pending == 0 && kinds[1].Picked == 19 && !values[1].Box.IsEnabled, "Opening normalized a nonstandard companion event slot");
        Console.WriteLine("PASS personality: event 127/128 and values preserved across refresh, other slot remains editable");
    }
}

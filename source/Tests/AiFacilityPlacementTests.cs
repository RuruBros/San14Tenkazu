using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiFacilityPlacementTests
{
    private static readonly string[] Gates = ["force", "hex", "scenery", "definition", "terrain", "existing", "owner", "capacity", "areaOwner", "prerequisite", "spacing"];
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiFacilityPlacementVectors.json")));
    private static JsonElement[] Items(JsonElement value, string name) => value.GetProperty(name).EnumerateArray().ToArray();
    private static Dictionary<string, bool> Branches(JsonElement row) => Items(row, "branches")
        .ToDictionary(b => b.GetProperty("name").GetString()!, b => b.GetProperty("value").GetBoolean());
    private static bool Value(Dictionary<string, bool> values, string name) => values.TryGetValue(name, out bool value) && value;

    [Fact]
    public void FullNativePlacementReturnsMatchOnlyTheEvaluatedGatePrefix()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("clockStable").GetBoolean());
        Assert.True(root.GetProperty("completeMatrix").GetBoolean());
        Assert.Empty(Items(root, "failures"));
        JsonElement[] rows = Items(root, "cases");
        Assert.Equal(92, root.GetProperty("plannedPlacementCases").GetInt32());
        Assert.Equal(92, rows.Length);
        HashSet<string> rejected = [];
        foreach (JsonElement row in rows)
        {
            JsonElement gates = row.GetProperty("evaluatedGates");
            JsonProperty[] observed = gates.EnumerateObject().ToArray();
            Assert.Equal(Gates.Take(observed.Length), observed.Select(g => g.Name));
            // An unexecuted suffix is absent from the native log. Neutral true values
            // leave the already rejected prefix unchanged; they are not observations.
            bool Check(string name) => !gates.TryGetProperty(name, out JsonElement value) || value.GetBoolean();
            bool allowed = AiFacilityPlacementRules.AllowsPlacement(new(Check("force"), Check("hex"), Check("scenery"),
                Check("definition"), Check("terrain"), Check("existing"), Check("owner"), Check("capacity"),
                Check("areaOwner"), Check("prerequisite"), Check("spacing")));
            Assert.Equal(allowed ? 1 : 0, row.GetProperty("native").GetInt32());
            if (allowed)
            {
                Assert.Equal(Gates.Length, observed.Length);
                Assert.All(observed, g => Assert.True(g.Value.GetBoolean()));
            }
            else
            {
                Assert.False(observed[^1].Value.GetBoolean());
                Assert.All(observed[..^1], g => Assert.True(g.Value.GetBoolean()));
                rejected.Add(observed[^1].Name);
            }
        }
        Assert.Equal(new[] { "areaOwner", "capacity", "definition", "existing", "force", "hex", "owner", "prerequisite", "scenery", "spacing", "terrain" }, rejected.Order().ToArray());
    }

    [Fact]
    public void NativeAreaCapacityCountsOnlyCategoriesTwoThreeAndFour()
    {
        using JsonDocument document = Vectors();
        JsonElement[] rows = Items(document.RootElement, "capacityCases");
        Assert.Equal(128, rows.Length);
        foreach (JsonElement row in rows)
        {
            if (row.TryGetProperty("variant", out JsonElement variant))
            {
                Assert.Equal("invalidArea", variant.GetString());
                Assert.False(row.GetProperty("valid").GetBoolean());
                Assert.False(AiFacilityPlacementRules.AreaCapacityFull(false, 0, 0));
                Assert.Equal(0, row.GetProperty("native").GetInt32());
                continue;
            }
            int count = AiFacilityPlacementRules.CountsForAreaCapacity(row.GetProperty("category").GetByte())
                ? row.GetProperty("includedFacilities").GetInt32() : 0;
            Assert.Equal(count, row.GetProperty("frames")[0].GetProperty("count").GetInt32());
            byte capacity = row.GetProperty("limit").GetByte();
            Assert.Equal(capacity, row.GetProperty("frames")[0].GetProperty("capacity").GetByte());
            Assert.Equal(AiFacilityPlacementRules.AreaCapacityFull(true, count, capacity) ? 1 : 0,
                row.GetProperty("native").GetInt32());
        }
    }

    [Fact]
    public void NativeExistingFacilityAndOwnershipBranchesMatchTheirRawConditions()
    {
        using JsonDocument document = Vectors();
        foreach (JsonElement row in Items(document.RootElement, "cases"))
        {
            Dictionary<string, bool> branches = Branches(row);
            JsonElement gates = row.GetProperty("evaluatedGates");
            if (gates.TryGetProperty("existing", out JsonElement existing))
            {
                bool present = Value(branches, "buildingPresent");
                byte category = present ? row.GetProperty("existingCategory").GetByte() : (byte)0;
                Assert.Equal(AiFacilityPlacementRules.ExistingFacilityAllowed(present, category, Value(branches, "buildingHostile")), existing.GetBoolean());
            }
            if (gates.TryGetProperty("owner", out JsonElement owner))
                Assert.Equal(AiFacilityPlacementRules.HexOwnershipAllowed(Value(branches, "hexOwnerSame"), Value(branches, "hexOwnerHostile")), owner.GetBoolean());
            if (gates.TryGetProperty("areaOwner", out JsonElement areaOwner))
                Assert.Equal(AiFacilityPlacementRules.AreaOwnershipAllowed(row.GetProperty("requestCategory").GetByte(),
                    Value(branches, "areaValid"), Value(branches, "areaOwnerSame")), areaOwner.GetBoolean());
        }
        JsonElement invalid = Items(document.RootElement, "cases").Single(r => r.GetProperty("variant").GetString() == "invalidArea");
        Assert.Equal(4, invalid.GetProperty("requestCategory").GetInt32());
        Assert.False(Value(Branches(invalid), "areaValid"));
        Assert.Equal(1, invalid.GetProperty("native").GetInt32());
    }

    [Fact]
    public void NativePrerequisiteOptionAndRequestedCategoryControlTheirHelperCalls()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        foreach (JsonElement row in Items(root, "cases"))
        {
            JsonElement gates = row.GetProperty("evaluatedGates");
            JsonElement[] events = Items(row, "events");
            JsonElement[] prerequisites = events.Where(e => e.GetProperty("rva").GetUInt32() == 0x2C4990).ToArray();
            if (gates.TryGetProperty("prerequisite", out JsonElement prerequisite))
            {
                bool needed = AiFacilityPlacementRules.NeedsPrerequisite(row.GetProperty("option").GetUInt32());
                Assert.Equal(needed ? 1 : 0, prerequisites.Length);
                Assert.Equal(!needed || prerequisites[0].GetProperty("result").GetInt32() != 0, prerequisite.GetBoolean());
            }
            else Assert.Empty(prerequisites);
            if (gates.TryGetProperty("spacing", out JsonElement spacing))
            {
                JsonElement call = events.Single(e => e.GetProperty("rva").GetUInt32() == 0x27BD50);
                Assert.Equal(AiFacilityPlacementRules.SpacingMode(row.GetProperty("requestCategory").GetByte()), call.GetProperty("args")[1].GetInt32());
                Assert.Equal(call.GetProperty("result").GetInt32() != 0, spacing.GetBoolean());
            }
            if (row.GetProperty("variant").GetString() == "prerequisite")
            {
                bool allowed = row.GetProperty("option").GetUInt32() == 0 || row.GetProperty("required").GetInt32() <= root.GetProperty("policyEffectValue").GetInt32();
                Assert.Equal(allowed ? 1 : 0, row.GetProperty("native").GetInt32());
            }
        }
        JsonElement[] controls = Items(root, "cases").Where(r => r.GetProperty("variant").GetString() == "spacingCategory2").ToArray();
        Assert.Equal(new[] { 1, 0 }, controls.Select(r => r.GetProperty("native").GetInt32()).ToArray());
    }

    [Fact]
    public void NativeSceneryFlagAndInvalidInputsStopBeforeLaterHelpers()
    {
        using JsonDocument document = Vectors();
        JsonElement[] rows = Items(document.RootElement, "cases");
        foreach (JsonElement row in rows.Where(r => r.GetProperty("variant").GetString() == "sceneryFlags"))
        {
            Assert.True(Value(Branches(row), "sceneryPresent"));
            bool allowed = (row.GetProperty("flags").GetInt32() & 1) == 0;
            Assert.Equal(allowed ? 1 : 0, row.GetProperty("native").GetInt32());
            if (!allowed) Assert.DoesNotContain(Items(row, "events"), e => e.GetProperty("rva").GetUInt32() == 0x42B40);
        }
        foreach (JsonElement row in rows.Where(r => r.GetProperty("variant").GetString() == "sceneryFallback"))
        {
            Assert.False(Value(Branches(row), "sceneryPresent"));
            Assert.Equal(1, row.GetProperty("native").GetInt32());
        }
        foreach (JsonElement row in rows.Where(r => r.GetProperty("variant").GetString() is "nullForce" or "dummyForce"))
        {
            Assert.Single(Items(row, "branches"));
            Assert.Empty(Items(row, "events"));
            Assert.Equal(0, row.GetProperty("native").GetInt32());
        }
    }
}

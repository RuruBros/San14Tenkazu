using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class AiFacilityPrerequisiteTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AiFacilityPrerequisiteVectors.json")));
    private static JsonElement[] Items(JsonElement value, string name) => value.GetProperty(name).EnumerateArray().ToArray();
    private static bool Flag(JsonElement row, string name) => row.GetProperty(name).GetBoolean();
    private static JsonElement[] Calls(JsonElement row, uint rva) => Items(row, "events").Where(e => e.GetProperty("rva").GetUInt32() == rva).ToArray();
    private static bool Exception(JsonElement row) => AiFacilityPlacementRules.PrerequisiteException(
        Flag(row, "globalPointerPresent"), row.GetProperty("globalValue").GetUInt32(),
        Flag(row, "featurePointerPresent"), row.GetProperty("featureFlags").GetByte(), row.GetProperty("facilityIndex").GetInt32());

    [Fact]
    public void FullNativePrerequisiteReturnsMatchTheGlobalExceptionAndPolicyComparison()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.True(Flag(root, "completeMatrix"));
        Assert.True(Flag(root, "clockStable"));
        Assert.Empty(Items(root, "failures"));
        Assert.Equal(262, root.GetProperty("plannedCases").GetInt32());
        JsonElement[] rows = Items(root, "cases");
        Assert.Equal(262, rows.Length);
        Assert.Equal(".?AVCPolicyEffectData@@", root.GetProperty("policyEffectDefinitionType").GetString());
        foreach (JsonElement row in rows)
        {
            bool validForce = Flag(row, "forceValid");
            bool exception = Exception(row);
            Assert.Equal(validForce && exception, Flag(row, "exception"));
            JsonElement value = row.GetProperty("evaluatedEffectValue");
            // Zero is neutral for a skipped comparison, not a native value observation.
            int effectValue = value.ValueKind == JsonValueKind.Null ? 0 : value.GetInt32();
            bool allowed = AiFacilityPlacementRules.AllowsPrerequisite(validForce, exception,
                Flag(row, "policyDefinitionValid"), effectValue, row.GetProperty("requiredRead").GetByte());
            Assert.Equal(allowed ? 1 : 0, row.GetProperty("native").GetInt32());
            Assert.All(Items(row, "events").Concat(Items(row, "getterEvents")), e => Assert.True(e.TryGetProperty("result", out _)));
        }
    }

    [Fact]
    public void NativeParameterGettersSelectTheFormatAndConsumeOnlyTheDecoderLowByte()
    {
        using JsonDocument document = Vectors();
        JsonElement[] rows = Items(document.RootElement, "cases");
        bool highDecoderResult = false;
        foreach (JsonElement row in rows)
        {
            ulong length = row.GetProperty("parameterBytes").GetUInt64();
            int index = row.GetProperty("facilityIndex").GetInt32();
            byte policy = AiFacilityPlacementRules.PrerequisitePolicyIndex(length,
                row.GetProperty("legacyPolicyIndex").GetByte(), row.GetProperty("encodedPolicyByte").GetByte(), index);
            byte required = AiFacilityPlacementRules.PrerequisiteRequiredValue(length,
                row.GetProperty("legacyRequired").GetByte(), row.GetProperty("encodedRequiredByte").GetByte(), index);
            Assert.Equal(row.GetProperty("policyEffectId").GetByte(), policy);
            Assert.Equal(row.GetProperty("rawPolicyIndex").GetByte(), policy);
            Assert.Equal(row.GetProperty("requiredRead").GetByte(), required);
            Assert.Equal(row.GetProperty("required").GetByte(), required);
            Assert.Equal(policy <= 100 ? policy : 0, AiFacilityPlacementRules.PolicyEffectLookupIndex(policy));
            JsonElement[] events = Items(row, "getterEvents");
            JsonElement[] decoders = events.Where(e => e.GetProperty("rva").GetUInt32() == 0xDB00).ToArray();
            if ((length & ~3UL) == 76)
            {
                Assert.Equal(6, events.Length);
                Assert.Equal(3, decoders.Length);
                Assert.Equal(new uint[] { 1, 1, 3 }, decoders.Select(e => e.GetProperty("args")[2].GetUInt32() & 255).ToArray());
                Assert.Equal(new byte[] { policy, policy, required }, decoders.Select(e => (byte)e.GetProperty("result").GetUInt32()).ToArray());
                Assert.All(decoders, e => Assert.Equal(index, e.GetProperty("args")[3].GetInt32()));
                highDecoderResult |= decoders.Any(e => e.GetProperty("result").GetUInt32() > 255);
            }
            else Assert.Empty(events);
        }
        Assert.True(highDecoderResult);
        Assert.Equal(new ulong[] { 0, 72, 75, 76, 77, 78, 79, 80 }, rows.Select(r => r.GetProperty("parameterBytes").GetUInt64()).Distinct().Order().ToArray());
    }

    [Fact]
    public void NativePrerequisiteStopsBeforePolicyHelpersOnAnExceptionOrInvalidForce()
    {
        using JsonDocument document = Vectors();
        foreach (JsonElement row in Items(document.RootElement, "cases"))
        {
            bool validForce = Flag(row, "forceValid");
            bool exception = validForce && Exception(row);
            bool lookup = validForce && !exception;
            bool compare = lookup && Flag(row, "policyDefinitionValid");
            Assert.Equal(lookup ? 1 : 0, Calls(row, 0x42410).Length);
            foreach (uint rva in new uint[] { 0x424A0, 0x41C440, 0x42500 })
                Assert.Equal(compare ? 1 : 0, Calls(row, rva).Length);
            Assert.Equal(!compare, row.GetProperty("evaluatedEffectValue").ValueKind == JsonValueKind.Null);
            if (!validForce)
            {
                JsonElement call = Assert.Single(Items(row, "events"));
                Assert.Equal(0x32F500U, call.GetProperty("rva").GetUInt32());
                Assert.Equal(0, call.GetProperty("result").GetInt32());
                continue;
            }
            bool globalPresent = Flag(row, "globalPointerPresent");
            bool active = globalPresent && row.GetProperty("globalValue").GetUInt32() != 0;
            Assert.Equal(!globalPresent ? 1 : !active ? 2 : 4, Calls(row, 0x56EE40).Length);
            Assert.Equal(!active ? 0 : Flag(row, "featurePointerPresent") ? 2 : 1, Calls(row, 0x56EE30).Length);
            if (compare)
            {
                JsonElement value = Assert.Single(Calls(row, 0x41C440));
                Assert.Equal(row.GetProperty("rawPolicyIndex").GetInt32(), value.GetProperty("args")[1].GetInt32());
                Assert.Equal(unchecked((int)value.GetProperty("result").GetUInt32()), row.GetProperty("evaluatedEffectValue").GetInt32());
            }
        }
    }

    [Fact]
    public void NativeBoundaryControlsRecordTheInactiveGameStateAndCopiedFeatureConstruction()
    {
        using JsonDocument document = Vectors();
        JsonElement root = document.RootElement;
        Assert.Equal(0U, root.GetProperty("originalGlobalValue").GetUInt32());
        Assert.Equal(0UL, root.GetProperty("originalFeaturePointer").GetUInt64());
        JsonElement construction = root.GetProperty("copiedFeatureConstruction");
        Assert.Equal(0x3F6BC0U, construction.GetProperty("allocator").GetUInt32());
        Assert.Equal(13, construction.GetProperty("size").GetInt32());
        Assert.Equal(new[] { 8, 4, 1 }, Items(construction, "changes").Select(c => c.GetProperty("size").GetInt32()).ToArray());
        JsonElement[] rows = Items(root, "cases");
        Assert.Equal(new[] { 15, 16, 17 }, rows.Where(r => Flag(r, "exception"))
            .Select(r => r.GetProperty("facilityIndex").GetInt32()).Distinct().Order().ToArray());
        foreach (JsonElement row in rows.Where(r => r.GetProperty("variant").GetString() == "policyIndexBoundary"))
        {
            int policy = row.GetProperty("policyEffectId").GetInt32();
            Assert.Equal(policy == 100, Flag(row, "policyDefinitionValid"));
            if (Flag(row, "exception") || policy != 100) Assert.Equal(1, row.GetProperty("native").GetInt32());
            else
            {
                Assert.Equal(0, row.GetProperty("evaluatedEffectValue").GetInt32());
                Assert.Equal(0, row.GetProperty("native").GetInt32());
            }
        }
        int level = root.GetProperty("policyEffectValue").GetInt32();
        foreach (JsonElement row in rows.Where(r => r.GetProperty("variant").GetString() == "matrix" && !Flag(r, "exception")))
        {
            Assert.Equal(level, row.GetProperty("evaluatedEffectValue").GetInt32());
            Assert.Equal(row.GetProperty("required").GetInt32() <= level ? 1 : 0, row.GetProperty("native").GetInt32());
        }
    }
}

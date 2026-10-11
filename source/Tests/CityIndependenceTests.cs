using System.Buffers.Binary;

namespace San14Tenkazu.Tests;

public sealed class CityIndependenceTests
{
    private const int Force = 3, City = 28, Capital = 29, Governor = 997, Ruler = 557;
    private static TestGame Fixture(int status = 3, int? place = null) => new TestGame()
        .Force(Force, Ruler, Force).Force(44, 0).Force(45, 0)
        .City(City, Force, Governor).City(Capital, Force, Ruler)
        .Officer(Governor, "염유", Force, City, status, place)
        .Officer(Ruler, "원소", Force, Capital, 1);

    [Fact]
    public void AutomaticSlotSelectionMatchesRecordedNativeCases()
    {
        var root = Formulas.Vectors.Read("force_creation_slots.json.gz");
        string world = root.GetProperty("world").GetString()!;
        int count = 0;
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            var fixture = c.GetProperty("fixture");
            if (fixture.GetProperty("requested").GetInt32() != 0) continue;
            var native = Formulas.Vectors.LoadWorld(world);
            Formulas.Vectors.Poke(native, fixture.GetProperty("pokes"));
            GameSnapshot data = Fixture().Build();
            data.ForceRecords.Clear();
            foreach (var force in native.Table(San14Tenkazu.Formulas.Tables.Forces))
                data.ForceRecords[force.Index] = new(force.Index, force.NativeAddress, force.Bytes.ToArray());
            var person = native.Table(San14Tenkazu.Formulas.Tables.Officers)[fixture.GetProperty("person").GetInt32()];
            var officer = new OfficerRecord(person.Index, person.NativeAddress, person.Bytes.ToArray());
            var result = c.GetProperty("result");
            int expected = result.ValueKind == System.Text.Json.JsonValueKind.Null ? 0 : result.GetInt32();
            Assert.Equal(expected, CityIndependence.SelectAutomaticSlot(data, officer));
            count++;
        }
        Assert.True(count > 0);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void AnAppliedResidentGovernorCanCreateOneNewForce(int status)
    {
        GameSnapshot data = Fixture(status).Build();
        Assert.Null(CityIndependence.Refusal(data, City));
        CityIndependence.Request request = CityIndependence.Prepare(data, City);
        Assert.Equal((City, Governor, Force, 44), (request.City, request.Governor, request.FromForce, request.NewForce));
        Assert.Equal(data.Cities[City].Address, request.CityAddress);
        Assert.Equal(data.OfficerById[Governor].Address, request.GovernorAddress);
        Assert.Equal(0, request.RequestedSlot);
    }

    [Fact]
    public void SlotSelectionUsesPreviousRulerThenBloodlineThenHighestNewForceSlot()
    {
        GameSnapshot data = Fixture().Force(9, 0).Build();
        OfficerRecord officer = data.OfficerById[Governor];
        BinaryPrimitives.WriteUInt16LittleEndian(officer.Data.AsSpan(0xB2), 123);
        foreach (ForceRecord f in data.ForceRecords.Values)
            BinaryPrimitives.WriteUInt16LittleEndian(f.Data.AsSpan(0x11E), 234);
        Assert.Equal(45, CityIndependence.SelectSlot(data, officer));
        BinaryPrimitives.WriteUInt16LittleEndian(data.ForceRecords[9].Data.AsSpan(0x11E), 123);
        Assert.Equal(9, CityIndependence.SelectSlot(data, officer));
        BinaryPrimitives.WriteUInt16LittleEndian(data.ForceRecords[44].Data.AsSpan(0x11E), Governor);
        Assert.Equal(44, CityIndependence.SelectSlot(data, officer));
    }

    [Fact]
    public void EmptyOrdinarySlotIsExplicitWhenAutomaticSlotsAreExhausted()
    {
        GameSnapshot data = Fixture().Force(44, 999).Force(45, 999).Force(9, 0).Force(46, 0).Force(51, 0).Build();
        foreach (ForceRecord f in data.ForceRecords.Values)
            BinaryPrimitives.WriteUInt16LittleEndian(f.Data.AsSpan(0x11E), 234);
        Assert.Equal(0, CityIndependence.SelectAutomaticSlot(data, data.OfficerById[Governor]));
        Assert.Equal(9, CityIndependence.SelectSlot(data, data.OfficerById[Governor]));
        Assert.Null(CityIndependence.Refusal(data, City));
        CityIndependence.Request request = CityIndependence.Prepare(data, City);
        Assert.Equal(9, request.NewForce);
        Assert.Equal(9, request.RequestedSlot);
        data.ForceRecords[9].Data[0x10] = 1;
        Assert.Equal(L.Independence.NoSlot, CityIndependence.Refusal(data, City));
        // 0xFFFF is nonzero and therefore native-valid, not an available slot.
        BinaryPrimitives.WriteUInt16LittleEndian(data.ForceRecords[44].Data.AsSpan(0x10), 0xFFFF);
        Assert.Equal(0, CityIndependence.SelectSlot(data, data.OfficerById[Governor]));
    }

    public static IEnumerable<object[]> OrdinarySlots() => Enumerable.Range(1, 45).Select(i => new object[] { i });

    [Fact]
    public void ExplicitSlotUsesHighestEmptyOrdinarySlotAfterAutomaticSelectionFails()
    {
        GameSnapshot data = Fixture().Force(44, 999).Force(45, 999).Force(1, 0).Force(9, 0).Force(37, 0).Build();
        foreach (ForceRecord f in data.ForceRecords.Values)
            BinaryPrimitives.WriteUInt16LittleEndian(f.Data.AsSpan(0x11E), 234);
        Assert.Equal(37, CityIndependence.Prepare(data, City).RequestedSlot);
        data.ForceRecords[37].Data[0x10] = 1;
        Assert.Equal(9, CityIndependence.Prepare(data, City).RequestedSlot);
        data.ForceRecords[9].Data[0x10] = 1;
        Assert.Equal(1, CityIndependence.Prepare(data, City).RequestedSlot);
        data.ForceRecords[1].Data[0x10] = 1;
        Assert.Equal(L.Independence.NoSlot, CityIndependence.Refusal(data, City));
    }

    [Theory]
    [MemberData(nameof(OrdinarySlots))]
    public void EveryEmptyOrdinarySlotCanBeSelected(int slot)
    {
        GameSnapshot data = Fixture().Force(slot, 0).Build();
        ForceRecord empty = data.ForceRecords[slot];
        BinaryPrimitives.WriteUInt16LittleEndian(empty.Data.AsSpan(0x11E), 234);
        data.ForceRecords.Clear();
        data.ForceRecords.Add(slot, empty);
        Assert.Equal(slot, CityIndependence.SelectSlot(data, data.OfficerById[Governor]));
        BinaryPrimitives.WriteUInt16LittleEndian(empty.Data.AsSpan(0x10), 0xFFFF);
        Assert.Equal(0, CityIndependence.SelectSlot(data, data.OfficerById[Governor]));
    }

    [Theory]
    [InlineData(1, "AlreadyRuler")]
    [InlineData(4, "InvalidGovernor")]
    [InlineData(5, "InvalidGovernor")]
    [InlineData(6, "InvalidGovernor")]
    [InlineData(7, "InvalidGovernor")]
    [InlineData(9, "InvalidGovernor")]
    public void NonGovernorStatusesAreRejected(int status, string key) =>
        Assert.Equal(L.Get("Independence." + key), CityIndependence.Refusal(Fixture(status).Build(), City));

    [Fact]
    public void AbsenceUnitCommandsAndMismatchedHomeOrForceAreRejected()
    {
        Assert.Equal(L.Independence.OutsideCity, CityIndependence.Refusal(Fixture(place: 30).Build(), City));
        Assert.Equal(L.Independence.OutsideCity, CityIndependence.Refusal(Fixture().Leading(Governor).Build(), City));
        Assert.Equal(L.Independence.InvalidGovernor, CityIndependence.Refusal(Fixture().With(Governor, OfficerFields.BaseField, Capital).Build(), City));
        Assert.Equal(L.Independence.InvalidGovernor, CityIndependence.Refusal(Fixture().With(Governor, OfficerFields.CorpsField, 0).Build(), City));
    }

    [Fact]
    public void MissingGovernorUnownedCityCapitalAndLastCityAreRejected()
    {
        Assert.Equal(L.Independence.NoGovernor, CityIndependence.Refusal(Fixture().City(City, Force).Build(), City));
        Assert.Equal(L.Independence.NoForce, CityIndependence.Refusal(Fixture().City(City, 0, Governor).Build(), City));
        Assert.Equal(L.Independence.RulerCity, CityIndependence.Refusal(Fixture().With(Ruler, OfficerFields.BaseField, City).Build(), City));
        Assert.Equal(L.Independence.RulerCity, CityIndependence.Refusal(Fixture().City(Capital, 0).Build(), City));
        foreach (int id in new[] { -1, 0, 47, 52, 999 }) Assert.Equal(L.Independence.NoCity, CityIndependence.Refusal(Fixture().Build(), id));
    }

    [Fact]
    public void MarkedAndSpecialOfficersRetainTheNativeEditEventRestrictions()
    {
        GameSnapshot marked = Fixture().Build();
        BinaryPrimitives.WriteUInt16LittleEndian(marked.OfficerById[Governor].Data.AsSpan(0x15E), 0x4000);
        Assert.Equal(L.Independence.SpecialOfficer, CityIndependence.Refusal(marked, City));
        foreach (int special in new[] { 4001, 4101, 5001, 5100 })
        {
            GameSnapshot data = Fixture().City(City, Force, special).Officer(special, "특수", Force, City, 3).Build();
            Assert.Equal(L.Independence.SpecialOfficer, CityIndependence.Refusal(data, City));
        }
    }

    [Fact]
    public void NativeSuccessRequiresRulerCityAndUnrelatedCityReadback()
    {
        GameSnapshot before = Fixture().Build();
        CityIndependence.Request request = CityIndependence.Prepare(before, City);
        OfficerRecord[] officers = before.Officers.Select(o => o with { Data = (byte[])o.Data.Clone() }).ToArray();
        GameSnapshot after = new()
        {
            Cities = before.Cities.Select(c => c with { Data = (byte[])c.Data.Clone() }).ToArray(),
            Counties = before.Counties, CastleByCity = before.CastleByCity, TileOwners = before.TileOwners, TileCounties = before.TileCounties,
            Officers = officers, OfficerById = officers.ToDictionary(o => o.Id), Units = before.Units,
            Forces = before.Forces.ToDictionary(p => p.Key, p => p.Key == 44 ? p.Value with { RulerId = Governor } : p.Value),
            ForceRecords = before.ForceRecords.ToDictionary(p => p.Key, p => p.Value with { Data = (byte[])p.Value.Data.Clone() }),
            CorpsForce = (byte[])before.CorpsForce.Clone()
        };
        BinaryPrimitives.WriteUInt16LittleEndian(after.ForceRecords[44].Data.AsSpan(0x10), Governor);
        OfficerFields.CorpsField.Encode(2).CopyTo(after.OfficerById[Governor].Data, OfficerFields.CorpsOffset);
        OfficerFields.StatusField.Encode(1).CopyTo(after.OfficerById[Governor].Data, OfficerFields.StatusOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(after.Cities[City].Data.AsSpan(CityFields.CorpsOffset), 2);
        after.CorpsForce[2] = 44;
        Assert.True(CityIndependence.Confirmed(before, after, request));
        Assert.False(CityIndependence.Confirmed(before, before, request));
        OfficerFields.StatusField.Encode(3).CopyTo(after.OfficerById[Governor].Data, OfficerFields.StatusOffset);
        Assert.False(CityIndependence.Confirmed(before, after, request));
        OfficerFields.StatusField.Encode(1).CopyTo(after.OfficerById[Governor].Data, OfficerFields.StatusOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(after.Cities[Capital].Data.AsSpan(CityFields.CorpsOffset), 2);
        Assert.False(CityIndependence.Confirmed(before, after, request));
    }

    [Fact]
    public void EventArgumentsMatchTheNativeOfficerThenCityLayout()
    {
        const ulong data = 0x200001000;
        byte[] args = GameMemory.IndependenceArgs(data, Governor, City);
        Assert.Equal(data + GameMemory.StubArgs + 0x20, BitConverter.ToUInt64(args, 0));
        Assert.Equal(Governor, BitConverter.ToInt32(args, 0x2C));
        Assert.Equal(City, BitConverter.ToInt32(args, 0x30));
        Assert.Equal(0, BitConverter.ToInt32(args, 0x34));
    }
}

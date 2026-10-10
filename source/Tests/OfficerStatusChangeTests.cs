namespace San14Tenkazu.Tests;

public sealed class OfficerStatusChangeTests
{
    private static TestGame Game(int status = 4) => new TestGame()
        .Force(1, 1, 1).Force(2, 2, 2).City(1, 1, status is 5 or 6 ? 0 : 12).City(2, 1).City(3, 2)
        .Officer(1, "조조", 1, 1, 1).Officer(2, "유비", 2, 3, 1)
        .Officer(12, "조인", status is 5 or 6 ? 0 : 1, 1, status, office: 20)
        .Officer(13, "순욱", 1, 1, 4).Policy(1, 2, status is 5 or 6 ? [(13, 10)] : [(12, 0), (13, 10)])
        .Strategist(1, status is 5 or 6 ? 0 : 12);

    [Theory]
    [InlineData(4, (int)OfficerStatusMode.Free, 0, 0, (int)Transfer.Dismiss, 5)]
    [InlineData(6, (int)OfficerStatusMode.Free, 0, 0, (int)Transfer.Release, 5)]
    [InlineData(5, (int)OfficerStatusMode.Regular, 2, 3, (int)Transfer.Recruit, 4)]
    [InlineData(6, (int)OfficerStatusMode.Regular, 2, 3, (int)Transfer.Recruit, 4)]
    [InlineData(4, (int)OfficerStatusMode.Regular, 1, 2, (int)Transfer.Move, 4)]
    [InlineData(4, (int)OfficerStatusMode.Regular, 2, 3, (int)Transfer.Defect, 4)]
    [InlineData(3, (int)OfficerStatusMode.Regular, 1, 1, (int)Transfer.Move, 4)]
    public void StatusChangesUseExistingTransferCleanupAndCanUndoEveryRecord(int status, int mode,
        int force, int home, int transfer, int expectedStatus)
    {
        var data = Game(status).Build();
        var change = OfficerStatusChanges.Plan(data, new(12, (OfficerStatusMode)mode, force, home));
        Assert.False(change.Plan.Refused); Assert.Equal((Transfer)transfer, change.Transfer); Assert.Null(change.Capture);
        var memory = new FakeMemory(data);
        var original = change.Plan.Edits.ToDictionary(e => e.Address, e => memory[e.Address]);
        Assert.Empty(GameMemory.Apply(memory, TestGame.Classes, change.Plan.Edits));
        Assert.Equal(expectedStatus, OfficerFields.StatusField.Read(memory[data.OfficerById[12].Address]!));
        if (mode == (int)OfficerStatusMode.Free || transfer == (int)Transfer.Defect || status == 3)
        {
            Assert.Equal(0, CityFields.Governor.Read(memory[data.Cities[1].Address]!));
        }
        bool leftForce = mode == (int)OfficerStatusMode.Free || transfer == (int)Transfer.Defect;
        Assert.Equal(leftForce || status is 5 or 6 ? 0 : 12, ForceFields.Strategist.Read(memory[data.ForceRecords[1].Address]!));
        Assert.Equal(leftForce || status is 5 or 6 ? 13 : 12, ForceFields.PolicyOfficer(2, 0).Read(memory[data.ForceRecords[1].Address]!));
        var undo = Undo.Compare(Undo.Of(change.Plan.Edits), e => memory[e.Address]);
        Assert.Empty(undo.Moved); Assert.Empty(undo.LinkedMoved);
        Assert.Empty(GameMemory.Apply(memory, TestGame.Classes, undo.Edits));
        foreach (var record in original) Assert.Equal(record.Value, memory[record.Key]);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void PrisonerRequestsLeaveAllWritesToNativeCapture(int status)
    {
        var data = Game(status).Build();
        var change = OfficerStatusChanges.Plan(data, new(12, OfficerStatusMode.Prisoner, 2));
        Assert.False(change.Plan.Refused); Assert.Null(change.Transfer);
        Assert.Equal(new OfficerCaptivity.Request(12, 2), change.Capture);
        Assert.Empty(change.Plan.Edits);
        Assert.Contains("포로", change.Plan.Summary); Assert.Contains(L.StatusChange.UndoNote, change.Plan.Summary);
    }

    [Theory]
    [InlineData(1)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public void NonVassalStatusesCannotBeCaptured(int status)
    {
        var change = OfficerStatusChanges.Plan(Game(status).Build(), new(12, OfficerStatusMode.Prisoner, 2));
        Assert.True(change.Plan.Refused); Assert.Empty(change.Plan.Edits);
    }

    [Fact]
    public void CaptureRejectsOwnForceActiveCommandersAndOfficersAwayFromTheirBase()
    {
        Assert.True(OfficerCaptivity.Plan(Game().Build(), new(12, 1)).Refused);
        Assert.Contains(L.Transfers.OnCampaign, OfficerCaptivity.Plan(Game().Leading(12).Build(), new(12, 2)).Refusal);
        Assert.Contains(L.Transfers.OutsideBase, OfficerCaptivity.Plan(Game().With(12, OfficerFields.PlaceField, 2).Build(), new(12, 2)).Refusal);
        var generated = Game().Build(); generated.OfficerById[12].Data[0x15F] |= 0x40;
        Assert.Equal(L.StatusChange.Generated, OfficerCaptivity.Plan(generated, new(12, 2)).Refusal);
    }

    [Fact]
    public void InvalidDestinationsAndNoChangeAreRejected()
    {
        var data = Game().Build();
        Assert.True(OfficerStatusChanges.Plan(data, new(12, OfficerStatusMode.Regular, 2, 1)).Plan.Refused);
        Assert.True(OfficerStatusChanges.Plan(data, new(12, (OfficerStatusMode)99)).Plan.Refused);
        Assert.True(OfficerStatusChanges.Plan(data, new(99, OfficerStatusMode.Free)).Plan.Refused);
        Assert.True(OfficerStatusChanges.Plan(data, new(12, OfficerStatusMode.Prisoner, 3)).Plan.Refused);
        Assert.Equal(L.StatusChange.Unchanged, OfficerStatusChanges.Plan(data, new(12, OfficerStatusMode.Regular, 1, 1)).Plan.Refusal);
        Assert.Equal(L.StatusChange.Unchanged, OfficerStatusChanges.Plan(Game(5).Build(), new(12, OfficerStatusMode.Free)).Plan.Refusal);
    }

    [Fact]
    public void CaptorGateOwnershipUsesItsCountyOwnerRatherThanItsStaleCorps()
    {
        var data = Game().Gate(52, 2).Build();
        OfficerFields.BaseField.Encode(52).CopyTo(data.OfficerById[2].Data, OfficerFields.BaseField.Offset);
        var gate = data.Gates.Single();
        // A captured gate can retain its previous corps while its county belongs to the new owner.
        BitConverter.GetBytes((ushort)1).CopyTo(gate.Data, CityFields.CorpsOffset);
        Assert.Equal(2, OfficerCaptivity.BaseForce(data, 52));
        Assert.Equal(52, OfficerCaptivity.Targets(data).Single(t => t.Force == 2).Base);
    }

    [Fact]
    public void GatePreflightGuardsItsActualCountyOwnerAndRejectsChangedOwnership()
    {
        var data = Game().Gate(52, 2).MilitaryPoints(1, 1, 1, 1, 0).MilitaryPoints(2, 2, 1, 2, 0).Build();
        var ruler = data.OfficerById[2];
        OfficerFields.BaseField.Encode(52).CopyTo(ruler.Data, OfficerFields.BaseField.Offset);
        var gate = data.Gates.Single(); var building = gate.Building!; var county = data.Counties.Single();
        BitConverter.GetBytes((ushort)building.Index).CopyTo(gate.Data, 0x4E);
        BitConverter.GetBytes((ushort)1).CopyTo(gate.Data, CityFields.CorpsOffset);
        const ulong exe = 0x40000000, raw = 0x20000000, state = 0x30000000, tileAddress = 0x50000000, centerAddress = 0x50001000;
        var memory = new BufferMemory();
        memory.Set(exe + 0x277DF18, BitConverter.GetBytes(raw)); memory.Set(raw + 0x871E8, BitConverter.GetBytes(state));
        memory.Set(raw + 0x73A50, new byte[8]);
        memory.Set(exe + GameMemory.OfficerCaptureRva, Convert.FromHexString("44894C24205355564881ECA0000000418BD8488BEA488BF1E8F3E7040085C075"));
        byte[] clock = new byte[0x18]; BitConverter.GetBytes((ushort)203).CopyTo(clock, 0x10); clock[0x12] = 3; clock[0x13] = 2;
        memory.Set(state + 0x24, clock);
        foreach (var officer in data.Officers)
        {
            memory.Set(raw + 0x158 + (ulong)officer.Id * 8, BitConverter.GetBytes(officer.Address)); memory.Set(officer.Address, officer.Data);
        }
        foreach (var force in data.ForceRecords.Values)
        {
            memory.Set(raw + 0xDF30 + (ulong)force.Id * 8, BitConverter.GetBytes(force.Address)); memory.Set(force.Address, force.Data);
        }
        foreach (var corps in data.CorpsRecords.Values)
        {
            memory.Set(raw + 0xE0D0 + (ulong)corps.Id * 8, BitConverter.GetBytes(corps.Address)); memory.Set(corps.Address, corps.Data);
        }
        memory.Set(raw + 0xDED8 + 8, BitConverter.GetBytes(gate.Address)); memory.Set(gate.Address, gate.Data);
        memory.Set(raw + 0x6DA98 + (ulong)building.Index * 8, BitConverter.GetBytes(building.Address)); memory.Set(building.Address, building.Data);
        byte[] tile = new byte[0x20]; BitConverter.GetBytes((ushort)county.Index).CopyTo(tile, 0x12);
        memory.Set(raw + 0xE270 + (ulong)building.Tile * 8, BitConverter.GetBytes(tileAddress)); memory.Set(tileAddress, tile);
        memory.Set(raw + 0x6CAF0 + (ulong)county.Index * 8, BitConverter.GetBytes(county.Address)); memory.Set(county.Address, county.Data);
        byte[] center = new byte[0x20]; center[0x14] = 2;
        memory.Set(raw + 0xE270 + (ulong)county.CenterTile * 8, BitConverter.GetBytes(centerAddress)); memory.Set(centerAddress, center);
        var identity = new GameIdentity(0, DateTime.UnixEpoch, exe, "fixture", true, TestGame.Classes);
        var layout = new GameLayout(new(0, 0, 0, raw + 0xE270, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            0, 0, 0, 0, 0, 0, 0, 0, [], [], 0, 0, state);
        var prepared = GameMemory.PrepareCaptivity(memory, identity, layout, data, new(12, 2));
        Assert.Equal(52, prepared.Target.Base); Assert.Equal(1, prepared.Place);
        Assert.Contains(prepared.Guards, g => g.Address == centerAddress && g.Expected[0x14] == 2);
        center[0x14] = 1; memory.Set(centerAddress, center);
        Assert.Equal(L.Transfers.DestinationChanged,
            Assert.Throws<InvalidOperationException>(() => GameMemory.PrepareCaptivity(memory, identity, layout, data, new(12, 2))).Message);
    }

    private sealed class BufferMemory : IRecordMemory
    {
        private readonly Dictionary<ulong, byte[]> records = [];
        internal void Set(ulong address, byte[] value) => records[address] = (byte[])value.Clone();
        public byte[] Read(ulong address, int length)
        {
            var record = records.First(r => r.Key <= address && address + (ulong)length <= r.Key + (ulong)r.Value.Length);
            return record.Value.AsSpan((int)(address - record.Key), length).ToArray();
        }
        public void Write(ulong address, byte[] bytes) => throw new InvalidOperationException("Preflight must not write");
    }
}

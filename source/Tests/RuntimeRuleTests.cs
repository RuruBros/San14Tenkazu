namespace San14Tenkazu.Tests;

public sealed class RuntimeRuleTests
{
    [Fact]
    public void ReceiptsRoundTripAndNeverAdoptAnotherProcessOrIncompleteChange()
    {
        string path = Path.Combine(Path.GetTempPath(), "tenkazu-rules-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var game = new GameIdentity(123, new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc), 0x140000000, "fixture", true, TestGame.Classes);
            var receipt = new RuntimeRuleReceipt(game.ProcessId, game.StartTimeUtc, game.ModuleBase, game.ExeHash,
                RuntimeRuleSettings.Original, new(true, AbilityRuleMode.Growth200), "prepared", [new(100, [1, 2], [3, 4], true)]);
            var file = new RuntimeRuleJournal(path); Assert.Null(file.Read()); file.Save(receipt);
            var read = file.Read()!; Assert.Equal(receipt.Writes[0].Before, read.Writes[0].Before);
            Assert.Throws<InvalidOperationException>(() => read.ExpectedFor(game));
            file.Save(receipt with { Phase = "committed" }); read = file.Read()!;
            Assert.Equal(receipt.After, read.ExpectedFor(game));
            Assert.Equal(RuntimeRuleSettings.Original, read.ExpectedFor(game with { ProcessId = 124 }));
            Assert.Equal(RuntimeRuleSettings.Original, read.ExpectedFor(game with { StartTimeUtc = game.StartTimeUtc.AddSeconds(1) }));
            Assert.Equal(RuntimeRuleSettings.Original, read.ExpectedFor(game with { ModuleBase = game.ModuleBase + 4096 }));
            Assert.Equal(RuntimeRuleSettings.Original, read.ExpectedFor(game with { ExeHash = "other" }));
            file.Save(receipt with { Phase = "rolled-back" }); Assert.Equal(receipt.Before, file.Read()!.ExpectedFor(game));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public void NineSlotsAffectTerrainImmunityAndAbilityEditsUseNewCurve()
    {
        var data = new TestGame().Officer(3002, "Officer", 1, 1, 4).Leading(3002)
            .Personality(77, "Detox", "", (52, 0)).Build(new(true, AbilityRuleMode.Growth200));
        var officer = data.Officers.Single();
        var personality = data.Personalities.Single(); personality.Data[0xBF] = 16;
        OfficerFields.Personalities[8].Encode(77).CopyTo(officer.Data, OfficerFields.Personalities[8].Offset);
        Assert.True(UnitConditions.HasPersonality(data, officer, 77));
        var plan = OfficerEdits.Plan(data, officer.Id, new([new(OfficerFields.Leadership, 150)], null, null, []));
        var changed = plan.Edits.Single();
        Assert.Equal(data.RuntimeRules, changed.ExpectedRuntimeRules);
        var experience = changed.Changes.Single(c => OfficerFields.IsExperience(c.Field));
        Assert.Equal(AbilityProgress.Threshold(150, false, true), experience.Value);
    }
    private sealed class Memory : IRuntimeRuleMemory
    {
        internal readonly Dictionary<ulong, byte> Bytes = [];
        internal int Writes;
        internal int FailWrite = -1;
        internal bool FailRollback;
        public byte[] Read(ulong address, int length) => Enumerable.Range(0, length).Select(i => Bytes[address + (uint)i]).ToArray();
        internal void Seed(ulong address, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) Bytes[address + (uint)i] = bytes[i]; }
        public void Write(RuntimeRuleWrite change, byte[] bytes)
        {
            Writes++;
            if (Writes == FailWrite || FailRollback && Writes > FailWrite)
            {
                Bytes[change.Address] = bytes[0]; // Failed writes can already have changed a prefix.
                throw new IOException("injected failure");
            }
            Seed(change.Address, bytes);
        }
    }

    private static Memory Fixture(RuntimeRuleSettings state)
    {
        var memory = new Memory();
        foreach (var patch in RuntimeRulePatches.All) memory.Seed(patch.GuardRva, Convert.FromHexString(patch.Guard));
        foreach (var patch in RuntimeRulePatches.All) memory.Seed(patch.Rva, patch.Bytes(state));
        return memory;
    }

    public static IEnumerable<object[]> Modes() => from trait in new[] { false, true }
        from mode in Enum.GetValues<AbilityRuleMode>() select new object[] { trait, (int)mode };

    [Theory, MemberData(nameof(Modes))]
    public void AllModesDetectAndTransition(bool traits, int ability)
    {
        var before = new RuntimeRuleSettings(traits, (AbilityRuleMode)ability);
        foreach (var row in Modes())
        {
            var after = new RuntimeRuleSettings((bool)row[0], (AbilityRuleMode)(int)row[1]);
            var memory = Fixture(before);
            Assert.Equal(before, RuntimeRulePatches.Detect(memory.Read, 0));
            var writes = RuntimeRulePatches.All.Select(p => new RuntimeRuleWrite(p.Rva, p.Bytes(before), p.Bytes(after), true))
                .Where(p => !p.Before.SequenceEqual(p.After)).ToArray();
            bool prepared = false, committed = false;
            RuntimeRuleTransaction.Apply(memory, writes, () => prepared = true, () =>
            {
                Assert.True(prepared); RuntimeRulePatches.Verify(memory.Read, 0, after); committed = true;
            });
            Assert.True(committed);
            Assert.Equal(after, RuntimeRulePatches.Detect(memory.Read, 0));
        }
    }

    [Fact]
    public void UnknownGuardNeverWrites()
    {
        var memory = Fixture(RuntimeRuleSettings.Original);
        memory.Bytes[RuntimeRulePatches.All[0].GuardRva] ^= 1;
        Assert.Null(RuntimeRulePatches.Detect(memory.Read, 0));
        Assert.Throws<InvalidDataException>(() => RuntimeRulePatches.Verify(memory.Read, 0, RuntimeRuleSettings.Original));
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public void EveryPartialWriteFailureRestoresAllAttemptedBytes()
    {
        var before = RuntimeRuleSettings.Original; var after = new RuntimeRuleSettings(true, AbilityRuleMode.Growth200);
        var writes = RuntimeRulePatches.All.Select(p => new RuntimeRuleWrite(p.Rva, p.Bytes(before), p.Bytes(after), true)).ToArray();
        for (int failure = 1; failure <= writes.Length; failure++)
        {
            var memory = Fixture(before); memory.FailWrite = failure;
            Assert.Throws<IOException>(() => RuntimeRuleTransaction.Apply(memory, writes, () => { }, () => { }));
            RuntimeRulePatches.Verify(memory.Read, 0, before);
        }
    }

    [Fact]
    public void JournalFailureBeforeWriteAndAfterWriteIsRecoverable()
    {
        var memory = Fixture(RuntimeRuleSettings.Original);
        var site = RuntimeRulePatches.All.First(p => p.Traits != p.Before);
        var change = new RuntimeRuleWrite(site.Rva, Convert.FromHexString(site.Before), Convert.FromHexString(site.Traits), true);
        Assert.Throws<IOException>(() => RuntimeRuleTransaction.Apply(memory, [change], () => throw new IOException(), () => { }));
        Assert.Equal(0, memory.Writes);
        Assert.Throws<IOException>(() => RuntimeRuleTransaction.Apply(memory, [change], () => { }, () => throw new IOException()));
        Assert.Equal(change.Before, memory.Read(change.Address, change.Before.Length));
    }

    [Fact]
    public void IncompleteRecoveryIsNotReportedAsRestored()
    {
        var memory = Fixture(RuntimeRuleSettings.Original); memory.FailWrite = 1; memory.FailRollback = true;
        var site = RuntimeRulePatches.All.First(p => p.Traits != p.Before);
        var change = new RuntimeRuleWrite(site.Rva, Convert.FromHexString(site.Before), Convert.FromHexString(site.Traits), true);
        Assert.Throws<RuntimeRuleRecoveryException>(() => RuntimeRuleTransaction.Apply(memory, [change], () => { }, () => { }));
    }

    [Fact]
    public void OwnCapDoesNotMutateEncodedCapsOrSealRule()
    {
        Assert.DoesNotContain(RuntimeRulePatches.All, p => new uint[] { 0x2253FA, 0x22544F, 0x2293F6, 0x229469, 0x23AAA8, 0x23AB06, 0x230098 }.Contains(p.Rva));
        Assert.Equal(2, RuntimeRulePatches.All.Count(p => p.Rva is 0x230126 or 0x23016A));
        var normalization = Assert.Single(RuntimeRulePatches.All, p => p.Rva == 0x22591C);
        Assert.Equal("ff5048", normalization.Before);
        Assert.Equal("b0c890", normalization.Cap);
        Assert.Equal(normalization.Cap, normalization.Growth);
        Assert.Equal(0x225380U, normalization.CodeStart);
        Assert.Equal(0x225F30U, normalization.CodeEnd);
        foreach (var p in RuntimeRulePatches.All)
        {
            Assert.Equal(p.Before.Length, p.Cap.Length); Assert.Equal(p.Before.Length, p.Growth.Length); Assert.Equal(p.Before.Length, p.Traits.Length);
            Assert.True(p.CodeStart <= p.Rva && p.CodeEnd >= p.Rva + p.Before.Length / 2);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ConversionKeepsLevelAndProgress(bool charm)
    {
        for (int level = 100; level < 200; level++)
            foreach (int percent in new[] { 0, 1, 50, 99 })
            {
                long start = AbilityProgress.Threshold(level, charm, false), end = AbilityProgress.Threshold(level + 1, charm, false);
                int old = checked((int)(start + (end - start) * percent / 100));
                int updated = AbilityProgress.ConvertExperience(level, old, charm, false, true);
                long target = AbilityProgress.Threshold(level, charm, true), next = AbilityProgress.Threshold(level + 1, charm, true);
                Assert.InRange(updated, target, next - 1);
                Assert.InRange(Math.Abs((updated - target) / (double)(next - target) - (old - start) / (double)(end - start)), 0, 0.0002);
                Assert.Equal(updated, AbilityProgress.ConvertExperience(level, updated, charm, true, true));
                int restored = AbilityProgress.ConvertExperience(level, updated, charm, true, false);
                Assert.InRange(restored, start, end - 1);
                Assert.InRange(Math.Abs((restored - start) / (double)(end - start) - (old - start) / (double)(end - start)), 0, 0.0002);
            }
        Assert.Throws<InvalidDataException>(() => AbilityProgress.ConvertExperience(150, -1, charm, false, true));
        int atMaximum = checked((int)AbilityProgress.Threshold(200, charm, false) + 10000);
        int convertedMaximum = AbilityProgress.ConvertExperience(200, atMaximum, charm, false, true);
        Assert.Equal(AbilityProgress.Threshold(200, charm, true) + 10000, convertedMaximum);
        Assert.Equal(atMaximum, AbilityProgress.ConvertExperience(200, convertedMaximum, charm, true, false));
    }

    [Fact]
    public void ThresholdAndEditorMatchVerifiedNativeCurve()
    {
        Assert.Equal(1683165, AbilityProgress.Threshold(101, false, true));
        Assert.Equal(6600000, AbilityProgress.Threshold(200, false, true));
        var record = new byte[GameMemory.OfficerStride];
        foreach (var field in OfficerFields.Abilities)
        {
            var change = OfficerFields.ExperienceFor(record, field, 199, true);
            field.Encode(199).CopyTo(record, field.Offset);
            change.Field.Encode(change.Value).CopyTo(record, change.Field.Offset);
            Assert.True(OfficerFields.FitsExperience(record, field, true));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => OfficerFields.ExperienceFor(record, OfficerFields.Leadership, 201, true));
    }
}

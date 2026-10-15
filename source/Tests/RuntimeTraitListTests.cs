namespace San14Tenkazu.Tests;

public sealed class RuntimeTraitListTests
{
    private const ulong Module = 0x140000000, Allocation = 0x20000000000;
    [Fact]
    public void RealDiagnosticSnapshotMatchesAfterRelocationAndStillRejectsChangedCallbacks()
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "TraitListDiagnostic.json")));
        var root = document.RootElement;
        ulong module = Convert.ToUInt64(root.GetProperty("module").GetString(), 16);
        byte[] actual = Convert.FromHexString(root.GetProperty("columns").GetString()!);
        Assert.Equal(actual, RuntimeTraitList.Rebase(RuntimeTraitList.Definition.Columns, module));
        byte[] allocation = RuntimeTraitList.Build(module, Allocation, actual);
        Assert.Equal(actual, allocation.AsSpan(RuntimeTraitList.ColumnsOffset, actual.Length).ToArray());
        foreach (int column in new[] { 22, 69, 99, 100 })
        {
            byte[] changed = (byte[])actual.Clone(); changed[column * 96 + 0x18] ^= 1;
            Assert.Throws<InvalidDataException>(() => RuntimeTraitList.Build(module, Allocation, changed));
        }
    }
    [Fact]
    public void EveryCapturedPointerNeedsExplicitRelocationIncludingRuntimeInitializedFields()
    {
        var def = RuntimeTraitList.Definition;
        RuntimeTraitList.ValidateDefinition(def);
        foreach (var block in new[] { def.Columns }.Concat(def.Tables).Concat(def.Guards))
        {
            foreach (int offset in block.Fixups)
            {
                var broken = block with { Fixups = block.Fixups.Where(i => i != offset).ToArray() };
                var altered = def with { Columns = ReferenceEquals(block, def.Columns) ? broken : def.Columns,
                    Tables = def.Tables.Select(t => ReferenceEquals(t, block) ? broken : t).ToArray(),
                    Guards = def.Guards.Select(g => ReferenceEquals(g, block) ? broken : g).ToArray() };
                Assert.Throws<InvalidDataException>(() => RuntimeTraitList.ValidateDefinition(altered));
            }
        }
    }
    [Fact]
    public void NativeCacheWritesArePreservedButAdjacentDefinitionsAreGuarded()
    {
        var state = new RuntimeRuleSettings(true, AbilityRuleMode.Growth200, true);
        var memory = Create(state);
        foreach (int column in RuntimeTraitList.MutableColumns)
        {
            uint offset = (uint)(column * 96 + 0x5C);
            memory.Seed(Module + RuntimeTraitList.Definition.Columns.Rva + offset, BitConverter.GetBytes(column));
            memory.Seed(Allocation + RuntimeTraitList.ColumnsOffset + offset, BitConverter.GetBytes(column + 1));
        }
        RuntimeRulePatches.VerifyFull(memory.Read, Module, state);
        byte[] columns = memory.Read(Module + RuntimeTraitList.Definition.Columns.Rva, RuntimeTraitList.Definition.Columns.Bytes.Length / 2);
        byte[] payload = RuntimeTraitList.Build(Module, Allocation, columns);
        foreach (int column in RuntimeTraitList.MutableColumns)
            Assert.Equal(column, BitConverter.ToInt32(payload, RuntimeTraitList.ColumnsOffset + column * 96 + 0x5C));
        foreach (int column in RuntimeTraitList.MutableColumns)
        {
            var changed = Create(state);
            changed.Data[Module + RuntimeTraitList.Definition.Columns.Rva + (uint)(column * 96 + 0x5B)] ^= 1;
            Assert.Throws<InvalidDataException>(() => RuntimeRulePatches.VerifyFull(changed.Read, Module, state));
            changed = Create(state);
            changed.Data[Allocation + RuntimeTraitList.ColumnsOffset + (uint)(column * 96 + 0x40)] ^= 1;
            Assert.Throws<InvalidDataException>(() => RuntimeRulePatches.VerifyFull(changed.Read, Module, state));
        }
        Assert.Equal(new[] { 0x3000, 0x4000 }, RuntimeTraitList.WritablePages);
    }
    private sealed class Memory : IRuntimeRuleMemory
    {
        internal readonly Dictionary<ulong, byte> Data = [];
        internal int Fail = -1, Count;
        internal void Seed(ulong at, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) Data[at + (uint)i] = bytes[i]; }
        public byte[] Read(ulong at, int size) => Enumerable.Range(0, size).Select(i => Data[at + (uint)i]).ToArray();
        public void Write(RuntimeRuleWrite change, byte[] bytes)
        {
            if (++Count == Fail) { Data[change.Address] = bytes[0]; throw new IOException("partial write"); }
            Seed(change.Address, bytes);
        }
    }
    private static Memory Create(RuntimeRuleSettings settings)
    {
        var m = new Memory();
        var def = RuntimeTraitList.Definition;
        foreach (var block in new[] { def.Columns }.Concat(def.Tables).Concat(def.Guards))
            m.Seed(Module + block.Rva, RuntimeTraitList.Rebase(block, Module));
        foreach (var p in RuntimeRulePatches.All) m.Seed(Module + p.GuardRva, Convert.FromHexString(p.Guard));
        foreach (var p in RuntimeRulePatches.All) m.Seed(Module + p.Rva, p.Bytes(settings));
        m.Seed(Allocation, RuntimeTraitList.Build(Module, Allocation));
        m.Seed(Module + RuntimeTraitList.Site, settings.TraitList9 ? RuntimeTraitList.Jump(Allocation) : RuntimeTraitList.Original);
        return m;
    }
    private static IEnumerable<RuntimeRuleSettings> Modes()
    {
        foreach (var ability in Enum.GetValues<AbilityRuleMode>())
        {
            yield return new(false, ability);
            yield return new(true, ability);
            yield return new(true, ability, true);
        }
    }
    [Fact]
    public void AllNineModesTransitionAndRetainOldScreenStorage()
    {
        foreach (var before in Modes()) foreach (var after in Modes())
        {
            var m = Create(before);
            Assert.Equal(before, RuntimeRulePatches.DetectFull(m.Read, Module));
            var writes = RuntimeRulePatches.All.Select(p => new RuntimeRuleWrite(Module + p.Rva, p.Bytes(before), p.Bytes(after), true))
                .Where(w => !w.Before.SequenceEqual(w.After)).ToList();
            if (before.TraitList9 != after.TraitList9) writes.Add(new(Module + RuntimeTraitList.Site,
                before.TraitList9 ? RuntimeTraitList.Jump(Allocation) : RuntimeTraitList.Original,
                after.TraitList9 ? RuntimeTraitList.Jump(Allocation) : RuntimeTraitList.Original, true));
            RuntimeRuleTransaction.Apply(m, writes, () => { }, () => RuntimeRulePatches.VerifyFull(m.Read, Module, after));
            Assert.Equal(after, RuntimeRulePatches.DetectFull(m.Read, Module));
            Assert.Equal(RuntimeTraitList.Build(Module, Allocation), m.Read(Allocation, RuntimeTraitList.Size));
        }
    }
    [Fact]
    public void FailureAtEveryWriteIncludingHookRestoresEffectsAndList()
    {
        foreach (bool enabling in new[] { true, false })
        {
            var before = enabling ? RuntimeRuleSettings.Original : new(true, AbilityRuleMode.Growth200, true);
            var after = enabling ? new(true, AbilityRuleMode.Growth200, true) : RuntimeRuleSettings.Original;
            var writes = RuntimeRulePatches.All.Select(p => new RuntimeRuleWrite(Module + p.Rva, p.Bytes(before), p.Bytes(after), true))
                .Where(w => !w.Before.SequenceEqual(w.After)).Append(new(Module + RuntimeTraitList.Site,
                    enabling ? RuntimeTraitList.Original : RuntimeTraitList.Jump(Allocation),
                    enabling ? RuntimeTraitList.Jump(Allocation) : RuntimeTraitList.Original, true)).ToArray();
            for (int failure = 1; failure <= writes.Length; failure++)
            {
                var m = Create(before); m.Fail = failure;
                Assert.Throws<IOException>(() => RuntimeRuleTransaction.Apply(m, writes, () => { }, () => { }));
                RuntimeRulePatches.VerifyFull(m.Read, Module, before);
                RuntimeTraitList.VerifyAllocation(m.Read, Module, Allocation);
            }
            var commit = Create(before);
            Assert.Throws<IOException>(() => RuntimeRuleTransaction.Apply(commit, writes, () => { }, () => throw new IOException("commit")));
            RuntimeRulePatches.VerifyFull(commit.Read, Module, before);
        }
    }
    [Fact]
    public void ConflictingTablesCodeAndCopiedCallbacksAreRejected()
    {
        var state = new RuntimeRuleSettings(true, AbilityRuleMode.Original, true);
        foreach (ulong at in new[] { Module + RuntimeTraitList.Site, Allocation + 10, Allocation + RuntimeTraitList.ColumnsOffset + 213 * 96 + 0x18,
            Module + RuntimeTraitList.Definition.Columns.Rva + 0x48, Module + RuntimeTraitList.Definition.Tables[1].Rva + 16 })
        {
            var m = Create(state); m.Data[at] ^= 1;
            Assert.Null(RuntimeRulePatches.DetectFull(m.Read, Module));
            Assert.Throws<InvalidDataException>(() => RuntimeRulePatches.VerifyFull(m.Read, Module, state));
            Assert.Equal(0, m.Count);
        }
        Assert.Throws<InvalidOperationException>(() => new RuntimeRuleSettings(false, AbilityRuleMode.Original, true).Validate());
    }
    [Fact]
    public void OldReceiptsDefaultToNoListAndNewAllocationSurvivesOffReceipt()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<RuntimeRuleSettings>("{\"Traits9\":true,\"Abilities\":0}");
        Assert.False(old!.TraitList9);
        var receipt = new RuntimeRuleReceipt(1, DateTime.UnixEpoch, Module, "fixture", new(true, AbilityRuleMode.Original, true),
            RuntimeRuleSettings.Original, "committed", [], Allocation);
        var read = System.Text.Json.JsonSerializer.Deserialize<RuntimeRuleReceipt>(System.Text.Json.JsonSerializer.Serialize(receipt));
        Assert.Equal(Allocation, read!.TraitListAllocation); Assert.False(read.After.TraitList9);
    }
}

using System.Text.Json;

namespace San14Tenkazu;

// Copies the verified game's column/page definitions. Only new list instances receive the copies;
// existing screens keep valid pointers until closed. Exposed storage lives until process exit.
internal static class RuntimeTraitList
{
    internal const uint Site = GameBuildFacts.Runtime.Site, CodeStart = GameBuildFacts.Runtime.CodeStart, CodeEnd = GameBuildFacts.Runtime.CodeEnd;
    internal const int Size = 0x8000, CodeSize = 0x1000, ColumnsOffset = CodeSize, RecordSize = 0x60;
    internal static readonly byte[] Original = Convert.FromHexString("488B4577488986500100004C8B6D7F");
    internal sealed record Block(uint Rva, string Bytes, int[] Fixups, int TraitIndex = -1, int TraitBase = 61);
    internal sealed record Layout(ulong OriginalBase, uint ImageSize, Block Columns, Block[] Tables, Block[] Guards);
    internal static readonly Layout Definition = Load();
    // The native list constructor (2BF138) stores callback results here. Everything else
    // remains a definition, including the callbacks, names, offsets and table pointers.
    internal static readonly int[] MutableColumns = [107, 126, 127, 128, 129];
    internal static int[] WritablePages => MutableColumns.Select(c => (ColumnsOffset + c * RecordSize + 0x5C) & ~4095).Distinct().ToArray();
    private static bool Matches(byte[] actual, byte[] expected, int columnsOffset)
    {
        if (actual.Length != expected.Length) return false;
        if (columnsOffset >= 0)
            foreach (int column in MutableColumns)
                actual.AsSpan(columnsOffset + column * RecordSize + 0x5C, 4)
                    .CopyTo(expected.AsSpan(columnsOffset + column * RecordSize + 0x5C, 4));
        return actual.AsSpan().SequenceEqual(expected);
    }
    private static Layout Load()
    {
        var definition = JsonSerializer.Deserialize<Layout>(GameBuildProfiles.Current.ReadResource("RuntimeRules.trait-list.json"))!;
        ValidateDefinition(definition);
        return definition;
    }
    internal static void ValidateDefinition(Layout definition)
    {
        if (definition.OriginalBase == 0 || definition.ImageSize is 0 or > 0x40000000
            || definition.OriginalBase > ulong.MaxValue - definition.ImageSize)
            throw new InvalidDataException(L.RuntimeErrors.TraitImageRangeInvalid);
        foreach (var block in new[] { definition.Columns }.Concat(definition.Tables).Concat(definition.Guards))
        {
            byte[] bytes = Convert.FromHexString(block.Bytes);
            var fixups = block.Fixups.ToHashSet();
            if ((ulong)block.Rva + (uint)bytes.Length > definition.ImageSize || fixups.Count != block.Fixups.Length)
                throw new InvalidDataException(L.RuntimeErrors.TraitRelocationInvalid);
            bool ImagePointer(ulong value) => value >= definition.OriginalBase && value - definition.OriginalBase < definition.ImageSize;
            foreach (int at in fixups)
                if (at < 0 || at > bytes.Length - 8 || !ImagePointer(BitConverter.ToUInt64(bytes, at)))
                    throw new InvalidDataException(L.RuntimeErrors.TraitPointerInvalid);
            // Inspect unaligned immediates too. A captured absolute pointer must never
            // silently survive because the PE lacks a relocation for a CRT-filled field.
            for (int at = 0; at + 8 <= bytes.Length; at++)
                if (ImagePointer(BitConverter.ToUInt64(bytes, at)) && !fixups.Contains(at))
                    throw new InvalidDataException(L.RuntimeErrors.TraitRelocationMissing($"{block.Rva + at:X}"));
        }
    }
    internal static byte[] Rebase(Block block, ulong module)
    {
        byte[] bytes = Convert.FromHexString(block.Bytes);
        foreach (int at in block.Fixups)
            BitConverter.TryWriteBytes(bytes.AsSpan(at), checked(BitConverter.ToUInt64(bytes, at) - Definition.OriginalBase + module));
        return bytes;
    }
    internal static byte[] Jump(ulong address) => [0xFF, 0x25, 0, 0, 0, 0, .. BitConverter.GetBytes(address), 0x90];
    internal static void VerifyOriginalTables(Func<ulong, int, byte[]> read, ulong module, ulong active)
    {
        foreach (var block in new[] { Definition.Columns }.Concat(Definition.Tables).Concat(Definition.Guards))
        {
            byte[] expected = Rebase(block, module);
            if (active != 0 && block.Rva <= Site && Site < block.Rva + expected.Length)
                Jump(active).CopyTo(expected, (int)(Site - block.Rva));
            if (!Matches(read(module + block.Rva, expected.Length), expected, block.Rva == Definition.Columns.Rva ? 0 : -1))
                throw new InvalidDataException(L.RuntimeErrors.TraitOriginalConflict($"{block.Rva:X}"));
        }
    }
    internal static ulong Detect(Func<ulong, int, byte[]> read, ulong module)
    {
        byte[] site = read(module + Site, Original.Length);
        if (site.SequenceEqual(Original)) return 0;
        ulong address = BitConverter.ToUInt64(site, 6);
        if (address == 0 || (address & 4095) != 0 || !site.SequenceEqual(Jump(address)))
            throw new InvalidDataException(L.RuntimeErrors.TraitCodeChanged);
        VerifyAllocation(read, module, address);
        VerifyOriginalTables(read, module, address);
        return address;
    }
    internal static void VerifyAllocation(Func<ulong, int, byte[]> read, ulong module, ulong address)
    {
        if (address == 0 || (address & 4095) != 0 || address > 0x00007FFFFFFF0000UL - Size
            || !Matches(read(address, Size), Build(module, address), ColumnsOffset))
            throw new InvalidDataException(L.RuntimeErrors.TraitAllocationChanged);
    }
    internal static byte[] Build(ulong module, ulong address, byte[]? currentColumns = null)
    {
        byte[] result = new byte[Size];
        Rebase(Definition.Columns, module).CopyTo(result, ColumnsOffset);
        if (currentColumns is not null)
        {
            if (!Matches(currentColumns, Rebase(Definition.Columns, module), 0))
                throw new InvalidDataException(L.RuntimeErrors.TraitColumnsChanged);
            foreach (int column in MutableColumns)
                currentColumns.AsSpan(column * RecordSize + 0x5C, 4).CopyTo(result.AsSpan(ColumnsOffset + column * RecordSize + 0x5C, 4));
        }
        int cursor = ColumnsOffset + 221 * RecordSize;
        var tables = new List<(uint Rva, int Offset)>();
        foreach (var table in Definition.Tables)
        {
            byte[] bytes = Rebase(table, module);
            tables.Add((table.Rva, cursor)); bytes.CopyTo(result, cursor); cursor += (bytes.Length + 7) & ~7;
        }
        int list = cursor;
        for (int style = 0; style < 2; style++)
        {
            int first = style == 0 ? 61 : 170, added = 213 + style * 4;
            int[] ids = [0, first, first + 1, first + 2, first + 3, first + 4, added, added + 1, added + 2, added + 3, -1];
            for (int i = 0; i < ids.Length; i++) BitConverter.TryWriteBytes(result.AsSpan(list + style * 44 + i * 4), ids[i]);
        }
        for (int i = 0; i < tables.Count; i++)
            BitConverter.TryWriteBytes(result.AsSpan(tables[i].Offset + Definition.Tables[i].TraitIndex * 24),
                address + (uint)(list + (Definition.Tables[i].TraitBase == 170 ? 44 : 0)));

        var c = new Code();
        // Preserve flags and scratch registers: the overwritten MOVs changed neither.
        c.Emit("9C514153488B45774C8B6D7F");
        c.Imm64("49BB", module + Definition.Columns.Rva); c.Emit("4D39DD"); int foreign = c.Branch("0F85");
        var done = new List<int>();
        foreach (var table in tables)
        {
            c.Imm64("49BB", module + table.Rva); c.Emit("4C39D8"); int next = c.Branch("0F85");
            c.Imm64("48B8", address + (uint)table.Offset);
            c.Imm64("49BD", address + ColumnsOffset); done.Add(c.Branch("E9")); c.Target(next);
        }
        c.Target(foreign); foreach (int jump in done) c.Target(jump);
        c.Emit("48898650010000415B599D"); c.Bytes.AddRange(Jump(module + Site + (uint)Original.Length));
        for (int style = 0; style < 2; style++) for (int slot = 5; slot < 9; slot++)
        {
            c.Align(); int value = c.Count;
            if (style == 1)
            {
                // These variants prefer the base officer's traits when a matching base record exists.
                c.Imm32("BA", slot); c.Bytes.AddRange(Jump(module + 0x35F020));
            }
            else
            {
            c.Emit("534883EC20"); c.Imm64("48BB", module + GameBuildFacts.Memory.BasePointerRva); c.Emit("488B1B");
            c.Imm32("BA", slot); c.Call(module + 0x2305D0);
            c.Emit("3D63010000"); int invalid = c.Branch("0F8F");
            c.Emit("4898488B9CC318DD0700"); int valid = c.Branch("E9"); c.Target(invalid);
            c.Emit("488B9B18DD0700"); c.Target(valid); c.Emit("488BCB"); c.Call(module + 0x32F500);
            c.Emit("85C0488D4310"); int named = c.Branch("0F85");
            c.Imm64("48B8", module + 0x1A47B98); c.Emit("488B00"); c.Target(named); c.Emit("4883C4205BC3");
            }
            int[] functions = [value, 0, 0];
            for (int order = 1; order >= 0; order--)
            {
                c.Align(); functions[2 - order] = c.Count;
                c.Emit("4883EC38"); c.Imm32("41B9", style); c.Imm32("C7442420", order); c.Imm32("41B8", slot);
                c.Call(module + 0x353810); c.Emit("4883C438C3");
            }
            int column = 213 + style * 4 + slot - 5;
            int offset = ColumnsOffset + column * RecordSize;
            Array.Copy(result, ColumnsOffset + (style == 0 ? 65 : 174) * RecordSize, result, offset, RecordSize);
            BitConverter.TryWriteBytes(result.AsSpan(offset), (ulong)column);
            BitConverter.TryWriteBytes(result.AsSpan(offset + 0x48), 0x3E2 + slot);
            int[] fields = [0x18, 0x30, 0x38];
            for (int i = 0; i < 3; i++) BitConverter.TryWriteBytes(result.AsSpan(offset + fields[i]), address + (uint)functions[i]);
        }
        if (c.Count > CodeSize || list + 88 > Size) throw new InvalidDataException(L.RuntimeErrors.TraitExpansionTooLarge);
        c.Bytes.CopyTo(result); return result;
    }
    private sealed class Code
    {
        internal readonly List<byte> Bytes = [];
        internal int Count => Bytes.Count;
        internal void Emit(string hex) => Bytes.AddRange(Convert.FromHexString(hex));
        internal void Imm64(string op, ulong value) { Emit(op); Bytes.AddRange(BitConverter.GetBytes(value)); }
        internal void Imm32(string op, int value) { Emit(op); Bytes.AddRange(BitConverter.GetBytes(value)); }
        internal void Call(ulong address) { Imm64("49BB", address); Emit("41FFD3"); }
        internal int Branch(string op) { Emit(op); int at = Count; Bytes.AddRange(new byte[4]); return at; }
        internal void Target(int at) { byte[] value = BitConverter.GetBytes(Count - at - 4); for (int i = 0; i < 4; i++) Bytes[at + i] = value[i]; }
        internal void Align() { while ((Count & 15) != 0) Bytes.Add(0xCC); }
    }
}

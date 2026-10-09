using System.Buffers.Binary;
using System.Text;

namespace San14Tenkazu;

// The game's record types, by the class names its exe keeps for C++ run-time type information (RTTI). Each class
// name sits in a type descriptor, a complete-object locator points at the descriptor, and the slot just before the
// class's vtable points at the locator; every record starts with its class's vtable. A patch moves the vtables but
// keeps the names, so they are looked up in the exe when the editor connects. (Checked 2026-10-02 on the Steam
// build: each name led to exactly one vtable, the one the editor had used, e.g. CCityData exe+0x13A6690.)
internal sealed record GameClasses(
    ulong City, ulong County, ulong Officer, ulong Tile, ulong Building, ulong Force, ulong Corps, ulong Unit, ulong World,
    ulong Gate, ulong Kingdom, ulong Item, ulong Personality, ulong Tactic, ulong Policy, ulong Doctrine, ulong Post, ulong Rank, ulong Formation, ulong PolicyEffect = 0, ulong DoctrinePanel = 0)
{
    // What the player reads first when the exe is not one the editor can use, then the detail for a bug report (진단).
    private static InvalidOperationException Unsupported(string diagnosis) => new($"{L.Errors.ExeUnsupported} {diagnosis}");

    // The vtables in the running game (the module loaded at moduleBase), found in the exe file's bytes.
    internal static GameClasses Find(byte[] exe, ulong moduleBase)
    {
        var image = new PeImage(exe);
        Dictionary<uint, List<uint>> locators = image.Locators();
        var vtables = new Dictionary<string, ulong>();
        string[] names =
        [
            "CCityData", "CAreaData", "CPersonData", "CHexData", "CObjectData", "CForceData", "CDistrictData", "CArmyUnitData", "CWorldData",
            "CGateData", "CKingdomData", "CItemData", "CPersonalityData", "CTacticsData", "CPolicyData", "CDoctrineData", "CPostData", "CRankData",
            "CFormationData", "CPolicyEffectData", "CDoctrinePanelData"
        ];
        // The locator of each class (the one for the object itself, at offset 0), then the slots that point at them.
        var wanted = new Dictionary<ulong, string>();
        foreach (string name in names)
        {
            uint descriptor = image.TypeDescriptor(name);
            if (!locators.TryGetValue(descriptor, out List<uint>? found) || found.Count != 1)
                throw Unsupported(L.Errors.ExeTypeInfo(name));
            wanted[image.ImageBase + found[0]] = name;
        }
        foreach ((string name, uint rva) in image.SlotsPointingAt(wanted))
            vtables[name] = moduleBase + rva + 8;
        string[] missing = names.Where(n => !vtables.ContainsKey(n)).ToArray();
        if (missing.Length > 0) throw Unsupported(L.Errors.ExeVtables(string.Join(", ", missing)));
        ulong[] v = names.Select(n => vtables[n]).ToArray();
        return new GameClasses(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15], v[16], v[17], v[18], v[19], v[20]);
    }

    // The parts of a 64-bit PE file the lookup needs: the image base and where each section's bytes lie.
    private sealed class PeImage
    {
        private readonly byte[] file;
        private readonly List<(string Name, uint Rva, uint RawSize, uint RawOffset)> sections = [];

        internal ulong ImageBase { get; }

        internal PeImage(byte[] file)
        {
            this.file = file;
            try
            {
                int header = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x3C));
                if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(header)) != 0x4550) throw new FormatException();
                int count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(header + 6));
                int optional = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(header + 20));
                if (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(header + 24)) != 0x20B) throw new FormatException();   // PE32+
                ImageBase = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(header + 24 + 24));
                for (int i = 0; i < count; i++)
                {
                    int at = header + 24 + optional + i * 40;
                    string name = Encoding.ASCII.GetString(file, at, 8).TrimEnd('\0');
                    sections.Add((name, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 12)),
                        BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 16)), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 20))));
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
            {
                throw Unsupported(L.Errors.ExeFormat);
            }
        }

        // The type descriptor of a class: its decorated name ".?AV<name>@@" sits 0x10 into it.
        internal uint TypeDescriptor(string className)
        {
            byte[] name = Encoding.ASCII.GetBytes($".?AV{className}@@\0");
            int at = file.AsSpan().IndexOf(name);
            if (at < 0x10 || file.AsSpan(at + 1).IndexOf(name) >= 0 || Rva(at - 0x10) is not uint rva)
                throw Unsupported(L.Errors.ExeTypeName(className));
            return rva;
        }

        // Every complete-object locator for an object itself (signature 1, offset 0, pointing back at itself) in
        // .rdata, by the type descriptor it names.
        internal Dictionary<uint, List<uint>> Locators()
        {
            var locators = new Dictionary<uint, List<uint>>();
            (string _, uint rva, uint size, uint raw) = ReadOnlyData();
            ReadOnlySpan<byte> data = file.AsSpan((int)raw, (int)size);
            for (int o = 0; o + 24 <= data.Length; o += 4)
            {
                if (data[o] != 1 || BinaryPrimitives.ReadUInt32LittleEndian(data[o..]) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 4)..]) != 0) continue;
                if (BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 20)..]) != rva + (uint)o) continue;
                uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 12)..]);
                if (!locators.TryGetValue(descriptor, out List<uint>? list)) locators[descriptor] = list = [];
                list.Add(rva + (uint)o);
            }
            return locators;
        }

        // The slots in .rdata holding one of the given addresses (as the image would load at its preferred base),
        // each address's name with the slot's RVA; an address held by more than one slot is left out.
        internal IEnumerable<(string Name, uint Rva)> SlotsPointingAt(Dictionary<ulong, string> addresses)
        {
            (string _, uint rva, uint size, uint raw) = ReadOnlyData();
            ReadOnlySpan<byte> data = file.AsSpan((int)raw, (int)size);
            var found = new Dictionary<string, List<uint>>();
            for (int o = 0; o + 8 <= data.Length; o += 8)
                if (addresses.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(data[o..]), out string? name))
                {
                    if (!found.TryGetValue(name, out List<uint>? list)) found[name] = list = [];
                    list.Add(rva + (uint)o);
                }
            return found.Where(pair => pair.Value.Count == 1).Select(pair => (pair.Key, pair.Value[0]));
        }

        private (string Name, uint Rva, uint RawSize, uint RawOffset) ReadOnlyData() =>
            sections.FirstOrDefault(s => s.Name == ".rdata") is { RawSize: > 0 } section && section.RawOffset + section.RawSize <= file.Length
                ? section : throw Unsupported(L.Errors.ExeRdata);

        private uint? Rva(int offset)
        {
            foreach ((string _, uint rva, uint size, uint raw) in sections)
                if (offset >= raw && offset < raw + size) return rva + (uint)(offset - raw);
            return null;
        }
    }
}

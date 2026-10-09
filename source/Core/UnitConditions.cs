using System.Buffers.Binary;

namespace San14Tenkazu;

// Terrain/fire belong to CHexData, not the unit's three editable ailment timers.
internal readonly record struct UnitTerrainState(int Tile, int Kind, int Fire)
{
    internal static UnitTerrainState? Read(ReadOnlySpan<byte> tiles, int tile)
    {
        if ((uint)tile >= GameMemory.TileCount || tiles.Length < (tile + 1) * GameMemory.TileStride) return null;
        ReadOnlySpan<byte> data = tiles.Slice(tile * GameMemory.TileStride, GameMemory.TileStride);
        return new(tile, data[0x10], BinaryPrimitives.ReadUInt16LittleEndian(data[0x16..]));
    }
}

internal sealed record UnitCondition(string Label, string Value, string Hint, bool Warning = false);

internal static class UnitConditions
{
    internal const int PoisonTerrain = 7, DetoxPersonality = 77, FireImmunityKind = 25;

    internal static IReadOnlyList<UnitCondition> Read(GameSnapshot data, UnitRecord unit)
    {
        UnitTerrainState? terrain = unit.Terrain is { } t && t.Tile == unit.Tile ? t : null;
        OfficerRecord? commander = data.OfficerById.GetValueOrDefault(unit.Commander);
        bool? detox = HasPersonality(data, commander, DetoxPersonality);
        bool? fireproof = HasKind(data, commander, FireImmunityKind);
        return
        [
            new(L.Unit.Poison, terrain is null ? L.Unit.ConditionUnknown : terrain.Value.Kind != PoisonTerrain ? L.Terms.None
                : detox switch { true => L.Unit.PoisonImmune, false => L.Unit.PoisonDamage, _ => L.Unit.PoisonUnknown },
                L.Unit.PoisonHint, terrain is { Kind: PoisonTerrain } && detox != true),
            new(L.Unit.Fire, terrain is null ? L.Unit.ConditionUnknown : terrain.Value.Fire == 0 ? L.Terms.None
                : fireproof switch { true => L.Unit.FireImmune, false => L.Unit.FireDamage, _ => L.Unit.FireUnknown },
                L.Unit.FireHint, terrain is { Fire: > 0 } && fireproof != true),
            // 0x2EA1B0 and personality condition 4 use the connection bit without a kind filter.
            new(L.Unit.Supply, (unit.Data[0x26] & 2) != 0 ? L.Unit.SupplyConnected : L.Unit.SupplyCut,
                L.Unit.SupplyHint, (unit.Data[0x26] & 2) == 0)
        ];
    }

    // 0x16F7F0 flag 4: built-in records use the encoded word when the vector's
    // byte length rounded down to a dword is four; custom records use +0xBF.
    internal static bool? ReadSlotEnabled(PersonalityRecord personality, Func<ulong, int, byte[]?> read)
    {
        byte[] data = personality.Data;
        int flags = data[0xBF];
        if (UsesEncodedFlags(personality))
        {
            byte[]? encoded = read(BitConverter.ToUInt64(data, 0xC8), 2);
            if (encoded is not { Length: 2 }) return null;
            int word = BinaryPrimitives.ReadUInt16LittleEndian(encoded);
            flags = ((word << 1 | word >> 15) ^ personality.Index) & 0xFF;
        }
        return (flags & 16) != 0;
    }

    private static bool UsesEncodedFlags(PersonalityRecord p) => p.Index < 256
        && (unchecked(BitConverter.ToUInt64(p.Data, 0xD0) - BitConverter.ToUInt64(p.Data, 0xC8)) & ~3UL) == 4;

    private static bool? SlotEnabled(PersonalityRecord p) => p.SlotEnabled
        ?? (UsesEncodedFlags(p) ? null : (p.Data[0xBF] & 16) != 0);

    // 0x232D00 -> 0x2305D0: only the first five slots count; IDs 5101..5200
    // bypass flag 4. Detox checks ID 77, not another personality with effect 52.
    internal static bool? HasPersonality(GameSnapshot data, OfficerRecord? officer, int id)
    {
        if (officer is null) return null;
        for (int slot = 0; slot < 5; slot++)
        {
            if (officer[OfficerFields.Personalities[slot]] != id) continue;
            if (officer.Id is >= 5101 and <= 5200) return true;
            PersonalityRecord? p = data.Personalities.FirstOrDefault(p => p.Index == id);
            return p is null ? null : SlotEnabled(p);
        }
        return false;
    }

    // Fire damage at 0x43C2C0 checks effect kind 25 (0x2C5460), irrespective
    // of its activation timer. Wounded soldiers have a separate fire-loss path.
    internal static bool? HasKind(GameSnapshot data, OfficerRecord? officer, int kind)
    {
        if (officer is null) return null;
        bool complete = true;
        for (int slot = 0; slot < 5; slot++)
        {
            int id = (int)officer[OfficerFields.Personalities[slot]];
            if (id is <= 0 or > 355) continue;
            PersonalityRecord? p = data.Personalities.FirstOrDefault(p => p.Index == id);
            if (p is null) { complete = false; continue; }
            if (id >= 256 && p.Kind(0) == 0) continue;
            if (p.Kind(0) != kind && p.Kind(1) != kind) continue;
            bool? enabled = officer.Id is >= 5101 and <= 5200 ? true : SlotEnabled(p);
            if (enabled == true) return true;
            if (enabled is null) complete = false;
        }
        return complete ? false : null;
    }
}

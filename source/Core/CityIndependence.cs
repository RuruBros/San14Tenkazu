using System.Buffers.Binary;

namespace San14Tenkazu;

// Manual independence uses the edit event and, when automatic slots are exhausted,
// its native creation body with an explicit empty ordinary slot. Both use the same
// corps/base lifecycle (Reverse/notes/independence.md), not the rebellion scene.
internal static class CityIndependence
{
    internal sealed record Request(int City, int Governor, int FromForce, int NewForce, ulong CityAddress, ulong GovernorAddress,
        int RequestedSlot = 0);

    private static ForceRecord[] EmptySlots(GameSnapshot data) => data.ForceRecords.Values
        .Where(f => f.Id is >= 1 and <= GameMemory.MaxPlayerForce && f.Data.Length >= 0x120 && f.Ruler == 0)
        .OrderBy(f => f.Id).ToArray();

    // Keep native automatic preference; explicit creation may also use any other
    // empty ordinary slot. Tribal/special slots and nonzero rulers stay excluded.
    internal static int SelectSlot(GameSnapshot data, OfficerRecord governor)
    {
        int automatic = SelectAutomaticSlot(data, governor);
        return automatic > 0 ? automatic : EmptySlots(data).LastOrDefault()?.Id ?? 0;
    }

    // 0x2A9520 with requested slot 0: previous ruler, bloodline, then the highest
    // empty slot in 38..45. A nonzero ruler (including 0xFFFF) is native-valid.
    internal static int SelectAutomaticSlot(GameSnapshot data, OfficerRecord governor)
    {
        ForceRecord[] empty = EmptySlots(data);
        int Previous(ForceRecord f) => BinaryPrimitives.ReadUInt16LittleEndian(f.Data.AsSpan(0x11E));
        if (empty.FirstOrDefault(f => Previous(f) == governor.Id) is { } previous) return previous.Id;
        int bloodline = BinaryPrimitives.ReadUInt16LittleEndian(governor.Data.AsSpan(0xB2));
        if (empty.FirstOrDefault(f => Previous(f) == bloodline) is { } family) return family.Id;
        return empty.Where(f => f.Id is >= 38 and <= 45).Select(f => f.Id).DefaultIfEmpty().Max();
    }

    internal static string? Refusal(GameSnapshot data, int id)
    {
        if (id is < 1 or > GameMemory.CityCount || id >= data.Cities.Length) return L.Independence.NoCity;
        CityRecord city = data.Cities[id];
        int force = data.ForceOfCity(id);
        if (force is < 1 or > GameMemory.MaxPlayerForce || BaseGrants.Ruler(data, force) is not { } ruler)
            return L.Independence.NoForce;
        if (!data.OfficerById.TryGetValue(city.Governor, out OfficerRecord? governor)) return L.Independence.NoGovernor;
        if (governor.Status == 1) return L.Independence.AlreadyRuler;
        if (governor.Status is not (2 or 3) || governor.BaseId != id || data.ForceOfCorps(governor.Corps) != force)
            return L.Independence.InvalidGovernor;
        if (governor[OfficerFields.PlaceField] != id || data.Units.Any(u => u.Commander == governor.Id))
            return L.Independence.OutsideCity;
        // SExecNewForceForEdit excludes IDs 4001..5100, the five special ID blocks,
        // and the native marked-officer flag. Do not bypass those native guards.
        if (governor.Id is >= 4001 and <= 5100 || Formulas.Objects.IsSpecialId(governor.Id)
            || (BinaryPrimitives.ReadUInt16LittleEndian(governor.Data.AsSpan(0x15E)) & 0x4000) != 0)
            return L.Independence.SpecialOfficer;
        if (ruler.BaseId == id || data.CitiesOf(force).Count() < 2) return L.Independence.RulerCity;
        if (SelectSlot(data, governor) == 0) return L.Independence.NoSlot;
        return null;
    }

    internal static Request Prepare(GameSnapshot data, int id)
    {
        if (Refusal(data, id) is { } refused) throw new InvalidOperationException(refused);
        CityRecord city = data.Cities[id];
        OfficerRecord governor = data.OfficerById[city.Governor];
        int slot = SelectSlot(data, governor);
        return new(id, governor.Id, data.ForceOfCity(id), slot, city.Address, governor.Address,
            SelectAutomaticSlot(data, governor) == 0 ? slot : 0);
    }

    // Re-reading success is required: the event returns 1 even when creation fails.
    internal static bool Confirmed(GameSnapshot before, GameSnapshot after, Request request) =>
        after.Cities[request.City].Address == request.CityAddress
        && after.ForceOfCity(request.City) == request.NewForce
        && after.Cities[request.City].Governor == request.Governor
        && BaseGrants.Ruler(after, request.NewForce) is { } ruler && ruler.Id == request.Governor
        && after.ForceRecords.TryGetValue(request.NewForce, out ForceRecord? force) && force.Ruler == request.Governor
        && ruler.Address == request.GovernorAddress && ruler.BaseId == request.City
        && ruler.Corps == after.Cities[request.City].Corps
        && ruler[OfficerFields.PlaceField] == request.City
        && Enumerable.Range(1, GameMemory.CityCount).Where(id => id != request.City)
            .All(id => before.ForceOfCity(id) == after.ForceOfCity(id));
}

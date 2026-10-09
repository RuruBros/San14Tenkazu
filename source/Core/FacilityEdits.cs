namespace San14Tenkazu;

// A facility card writes only its current durability, using the building's original bytes for write/undo checks.
internal static class FacilityEdits
{
    internal static EditPlan Plan(GameSnapshot data, int index, int kind, int tile, int force, long durability)
    {
        if (data.Facility(index, kind, tile, force) is not { Building: { } building } facility)
            return EditPlan.Refuse(L.Facility.Gone);
        FacilityFields.Durability.Encode(durability);
        return new EditPlan(building[FacilityFields.Durability] == durability ? [] :
            [new RecordEdit(RecordKind.Building, building.Address, building.Data, L.Facility.Number(index), [new FieldChange(FacilityFields.Durability, durability)])],
            L.Facility.Number(facility.Index));
    }
}

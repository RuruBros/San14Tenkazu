namespace San14Tenkazu.Formulas;

// UI object management and native list storage stay explicit engine boundaries.
// Availability is queried at its actual use point, after earlier callbacks may free wrappers.
internal sealed record UnitFinalizerEngine(Action<Rec>? NotifyRemoved, Action<Rec> DetachObject,
    Func<bool> MemberListAvailable, Func<bool> NodePoolEnabled, Action<Rec, int> ClearUnitList,
    Action<Rec> RemoveTroopCache);

internal sealed class UnitFinalizer(World w, List<Rec?>? cautions,
    IReadOnlyDictionary<int, List<Rec?>> cautionTroops, IDictionary<ulong, ulong> unitCache,
    UnitFinalizerEngine engine)
{
    // 0x282130: preserve the original troop pointer across unit reset and active-list erasure.
    // There is no unit-validity check; a null pointer alone skips the entire body.
    internal void Apply(Rec? unit)
    {
        if (unit is null) return;
        engine.NotifyRemoved?.Invoke(unit);
        UnitLifecycle.DetachUi(unit, engine.DetachObject);
        UnitLifecycle.CleanupCautions(w, unit, cautions, cautionTroops);
        Rec troop = w.Entry(Tables.Troops, unit.U16(0x58), 500);
        UnitLifecycle.ReplaceCommander(w, unit, engine.MemberListAvailable);
        UnitLifecycle.RemoveUnitCache(unitCache, unit.NativeAddress);
        UnitLifecycle.Reset(unit, engine.NodePoolEnabled(), at => engine.ClearUnitList(unit, at));
        UnitLifecycle.RemoveActiveUnit(w, unit);
        if (UnitLifecycle.TroopMemberCount(w, troop) == 0) UnitLifecycle.DissolveTroop(w, troop, engine.RemoveTroopCache);
    }

    // 0x28E290: pending membership remains visible to every body, then the list is cleared once.
    // Read each payload at its visit so replacements and appended entries remain observable.
    internal void ConsumePending()
    {
        List<Rec?[]> pending = w.List(Lists.EngagedUnits);
        for (int i = 0; i < pending.Count; i++) Apply(pending[i][0]);
        pending.Clear();
    }
}

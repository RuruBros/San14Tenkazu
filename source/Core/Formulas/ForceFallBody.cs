using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// Physical playback storage and presentation remain explicit host requests.
internal sealed record ForceFallHost(Func<bool> NodePoolEnabled, bool SoundSuppressed, bool EventLogSuppressed,
    bool StrategyRefreshAvailable, Action<int, int> BindOfficer, Action<ForceFallBody.Playback> RecordPlayback,
    Action<Rec> ForceRemoved, Action RefreshStrategy, Action<ulong>? ReleaseStorage = null,
    Action<Rec>? RefreshCity = null);

// 0x293790: complete nonterminal NPC path, composed from the verified gameplay bodies.
internal sealed class ForceFallBody(World w, ForceEvents events, HistoryLog history, ForceEventPresentation presentation,
    TileTransfer transfer, TileOwnership ownership, UnitDestruction destruction, OfficerLifecycle officers,
    CorpsLifecycle corps, UnitFinalizer finalizer, IDictionary<ulong, ulong> forceCache,
    IReadOnlyDictionary<string, List<ulong>> forceLists, ForceFallHost host)
{
    internal readonly record struct Playback(int Kind, int Subkind, int Tick, Rec Force, int Ruler, int Append);

    private IEnumerable<Rec> ForceOfficers(Rec force) => w.List(Lists.OfficerList).Select(n => n[0]).OfType<Rec>()
        .Where(p => OfficerRelations.RawForce(w, p) == force.Index);
    private IEnumerable<Rec> ForceCorps(Rec force) => w.List(Lists.CorpsList).Select(n => n[0]).OfType<Rec>()
        .Where(c => Objects.CorpsValid(c) && c.U8(0x10) == force.Index);
    private IEnumerable<Rec> ForceCities(Rec force) => w.List(Lists.CityList).Select(n => n[0]).OfType<Rec>()
        .Where(c => Map.BaseForce(w, c) == force.Index);

    // The terminal paths and remaining gameplay dependencies must fail before any state or host request changes.
    private void RequireNonterminalNpc(Rec defeated)
    {
        if (!Objects.ForceValid(defeated) || defeated.Index >= 46 || Objects.IsPlayerForce(w, defeated))
            throw new NotSupportedException("Player, tribal and invalid force teardown require separate bodies.");
        if (!host.NodePoolEnabled())
            throw new NotSupportedException("Force teardown requires the copied native list pool.");
        if (w.Flag("mgr_2143268") && w.WorldState.I8(0xBC) != -1)
            throw new NotSupportedException("Challenge-manager teardown requires its callback body.");
        bool otherPlayer = w.Table(Tables.Forces).Any(f => Objects.ForceValid(f) && f.Index < 46 &&
            f.Index != defeated.Index && Objects.IsPlayerForce(w, f));
        if (w.Flag("challenge_active") && !otherPlayer || !w.Table(Tables.Forces).Any(f =>
            Objects.ForceValid(f) && f.Index < 46 && f.Index != defeated.Index))
            throw new NotSupportedException("Challenge completion and unification require the terminal state body.");
        if (w.List(Lists.Bases).Select(n => n[0]).OfType<Rec>().Any(b =>
            Map.BaseForce(w, b) == defeated.Index && b.U16(0x4E) is >= 52 and <= 61))
            throw new NotSupportedException("Owned gates require the gate evacuation body.");
        if (w.Table(Tables.Buildings).Any(b => Map.BuildingValid(b) && b.U8(0x13) == defeated.Index &&
            w.Entry(Tables.ObjectKinds, b.U8(0x12), 30).U8(0x2C) is 2 or 4))
            throw new NotSupportedException("Demolition requires its building gameplay body.");
        if (ForceOfficers(defeated).Any(p => p.U8(0xE6) == 2 || (p.U16(0x15E) & 0x4000) != 0))
            throw new NotSupportedException("Corps-leader recursion and marked teardown require their complete composition.");
        if (w.Table(Tables.Tiles).Any(tile => tile.U8(0x14) == defeated.Index && tile.I16(0x1C) < 0))
            throw new InvalidDataException("A negative native unit lookup needs memory outside the copied table.");
        foreach (Rec unit in w.List(Lists.UnitList).Select(n => n[0]).OfType<Rec>())
            if (!Map.Engaged(w, unit) && Objects.UnitForce(w, unit) == defeated.Index &&
                PrisonerRelease.HeldBy(w, unit).Any(p => (p.U16(0x15E) & 0x4000) != 0 || Objects.IsPlayerForce(w, Objects.ForceOf(w, p))))
                throw new NotSupportedException("Marked and player prisoner release require their complete queue/presentation composition.");
        foreach (Rec city in ForceCities(defeated))
        {
            Rec state = w.Entry(Tables.States, city.U8(0xCE), 22);
            if (!Advantage.Holds(w, defeated, state)) continue;
            Rec advantage = w.Entry(Tables.Advantages, state.U8(0x30), 17);
            for (int i = 0; i < 2; i++)
            {
                if (Advantage.EffectKind(advantage, i) != 1) continue;
                Rec force = w.Entry(Tables.Forces, Advantage.EffectVal(advantage, i), 51);
                if (Objects.ForceValid(force) && force.U8(0x194) == 0)
                    throw new NotSupportedException("The regional advantage requires its force-response gameplay body.");
            }
        }
        foreach (Rec force in w.List(Lists.ForceList).Select(n => n[0]).OfType<Rec>())
        {
            int slot = ForceRelations.DiplomaticSlot(force, (uint)defeated.Index, false);
            if (force != defeated && Objects.IsPlayerForce(w, force) && slot >= 0 &&
                Objects.Valid(w, ForceRelations.DiplomaticOfficer(w, force, (uint)slot)))
                throw new NotSupportedException("Player diplomatic recall requires its history and speaker body.");
        }
        if (BinaryPrimitives.ReadUInt64LittleEndian(defeated.Bytes.AsSpan(0x1A0)) != 0 && host.ReleaseStorage is null)
            throw new NotSupportedException("Owned force storage requires its release host.");
        foreach (int at in new[] { 0x250, 0x260 })
            if (BinaryPrimitives.ReadUInt64LittleEndian(defeated.Bytes.AsSpan(at)) != 0 &&
                !forceLists.ContainsKey($"force:{defeated.Index}:{at:x}"))
                throw new InvalidDataException("The copied force list is absent.");
    }

    private void TransferTerritory(Rec defeated, int suppressPresentation)
    {
        var buildings = new List<Rec>();
        foreach (Rec tile in w.Table(Tables.Tiles))
        {
            if (tile.U8(0x14) != defeated.Index) continue;
            Rec building = Reach.Building(w, tile), destination = w.Table(Tables.Forces)[0];
            if (Map.BuildingValid(building))
            {
                if (building.U8(0x13) == defeated.Index)
                {
                    if (!buildings.Contains(building)) buildings.Add(building);
                }
                else destination = w.Entry(Tables.Forces, building.U8(0x13), 51);
            }
            int unitNumber = tile.I16(0x1C);
            if (unitNumber < 0) throw new InvalidDataException("A negative native unit lookup needs memory outside the copied table.");
            Rec unit = w.Entry(Tables.Units, unitNumber, 500);
            if (Objects.UnitValid(unit) && !Objects.ForceValid(destination))
                destination = w.Entry(Tables.Forces, Objects.UnitForce(w, unit), 51);
            int force = Objects.ForceValid(destination) ? destination.Index : 0;
            transfer.Apply(tile, force, 0, 1, suppressPresentation == 0 ? 1 : 0, 0);
            ownership.Apply(tile, force);
        }
        foreach (Rec building in buildings) building.Bytes[0x13] = 0;
    }

    private void RecordFall(Rec defeated)
    {
        presentation.Reset();
        presentation.BindForce(defeated.Index, 0);
        string primary = presentation.Message(0x3200), secondary = "";
        Rec player = w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 51);
        if (Objects.ForceValid(player))
        {
            presentation.Reset();
            host.BindOfficer(5012, 0);
            host.BindOfficer(player.U16(0x10), 1);
            presentation.BindForce(defeated.Index, 0);
            secondary = presentation.Message(0x533D);
        }
        history.Add(new(5, 0, 0, 1, w.WorldState.I32(0x450), primary, secondary, 5012, Immediate: 1));
        if (!host.EventLogSuppressed)
            host.RecordPlayback(new(3, 1, w.WorldState.I32(0x450), defeated, defeated.U16(0x10), 1));
    }

    // 0x293790 keeps status6 and the captivity location, clearing the defeated original corps and service fields.
    private void ClearPrisonerOrigin(Rec person)
    {
        CorpsLifecycle.SetOfficerCorps(w, person, 0);
        person.Bytes[0x165] = 0;
        person.Bytes[0xE8] = (byte)((person.U16(0x15E) & 0x2000) != 0 ? 255 : 0);
        person.Bytes[0x1BC] = person.Bytes[0x1C4] = 0;
        Rec[] rules = w.Table(OfficerLifecycle.LoyaltyRules);
        int initial = rules[0].I16(0x2C), lower = rules[Objects.OptionAt4C4(w) == 1 ? 2 : 1].I16(0x2C);
        int upper = rules[6].I16(0x2C);
        BinaryPrimitives.WriteInt16LittleEndian(person.Bytes.AsSpan(0x1BE), (short)(lower > initial ? lower : Math.Min(initial, upper)));
        person.Bytes.AsSpan(0x1C0, 4).Clear();
    }

    internal int ApplyNonterminalNpc(Rec defeated, Rec? victor, int mode = 0, int suppressPresentation = 0)
    {
        RequireNonterminalNpc(defeated);
        if (suppressPresentation == 0 && !(w.Flag("mgr_2143268") && w.WorldState.I8(0xBC) != -1) && mode == 0)
        {
            presentation.Reset();
            presentation.BindForce(defeated.Index, 0);
            if (!host.SoundSuppressed) presentation.Sound(8);
            presentation.Show(presentation.Message(0x3201), null);
        }
        victor ??= w.Table(Tables.Forces)[0];
        events.LeaderDefeated(defeated);
        events.TargetDefeated(defeated, victor);
        defeated.Bytes[0x178] &= 0xFE;
        ForceFall.UpdateScenarioFlags(w, defeated, victor);
        TransferTerritory(defeated, suppressPresentation);
        if (mode == 0) RecordFall(defeated);
        // Keep the native unit-list order; pending units are excluded by the iterator.
        foreach (Rec unit in w.List(Lists.UnitList).Select(n => n[0]).OfType<Rec>())
            if (!Map.Engaged(w, unit) && Objects.UnitForce(w, unit) == defeated.Index)
                destruction.ApplyUnopposedRemoval(unit);
        foreach (Rec force in w.List(Lists.ForceList).Select(n => n[0]).OfType<Rec>())
            ForceRelations.ClearDiplomaticAppointment(w, force, defeated);
        // Selector4 accepts every status and compares the raw corps force byte without person validity.
        foreach (Rec person in ForceOfficers(defeated))
        {
            ForceFall.TransferItemsToRuler(w, person, victor);
            if (person.U8(0xE6) == 6) ClearPrisonerOrigin(person);
            else if (person.U8(0xE6) is >= 1 and <= 4) officers.ApplyFree(person);
        }
        foreach (Rec corpsRecord in ForceCorps(defeated).ToArray()) corps.Merge(corpsRecord, null, mode);
        ForceFall.RemoveForceCache(forceCache, defeated.NativeAddress);
        foreach (Rec corpsRecord in w.List(Lists.CorpsList).Select(n => n[0]).OfType<Rec>())
            if (corpsRecord.U8(0x19) == 7 && corpsRecord.U8(0x18) == defeated.Index) CorpsLifecycle.ResetOrders(corpsRecord);
        ForceFall.RemoveFromForceList(w, defeated);
        ForceReset.Apply(w, defeated, host.NodePoolEnabled(), at => forceLists[$"force:{defeated.Index}:{at:x}"].Clear(),
            host.RefreshCity, host.ReleaseStorage);
        host.ForceRemoved(defeated);
        finalizer.ConsumePending();
        ForceFall.UpdateStandings(w);
        if (host.StrategyRefreshAvailable) host.RefreshStrategy();
        return 0;
    }
}

using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

internal sealed record RulerSuccessionPresentation(Action Reset, Action<int, int> BindForce,
    Action<int, int> BindOfficer, Func<int, string> Message, Action<int> BeginScript,
    Action EndScript, Action<int, string> Speaker, Action<HistoryLog.Entry> TraceHistory);

// Logical copies of the six map managers and the final path-cache manager, never live process memory.
internal sealed class RulerSuccessionEngine(IReadOnlyList<Rec?> mapManagers, Rec pathCache,
    Action? traceMapRefresh = null, Action? tracePathReset = null)
{
    internal void RequireInputs()
    {
        if (mapManagers.Count != 6 || mapManagers.Any(m => m is not null && m.Bytes.Length < 0xA4) || pathCache.Bytes.Length < 0x28)
            throw new InvalidDataException("Missing copied succession map or path-cache input.");
    }

    internal void RefreshMaps()
    {
        foreach (Rec? manager in mapManagers)
            if (manager is not null) BinaryPrimitives.WriteInt32LittleEndian(manager.Bytes.AsSpan(0xA0), 1);
        traceMapRefresh?.Invoke();
    }

    // 0x17E5E0 clears two qwords before setting the reset flag.
    internal void ResetPathCache()
    {
        pathCache.Bytes.AsSpan(0x14, 16).Clear();
        BinaryPrimitives.WriteInt32LittleEndian(pathCache.Bytes.AsSpan(0x24), 1);
        tracePathReset?.Invoke();
    }
}

// 0x28C180: surviving NPC succession with same-corps candidates and no rebellion.
// The complete native proof uses the explicit UI/history suppression input; enabled UI remains separate.
internal sealed class RulerSuccessionBody(World w, CorpsLifecycle corps, CommandStorage commands,
    RulerPolicy policy, HistoryLog history, RulerSuccessionPresentation presentation, RulerSuccessionEngine engine,
    bool uiHistorySuppressed, OfficerLifecycle? officers = null)
{
    internal const string DepartureLoyaltyLimit = "departure_loyalty_limit";

    internal Rec ApplyNpcWithoutRebellion(Rec dying)
    {
        if (!uiHistorySuppressed || !history.Suppressed)
            throw new NotSupportedException("Enabled UI or history requires a separately verified succession body.");
        policy.RequireInputs(); engine.RequireInputs();
        Rec force = Objects.ForceOf(w, dying);
        var selection = RulerSuccession.SelectBeforeSuccession(w, dying);
        Rec successor = selection.Officer ?? throw new NotSupportedException("This body requires a selected surviving NPC successor.");
        int cities = Capture.CityCount(w, force);
        Rec[] roster = w.List(Lists.OfficerList).Select(n => n[0] ?? throw new InvalidDataException("Null force officer node."))
            .Where(p => OfficerRelations.RawForce(w, p) == force.Index).ToArray();
        Rec[] forceCorps = w.List(Lists.CorpsList).Select(n => n[0]).OfType<Rec>()
            .Where(c => Objects.CorpsValid(c) && c.U8(0x10) == force.Index).ToArray();
        if (!Objects.PersonValid(dying) || dying.U8(0xE6) != 1 || Objects.IsPlayerForce(w, force) ||
            force.Index is < 1 or > 45 || cities is < 1 or > 2 || dying.U8(0xE0) != successor.U8(0xE0) ||
            forceCorps.Length != 1 || forceCorps[0].Index != dying.U8(0xE0) ||
            roster.Any(p => (p.U16(0x15E) & 0x4000) != 0) ||
            commands.FindOfficer(successor.U16(0x10)) is { Kind: 17 or 18 })
            throw new NotSupportedException("Rebellion, marked handling, corps merging or player succession requires a separate body.");
        Rec[] departures = roster.Where(p => p != dying && p != successor && OfficerDeparture.TestForRuler(w, p, successor,
            p.U8(0xE6), RulerChange.PreviewLoyalty(w, p, dying, successor))).ToArray();
        if (departures.Length != 0 && officers is null)
            throw new NotSupportedException("Missing officer lifecycle for succession departure.");
        foreach (Rec person in departures) officers!.RequireSuccessionDeparture(person, force, successor);
        Rec home = Map.Base(w, successor.U16(0xE2)) ?? throw new InvalidDataException("Missing successor home base.");
        int corpsIndex = dying.U8(0xE0), rulerId = successor.U16(0x10), dyingId = dying.U16(0x10);

        presentation.BeginScript(0x1F);
        presentation.Reset();
        presentation.BindOfficer(rulerId, 0); presentation.BindOfficer(dyingId, 1);
        presentation.Speaker(rulerId, presentation.Message(0x2A3E));
        presentation.EndScript();
        presentation.Reset();
        presentation.BindOfficer(rulerId, 0); presentation.BindOfficer(dyingId, 1);
        presentation.BindForce(force.Index, 0);
        string primary = presentation.Message(0x2A88);
        presentation.Reset();
        presentation.BindOfficer(5012, 0);
        Rec player = w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 51);
        presentation.BindOfficer(Objects.ForceValid(player) ? player.U16(0x10) : 5014, 1);
        presentation.BindOfficer(rulerId, 2); presentation.BindOfficer(dyingId, 3);
        string secondary = presentation.Message(0x533E);
        var entry = new HistoryLog.Entry(1, corpsIndex, Map.BaseHex(w, home), 1, w.WorldState.I32(0x450),
            primary, secondary, 5012, 1, 1);
        presentation.TraceHistory(entry); history.Add(entry);

        List<Rec?> rebuilt = [];
        RulerChange.ApplyWithoutPolicyReset(w, corps, dying, successor, rebuilt,
            new RulerChangeHost(RefreshPresentation: engine.RefreshMaps));
        // 0x2E54E0 returns before candidate construction for fewer than three cities.
        foreach (Rec? person in rebuilt)
            if (person is not null && person.U16(0x10) != dyingId && OfficerDeparture.Test(w, person))
                officers!.ApplyFree(person, successor);
        foreach (Rec current in forceCorps) corps.CheckSurvivingForce(current);
        policy.Apply(force, successor, dying, true);
        engine.ResetPathCache();
        return successor;
    }
}

namespace San14Tenkazu.Formulas;

// Small projections at explicit capture-engine boundaries; these do not implement the engine bodies.
internal static class CaptureEngine
{
    internal readonly record struct TribeConquest(Rec Force, uint Flags, int Suzerain);

    // 0x284010: after the player-only entry guards, set the conquest bit and the tribe's suzerain.
    // Dialog/history, army/officer changes and occupation remain native engine work.
    internal static TribeConquest? PlayerTribeConquest(World w, Rec? target, Rec? attacker)
    {
        if (!Objects.Valid(w, target) || !Objects.Valid(w, attacker) || target!.Table != Tables.Cities) return null;
        Rec tribe = w.Entry(Tables.Forces, Map.BaseForce(w, target), 0x33);
        if (!Objects.ForceValid(tribe) || (uint)(tribe.Index - 46) >= 5) return null;
        Rec force = w.Entry(Tables.Forces, Objects.UnitForce(w, attacker!), 0x33);
        if (!Objects.ForceValid(force) || !Objects.IsPlayerForce(w, force)) return null;
        uint flags = unchecked((uint)w.WorldState.I32(0x169C)) | 1u << (tribe.Index - 46 + 5);
        return new(tribe, flags, Math.Clamp(force.Index, 0, 0x33));
    }

    // 0x2939B3..0x2939DD: apply this only to records yielded by the removed force's base iterator.
    // The actual corps/unit zero records are passed by pointer, not null; the complete force-removal body is external.
    internal static DestructionQueue.EngineCall? ForceRemovalGateRequest(World w, Rec target)
    {
        if ((uint)(target.U16(0x4E) - 52) > 9) return null;
        return new(DestructionQueue.Handler.BaseCaptured, target, w.Table(Tables.Units)[0],
            AttackerCorps: w.Table(Tables.Corps)[0], CaptureMode: 1);
    }
}

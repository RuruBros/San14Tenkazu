namespace San14Tenkazu.Formulas;

// 0x31EAB0: append or deduplicate an engine destruction/capture request.
// A false return tells the caller to enter its immediate engine processing path.
internal static class DestructionQueue
{
    internal const int Capacity = 128;

    internal enum Handler { UnitDestroyed, BaseCaptured, TribeCaptured }

    // The engine boundary remains explicit: capture, destruction and presentation are not formulas here.
    internal readonly record struct EngineCall(Handler Kind, Rec Target, Rec? AttackerUnit,
        Rec? AttackerBase = null, Rec? AttackerCorps = null, int Mode = 0, int Reason = 0,
        int CaptureMode = 0, bool Suzerain = false);

    // 0x44F060: resolve each request against the current state, then 0x325AF0 clears the initialized queue.
    // A handler may change later records or append requests; inspect the next node only after it returns.
    internal static void Consume(World w, LinkedList<TacticEffects.PendingDestruction> queue, Action<EngineCall> process)
    {
        var node = queue.First;
        while (node is not null)
        {
            var request = node.Value;
            if (Objects.Valid(w, request.Unit))
                process(new(Handler.UnitDestroyed, request.Unit!, request.AttackerUnit, request.AttackerBase,
                    Mode: request.Mode, Reason: request.Reason));
            else if (Objects.Valid(w, request.Base))
            {
                Rec? corps = null;
                if (Objects.Valid(w, request.AttackerUnit))
                {
                    // 0x22B980 returns the commander's corps even when that corps is invalid.
                    Rec? commander = w.OfficerById(request.AttackerUnit!.U16(0x12));
                    corps = w.Entry(Tables.Corps, Objects.Valid(w, commander) ? commander!.U8(0xE0) : 0, 0x33);
                }
                Rec force = w.Entry(Tables.Forces, Map.BaseForce(w, request.Base!), 0x33);
                if (Objects.Valid(w, force) && (uint)(force.Index - 46) < 5)
                    process(new(Handler.TribeCaptured, request.Base!, request.AttackerUnit,
                        Suzerain: force.U8(0x194) == Objects.UnitForce(w, request.AttackerUnit!)));
                else
                    process(new(Handler.BaseCaptured, request.Base!, request.AttackerUnit,
                        AttackerCorps: corps, CaptureMode: request.CaptureMode));
            }
            node = node.Next;
        }
        // A failed handler never reaches the native clear call either.
        queue.Clear();
    }

    internal static bool TryAdd(World w, List<TacticEffects.PendingDestruction> queue, TacticEffects.PendingDestruction request)
    {
        // The capacity check precedes duplicate detection in the native function.
        if (queue.Count >= Capacity) return false;
        foreach (var existing in queue)
        {
            if (Objects.Valid(w, existing.Unit) && Objects.Valid(w, request.Unit))
            {
                if (existing.Unit!.Index == request.Unit!.Index) return true;
            }
            else if (Objects.Valid(w, existing.Base) && Objects.Valid(w, request.Base)
                && existing.Base!.U16(0x4E) == request.Base!.U16(0x4E)) return true;
        }
        queue.Add(request);
        return true;
    }
}

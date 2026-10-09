using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// 0x172E20: select the main tactic, construct linked commands, apply their slots, and reset source pools.
// Engine presentation and subsequent consumption of deferred damage/destruction requests belong to the caller.
internal static class TacticFlow
{
    [Rva(0x1A48A6C)] internal const int TargetScore = 5;
    [Rva(0x1A48A78)] internal const int TerrainScore = 5;
    [Rva(0x1A48A84)] internal const int NeighborScore = 5;
    [Rva(0x1A48A90)] internal const int SpecialScore = 9;
    [Rva(0x1A4807C)] internal const int TacticPoolMax = 100;
    [Rva(0x1A48088)] internal const int SupplyPoolMax = 150;
    [Rva(0x1A48094)] internal const int MarchPoolMax = 200;
    [Rva(0x1A48A54)] internal const int Formation1Delay = 8;
    [Rva(0x1A48A60)] internal const int Formation2Delay = -8;
    [Rva(0x1A489F8)] internal const int Formation14Delay = -8;
    [Rva(0x1A48C20)] internal const int ForceDelayReduction = 4;

    internal readonly record struct Command(int Kind, int Number, Rec Tactic, List<Rec>[] Targets);
    internal readonly record struct Resolution(Rec? Main, List<Rec> Candidates, List<Command> Commands,
        List<TacticEffects.DispatchApplication> Applications, Dictionary<Rec, int> UnitDamage,
        Dictionary<Rec, int> BaseDamage, List<TacticEffects.PendingDestruction> Pending, bool Destroyed);

    private static Tactic.Side ResolveSide(World w, Engagement.Side side) => side.Kind switch
    {
        27 => new(w.Entry(Tables.Units, side.Index, 500), null),
        5 => new(null, Map.Base(w, (uint)side.Index <= 51 ? side.Index : 0)),
        6 => new(null, Map.Base(w, (uint)(side.Index - 1) < 10 ? side.Index + 51 : 0)),
        _ => default,
    };

    private static bool Valid(World w, Tactic.Side side) => Objects.Valid(w, side.Unit) != Objects.Valid(w, side.Base);

    // 0x16CA30/0x16C950: cities require flag 0 on unit tactics; gates do not.
    internal static List<Rec> Candidates(World w, Tactic.Side source, int targetKind)
    {
        if (Objects.Valid(w, source.Unit)) return UnitTactics.Candidates(w, source.Unit!)
            .Where(t => (targetKind != 5 || TacticEffects.Flag(t, 0)) && TacticLink.Allowed(w, t)).ToList();
        if (Objects.Valid(w, source.Base) && w.OfficerById(source.Base!.U16(0x2E)) is { } governor && Objects.Valid(w, governor))
        {
            Rec tactic = w.Entry(Tables.Tactics, source.Base.U8(0x48), 200);
            if (Objects.Valid(w, tactic)) return [tactic];
        }
        return [];
    }

    // The terrain test is the native tile byte +0x10 in {16,17}, independently of a tile's object kind.
    private static bool Terrain(Rec tile) => (byte)(tile.U8(0x10) - 16) < 2;
    internal static int Score(World w, Rec tactic, List<Rec>[] targets, Engagement.Side source)
    {
        int score = unchecked((targets[0].Count + targets[1].Count) * TargetScore);
        if (source.Kind != 27) return score;
        bool bonus = false;
        if (TacticEffects.Flag(tactic, 0))
            for (int slot = 0; slot < 2; slot++)
                if (TacticEffects.Kind(tactic, slot) is 1 or 17 && targets[slot].Any(Terrain)) bonus = true;
        if (!bonus)
            for (int slot = 0; slot < 2; slot++)
                if (TacticEffects.Kind(tactic, slot) is 7 or 9 or 18)
                    foreach (Rec tile in targets[slot])
                        if (Map.RingAt(tile.Index, 1).Any(h => Terrain(w.Table(Tables.Tiles)[h])
                            && Reach.Hostile(w, w.Table(Tables.Tiles)[h].U8(0x14), source.Force))) bonus = true;
        if (bonus) score = unchecked(score + TerrainScore);
        bonus = false;
        for (int slot = 0; slot < 2; slot++)
            if (TacticEffects.Kind(tactic, slot) is 12 or 14)
                foreach (Rec tile in targets[slot])
                {
                    Rec? unit = Reach.UnitOnHex(tile) ? w.Entry(Tables.Units, tile.U16(0x1C), 500) : null;
                    if (unit is not null && Reach.Hostile(w, Objects.UnitForce(w, unit), source.Force)
                        && Map.RingAt(tile.Index, 1).Any(h => Terrain(w.Table(Tables.Tiles)[h])
                            && w.Table(Tables.Tiles)[h].U8(0x14) == source.Force)) bonus = true;
                }
        if (bonus) score = unchecked(score + NeighborScore);
        if (Tactic.Special(tactic)) score = unchecked(score + SpecialScore);
        return score;
    }

    internal static Resolution Apply(World w, Engagement.Side source, Engagement.Side target)
    {
        var candidates = new List<Rec>(); var commands = new List<Command>();
        var applications = new List<TacticEffects.DispatchApplication>();
        var units = new Dictionary<Rec, int>(); var bases = new Dictionary<Rec, int>();
        var pending = new List<TacticEffects.PendingDestruction>();
        Resolution Empty() => new(null, candidates, commands, applications, units, bases, pending, false);
        Tactic.Side a = ResolveSide(w, source), b = ResolveSide(w, target);
        if (!Valid(w, a) || !Valid(w, b)) return Empty();
        Rec from = w.Entry(Tables.Tiles, source.Hex, 48399), to = w.Entry(Tables.Tiles, target.Hex, 48399);
        int force = Objects.Valid(w, a.Unit) ? Objects.UnitForce(w, a.Unit!) : Map.BaseForce(w, a.Base!);
        var lists = new Dictionary<Rec, List<Rec>[]>();
        foreach (Rec tactic in Candidates(w, a, target.Kind))
        {
            List<Rec>[] targets = TacticEffects.Targets(w, tactic, from, to, force);
            if (targets.All(t => t.Count == 0)) continue;
            candidates.Add(tactic); lists[tactic] = targets;
        }
        int difficulty = w.Settings.I32(w.Settings.I32(0xE58) == 0 ? 0x134 : 0x28);
        if (difficulty >= 4) candidates = candidates.OrderByDescending(t => Score(w, t, lists[t], source)).ToList();
        else if (candidates.Count > 1)
        {
            var remaining = candidates; candidates = [];
            for (int iteration = 0; remaining.Count != 0; iteration++)
            {
                int index = Wounded.Seeded(w, remaining.Count, 1, source.Index, iteration, w.WorldState.I8(0x38), 0);
                candidates.Add(remaining[index]); remaining.RemoveAt(index);
            }
        }
        Rec? main = Tactic.Fire(w, a, b, candidates);
        if (!Objects.Valid(w, main)) return Empty();
        commands.Add(new(source.Kind, source.Index, main!, lists[main!]));
        if (Objects.Valid(w, a.Unit))
        {
            Rec linkTarget = Objects.Valid(w, b.Unit) ? w.Entry(Tables.Tiles, b.Unit!.U16(0x2A), 48399) : Map.BuildingTile(w, b.Base!);
            foreach (var linked in TacticLink.Select(w, w.OfficerById(a.Unit!.U16(0x12)), linkTarget, 0, TacticLink.CandidateUnits(w, a.Unit)))
                commands.Add(new(27, linked.Unit.Index, linked.Tactic, linked.Targets));
        }
        Rec sourceForce = w.Entry(Tables.Forces, force, 51);
        int reduction = Objects.Valid(w, sourceForce) && main!.U8(0x40) is 5 or 6
            ? Objects.Doctrine(w, sourceForce, main.U8(0x40) == 5 ? 36 : 37) : 0;
        var linkedResults = new Rec?[10];
        List<Rec?[]> queue = w.List(Lists.Engagements);
        int initialCount = queue.Count, delay = 0;
        bool destroyed = false;
        try
        {
            foreach (Command command in commands)
            {
                var result = TacticEffects.Dispatch(w, command.Tactic, command.Kind, command.Number, a.Unit, linkedResults,
                    command.Targets, commands.Count - 1, to.Index, false);
                applications.Add(result);
                if (result.ResultIndex > 0) linkedResults[result.ResultIndex - 1] = command.Tactic;
                foreach (var e in result.UnitDamage) units[e.Key] = unchecked(units.GetValueOrDefault(e.Key) + e.Value);
                foreach (var e in result.BaseDamage) bases[e.Key] = unchecked(bases.GetValueOrDefault(e.Key) + e.Value);
                foreach (var request in result.Pending)
                {
                    pending.Add(request); queue.Add([request.Unit, request.Base, request.AttackerUnit]);
                }
                destroyed |= result.Destroyed;
                delay = unchecked(delay + command.Tactic.U16(0x42));
            }
            int average = Objects.Trunc((float)delay / commands.Count);
            foreach (Command command in commands)
                if (command.Kind is 5 or 6) BinaryPrimitives.WriteUInt16LittleEndian(a.Base!.Bytes.AsSpan(0x4C), (ushort)Math.Clamp(average, 0, TacticPoolMax));
                else if (command.Kind == 27)
                {
                    Rec unit = w.Entry(Tables.Units, command.Number, 500);
                    Rec tile = w.Entry(Tables.Tiles, unit.U16(0x2A), 48399), terrain = w.Entry(Tables.Terrains, tile.U8(0x10), 30);
                    int formation = (terrain.U8(0x2C) & 2) != 0 ? unit.U8(0x1C) : unit.U8(0x1B);
                    int offset = formation switch { 1 => Formation1Delay, 2 => Formation2Delay, 14 => Formation14Delay, _ => 0 };
                    Rec owner = w.Entry(Tables.Forces, Objects.UnitForce(w, unit), 51);
                    int forceReduction = (owner.U8(0x193) & 0x40) != 0 ? ForceDelayReduction : 0;
                    unit.Bytes[0x20] = (byte)Math.Clamp(unchecked(offset - forceReduction - reduction + average), 0, TacticPoolMax);
                    unit.Bytes[0x21] = SupplyPoolMax; unit.Bytes[0x22] = MarchPoolMax;
                }
        }
        finally { queue.RemoveRange(initialCount, queue.Count - initialCount); }
        return new(main, candidates, commands, applications, units, bases, pending, destroyed);
    }
}

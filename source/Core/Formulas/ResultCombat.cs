namespace San14Tenkazu.Formulas;

// 0x3789E0 reason 0: morale request/setter projection and the positive-morale injury decision.
// The notification prefix is ResultEffects. History/UI and route-engine bodies stay external.
internal static class ResultCombat
{
    [Rva(0x1A49390)] internal const int InjuryLimit = 75;

    internal readonly record struct Result(Rec TargetUnit, Rec SourceUnit, int Delta, int SourceHex,
        int SetterValue, int Morale, bool PlayerNotice, bool InjuryRequested, Injury.Result Injury);

    internal readonly record struct MoraleRequest(Rec TargetUnit, Rec SourceUnit, int Delta, int SourceHex,
        int SetterValue, int Morale, bool PlayerNotice);

    internal static Result? ComputePositiveMorale(World w, Rec? first, Rec? second)
    {
        if (ComputeMoraleRequest(w, first, second) is not { } request) return null;
        if (request.Morale == 0) throw new NotSupportedException("Zero morale needs the route engine before the caller's injury decision.");
        Rec force = w.Entry(Tables.Forces, Objects.UnitForce(w, request.TargetUnit), 0x33);
        int cap = BattlePower.MaxMorale(w, force);
        bool injury = Math.Min(request.Morale, cap) > 0 && second!.U8(0x161) < 3 && second.U8(0xF1) < InjuryLimit;
        Injury.Result result = injury ? Injury.Apply(w, second!, 3, first) : new(second!.U8(0x161), false);
        return new(request.TargetUnit, request.SourceUnit, request.Delta, request.SourceHex,
            request.SetterValue, request.Morale, request.PlayerNotice, injury, result);
    }

    internal static MoraleRequest? ComputeMoraleRequest(World w, Rec? first, Rec? second)
    {
        if (!Objects.Valid(w, first) || !Objects.Valid(w, second)) return null;
        Rec source = Objects.UnitOf(w, first!), target = Objects.UnitOf(w, second!);
        if (!Objects.UnitValid(source) || !Objects.UnitValid(target)) return null;
        Rec sourceForce = Objects.ForceOf(w, first!), targetForce = Objects.ForceOf(w, second!);
        bool player = Objects.ForceValid(sourceForce) && Objects.IsPlayerForce(w, sourceForce)
            || Objects.ForceValid(targetForce) && Objects.IsPlayerForce(w, targetForce);
        int bonus = 0;
        if (Personality.HasPersonality(w, first!, 0x3D)) bonus = 5;
        else if (Personality.HasPersonality(w, first!, 0x3E)) bonus = 10;
        else if (Personality.HasPersonality(w, first!, 0x90))
        {
            // 0x229E30 is called only with personality 0x90 here. In these modes its age is always 80.
            int gameMode = w.WorldState.I32(0x44);
            int age = gameMode == 2 || gameMode == 3 && w.WorldState.I32(0x50) == 0 ? 80
                : Math.Max(0, w.WorldState.I16(0x34) - first!.U16(0xAC) + 1);
            bonus = age > 60 ? 10 : 5;
        }
        int strength = Math.Clamp(OfficerAbility.Get(w, first!, 1, OfficerAbility.All) / 10, 0, 10);
        Rec force = w.Entry(Tables.Forces, Objects.UnitForce(w, target), 0x33);
        int difficulty = w.Settings.I32(w.Settings.I32(0xE58) == 0 ? 0x134 : 0x28);
        int reduction = Objects.ForceValid(force) && Objects.IsPlayerForce(w, force)
            ? difficulty == 0 ? 20 : difficulty == 1 ? 10 : 0 : 0;
        int loss = Math.Clamp(ResultEffects.MoraleThreshold - reduction + strength + bonus, 30, 70);
        // Doctrine is subtracted after the 30..70 clamp, without a second clamp.
        if (Objects.ForceValid(force)) loss -= Objects.Doctrine(w, force, 0x2A);
        int delta = -loss, adjusted = delta;
        if (delta < 0)
        {
            int sum = Personality.UnitSums(w, target, false)[13];
            if (sum != 0)
            {
                float value = (float)unchecked(-delta * sum * Engagement.MoraleKind13Factor) / 100f;
                if (Objects.Trunc(value) == 0) value = value >= 0f ? 1f : -1f;
                adjusted = unchecked(adjusted + Objects.Trunc(value));
            }
        }
        int cap = BattlePower.MaxMorale(w, force);
        int next = unchecked(Math.Min(target.U8(0x1A), cap) + adjusted);
        int setter = next < 0 ? 0 : Math.Min(next, cap);
        int morale = unchecked((byte)(setter < 0 ? 0 : Math.Min(setter, cap)));
        return new(target, source, delta, source.U16(0x2A) <= 0xBD0F ? source.U16(0x2A) : 0,
            setter, morale, player);
    }

    // 0x270550, fifth argument 0: a valid supplied hex bypasses the nearest-enemy search.
    // The caller executes this explicit engine boundary before checking morale/health for injury.
    internal static DestructionQueue.EngineCall? ImmediateRouteFromSourceHex(World w, MoraleRequest request)
    {
        if (request.SetterValue != 0) return null;
        Rec tile = w.Entry(Tables.Tiles, request.SourceHex, 0xBD0F);
        Rec building = Reach.Building(w, tile);
        Rec? attacker = null, attackerBase = null;
        if (Map.BuildingValid(building) && (Reach.IsCity(building) || Reach.IsGate(building)))
            attackerBase = Map.Base(w, building.Index);
        else if (Reach.UnitOnHex(tile))
            attacker = w.Entry(Tables.Units, tile.I16(0x1C), 500);
        return new(DestructionQueue.Handler.UnitDestroyed, request.TargetUnit, attacker, attackerBase,
            Mode: 1, Reason: 0);
    }
}
